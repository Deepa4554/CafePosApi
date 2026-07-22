using System.Text.Json;
using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

[ApiController]
[Route("api/approvals")]
[Authorize(Policy = Policies.RequirePlus)]
public class ApprovalsController(CafePosDbContext db, IAuditService audit, ITaxRateCache taxRateCache, ITenantContext tenantContext) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<ApprovalDto>> List([FromQuery] ApprovalStatus? status, [FromQuery] int? assignedToId)
    {
        var query = db.Approvals.AsQueryable();
        if (status is not null) query = query.Where(a => a.Status == status);
        if (assignedToId is not null) query = query.Where(a => a.AssignedToId == assignedToId);

        var items = await query.OrderByDescending(a => a.CreatedAt).ToListAsync();
        return items.Select(ApprovalDto.From);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApprovalDto>> Get(int id)
    {
        var req = await db.Approvals.FindAsync(id);
        return req is null ? NotFound() : ApprovalDto.From(req);
    }

    [HttpPost]
    public async Task<ActionResult<ApprovalDto>> Submit(SubmitApprovalRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            throw new ApiValidationException("Title is required.");

        var currentUserId = CurrentUserId();
        var request = new ApprovalRequest
        {
            Type = req.Type,
            RequestedById = currentUserId ?? 0,
            AssignedToId = req.AssignedToId,
            Title = req.Title,
            Description = req.Description,
            Amount = req.Amount,
            Currency = req.Currency,
            Level = req.Level,
        };
        db.Approvals.Add(request);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = request.Id }, ApprovalDto.From(request));
    }

    /// <summary>Refund/Discount/Expense requests here only ever exist because a Manager hit
    /// ApprovalThresholds and got held (Owner-initiated ones bypass entirely and never reach
    /// this table) — so only the Owner may resolve them, closing the obvious hole where a
    /// Manager could otherwise just approve their own request from this same screen. Leave
    /// keeps the same OwnerOrManager bar StaffController's own leave-approval endpoints use,
    /// since that one isn't money and Manager-approves-staff-leave is normal today.</summary>
    private bool CanResolve(ApprovalType type) => type switch
    {
        ApprovalType.Refund or ApprovalType.Discount or ApprovalType.Expense => User.IsInRole(nameof(AppRole.Owner)),
        _ => User.IsInRole(nameof(AppRole.Owner)) || User.IsInRole(nameof(AppRole.Manager)),
    };

    [HttpPatch("{id:int}/approve")]
    public async Task<ActionResult<ApprovalDto>> Approve(int id, ResolveApprovalRequest req)
    {
        var request = await db.Approvals.FindAsync(id);
        if (request is null) return NotFound();
        if (request.Status != ApprovalStatus.Pending && request.Status != ApprovalStatus.Escalated)
            throw new ApiConflictException("Only pending or escalated requests can be approved.");
        if (!CanResolve(request.Type))
            throw new ApiValidationException(request.Type is ApprovalType.Refund or ApprovalType.Discount or ApprovalType.Expense
                ? "Only the Owner can approve this request."
                : "You don't have permission to approve this request.");

        await ExecuteApprovedActionAsync(request);

        request.Status = ApprovalStatus.Approved;
        request.ResolvedAt = DateTime.UtcNow;
        request.ResolvedById = CurrentUserId();
        request.Notes = req.Notes;
        await db.SaveChangesAsync();

        await audit.LogAsync(AuditAction.ApprovalGranted, AuditResource.Order, request.Id.ToString(),
            $"Approved '{request.Title}' ({request.Amount:C})", AuditSeverity.Medium, CurrentUserId(), CurrentUserName());

        return ApprovalDto.From(request);
    }

    /// <summary>The whole point of gating Refund/Discount/Expense behind an approval — this
    /// is where the action the Manager originally asked for actually happens, now that the
    /// Owner has signed off. Mirrors exactly what OrdersController.Refund/ApplyBillDiscount/
    /// ExpensesController.Create would have done immediately if the request had been under
    /// threshold. Leave is handled by StaffController.ApproveLeaveRequest instead — called
    /// here via the same DbContext rather than duplicating its OnLeave-flip logic.</summary>
    private async Task ExecuteApprovedActionAsync(ApprovalRequest request)
    {
        switch (request.Type)
        {
            case ApprovalType.Refund when request.LinkedEntityId is int refundOrderId:
            {
                var order = await db.Orders.FindAsync(refundOrderId);
                if (order is null || order.Refunded) return; // already handled or order gone — nothing to replay
                order.Refunded = true;
                order.RefundedAmount = request.Amount ?? 0;
                order.RefundReason = request.Description;
                order.RefundedAt = DateTime.UtcNow;
                break;
            }
            case ApprovalType.Discount when request.LinkedEntityId is int discountOrderId:
            {
                var order = await db.Orders.FindAsync(discountOrderId);
                if (order is null || order.Paid) return; // paid/cancelled in the meantime — nothing to replay
                order.BillDiscountAmount = request.Amount ?? 0;
                OrderBuildingService.RecomputeTotals(order, await GetTaxRatePctAsync());
                break;
            }
            case ApprovalType.Expense when request.PayloadJson is not null:
            {
                var payload = JsonSerializer.Deserialize<CreateCafeExpenseRequest>(request.PayloadJson);
                if (payload is null) return;
                var recordedBy = await db.Users.FindAsync(request.RequestedById);
                db.CafeExpenses.Add(new CafeExpense
                {
                    Amount = payload.Amount,
                    Category = payload.Category,
                    Purpose = payload.Purpose,
                    SpentBy = payload.SpentBy,
                    SpentAt = payload.SpentAt ?? DateTime.UtcNow,
                    RecordedByUserId = recordedBy?.Id ?? request.RequestedById,
                    RecordedByName = recordedBy?.Name ?? "",
                });
                break;
            }
            case ApprovalType.Leave when request.LinkedEntityId is int leaveId:
            {
                var leave = await db.LeaveRequests.FindAsync(leaveId);
                if (leave is null || leave.Status != LeaveRequestStatus.Pending) return;
                var reviewer = await db.Users.FindAsync(CurrentUserId());
                leave.Status = LeaveRequestStatus.Approved;
                leave.ReviewedByUserId = reviewer?.Id;
                leave.ReviewedByName = reviewer?.Name;
                leave.ReviewedAt = DateTime.UtcNow;
                var staff = await db.Staff.FindAsync(leave.StaffId);
                if (staff is not null) staff.Status = StaffStatus.OnLeave;
                break;
            }
        }
    }

    private async Task<decimal> GetTaxRatePctAsync() =>
        await taxRateCache.GetTaxRatePctAsync(tenantContext.TenantIdOrDefault,
            async () => (await db.Settings.FirstAsync()).TaxRatePct);

    [HttpPatch("{id:int}/reject")]
    public async Task<ActionResult<ApprovalDto>> Reject(int id, ResolveApprovalRequest req)
    {
        var request = await db.Approvals.FindAsync(id);
        if (request is null) return NotFound();
        if (request.Status != ApprovalStatus.Pending && request.Status != ApprovalStatus.Escalated)
            throw new ApiConflictException("Only pending or escalated requests can be rejected.");
        if (!CanResolve(request.Type))
            throw new ApiValidationException(request.Type is ApprovalType.Refund or ApprovalType.Discount or ApprovalType.Expense
                ? "Only the Owner can reject this request."
                : "You don't have permission to reject this request.");

        // Leave's own reject path (StaffController.RejectLeaveRequest) is the one that
        // actually flips LeaveRequest.Status — call it via the same mirror-sync it already
        // does for approvals initiated from ITS screen, so rejecting from here doesn't leave
        // the LeaveRequest itself stuck on Pending forever.
        if (request.Type == ApprovalType.Leave && request.LinkedEntityId is int leaveId)
        {
            var leave = await db.LeaveRequests.FindAsync(leaveId);
            if (leave is not null && leave.Status == LeaveRequestStatus.Pending)
            {
                var reviewer = await db.Users.FindAsync(CurrentUserId());
                leave.Status = LeaveRequestStatus.Rejected;
                leave.ReviewedByUserId = reviewer?.Id;
                leave.ReviewedByName = reviewer?.Name;
                leave.ReviewedAt = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(req.Notes)) leave.Reason = $"{leave.Reason} (Rejected: {req.Notes.Trim()})".Trim();
            }
        }

        request.Status = ApprovalStatus.Rejected;
        request.ResolvedAt = DateTime.UtcNow;
        request.ResolvedById = CurrentUserId();
        request.Notes = req.Notes;
        await db.SaveChangesAsync();

        await audit.LogAsync(AuditAction.ApprovalDenied, AuditResource.Order, request.Id.ToString(),
            $"Rejected '{request.Title}'", AuditSeverity.Medium, CurrentUserId(), CurrentUserName());

        return ApprovalDto.From(request);
    }

    [HttpPatch("{id:int}/escalate")]
    public async Task<ActionResult<ApprovalDto>> Escalate(int id)
    {
        var request = await db.Approvals.FindAsync(id);
        if (request is null) return NotFound();

        request.Status = ApprovalStatus.Escalated;
        request.Level += 1;
        await db.SaveChangesAsync();
        return ApprovalDto.From(request);
    }

    private int? CurrentUserId()
    {
        var claim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }

    private string CurrentUserName() => User.Identity?.Name ?? "System";
}
