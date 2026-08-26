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
// MoreScreen/DesktopAppShell only poll this for their pending-count badge, and both now
// skip the call entirely without Approvals access (see useApprovals' `enabled` option).
[RequireScreen("Approvals")]
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

        // An approval blocks real work the requester is standing there waiting on (a refund at
        // the counter, a discount before the bill prints), so it can't wait for whenever someone
        // next opens the Approvals screen. Addressed by ResolverRoles rather than
        // request.AssignedToId: for money types only the Owner may resolve, so an assignment to
        // a fellow Manager would otherwise ping someone who'd get nothing but a permission error.
        var requester = await db.Users.FindAsync(currentUserId);
        db.Notifications.Add(new AppNotification
        {
            Title = "Approval needed",
            Body = request.Amount is decimal amount
                ? $"{requester?.Name ?? "Someone"} requested {request.Type} approval — \"{request.Title}\" ({amount:C})."
                : $"{requester?.Name ?? "Someone"} requested {request.Type} approval — \"{request.Title}\".",
            Category = NotificationCategory.Approval,
            Channel = NotificationChannel.InApp,
            ActionUrl = "/approvals",
            TargetRolesCsv = string.Join(',', ResolverRoles(req.Type)),
        });

        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = request.Id }, ApprovalDto.From(request));
    }

    /// <summary>Refund/Discount/Expense requests here only ever exist because a Manager hit
    /// ApprovalThresholds and got held (Owner-initiated ones bypass entirely and never reach
    /// this table) — so only the Owner may resolve them, closing the obvious hole where a
    /// Manager could otherwise just approve their own request from this same screen. Leave
    /// keeps the same OwnerOrManager bar StaffController's own leave-approval endpoints use,
    /// since that one isn't money and Manager-approves-staff-leave is normal today.
    ///
    /// Single source of truth for both the permission check (CanResolve) and who Submit's
    /// "approval needed" notification is addressed to — the two must not drift, or the app
    /// ends up pinging someone whose only possible next step is a permission error.</summary>
    private static AppRole[] ResolverRoles(ApprovalType type) =>
        (type is ApprovalType.Refund or ApprovalType.Discount or ApprovalType.Expense or ApprovalType.Complimentary)
            ? [AppRole.Owner]
            : [AppRole.Owner, AppRole.Manager];

    private bool CanResolve(ApprovalType type) => ResolverRoles(type).Any(r => User.IsInRole(r.ToString()));

    [HttpPatch("{id:int}/approve")]
    public async Task<ActionResult<ApprovalDto>> Approve(int id, ResolveApprovalRequest req)
    {
        var request = await db.Approvals.FindAsync(id);
        if (request is null) return NotFound();
        if (request.Status != ApprovalStatus.Pending && request.Status != ApprovalStatus.Escalated)
            throw new ApiConflictException("Only pending or escalated requests can be approved.");
        if (!CanResolve(request.Type))
            throw new ApiValidationException(request.Type is ApprovalType.Refund or ApprovalType.Discount or ApprovalType.Expense or ApprovalType.Complimentary
                ? "Only the Owner can approve this request."
                : "You don't have permission to approve this request.");

        await ExecuteApprovedActionAsync(request);

        request.Status = ApprovalStatus.Approved;
        request.ResolvedAt = DateTime.UtcNow;
        request.ResolvedById = CurrentUserId();
        request.Notes = req.Notes;
        NotifyRequesterResolved(request, approved: true, req.Notes);
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
                // Joins the same optimistic-concurrency guard OrdersController.Refund uses —
                // see Order.PaymentVersion. Without the bump, two Owners approving the same
                // request in the same instant both pass the Refunded check above and both
                // commit, putting the refund through the day's numbers twice.
                order.PaymentVersion++;
                break;
            }
            case ApprovalType.Discount when request.LinkedEntityId is int discountOrderId:
            {
                // Items must be loaded: RecomputeTotals charges tax per line at each line's
                // own snapshotted rate, so an order fetched without them would recompute to
                // a zero total. FindAsync alone doesn't populate the collection.
                var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == discountOrderId);
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
                    PaymentMode = payload.PaymentMode,
                    RecordedByUserId = recordedBy?.Id ?? request.RequestedById,
                    RecordedByName = recordedBy?.Name ?? "",
                });
                break;
            }
            case ApprovalType.Complimentary when request.LinkedEntityId is int compOrderId:
            {
                // Mirrors exactly what OrdersController.Pay would have done immediately if the
                // write-off had been under threshold — see its Complimentary handling. Payments
                // must be loaded: this both computes what's still owed and appends to it.
                var order = await db.Orders.Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == compOrderId);
                if (order is null || order.Paid || order.Cancelled) return; // settled/cancelled in the meantime — nothing to replay
                var owed = Math.Max(0, order.Total - order.Payments.Sum(p => p.Amount));
                if (owed <= 0) return; // already fully covered some other way — nothing left to write off
                // Capped at what's actually still owed — the bill may have shrunk (a void, an
                // edit) since the request went in, and request.Amount is only ever a ceiling.
                var amount = Math.Min(request.Amount ?? 0, owed);
                order.Payments.Add(new OrderPayment { OrderId = order.Id, Method = "Complimentary", Amount = amount, LedgerIndex = order.Payments.Count });
                order.PaymentMethod = order.Payments.Count > 1 ? "Multiple" : "Complimentary";
                order.ComplimentaryReason = request.Description;
                // Same optimistic-concurrency bump every other money-mutating path on Order
                // takes (see Order.PaymentVersion) — cheap insurance against a device settling
                // the same order some other way in the same instant this approval lands.
                order.PaymentVersion++;
                if (owed - amount <= 0.01m)
                    await OrderBuildingService.CloseOrderAsync(db, order);
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
            throw new ApiValidationException(request.Type is ApprovalType.Refund or ApprovalType.Discount or ApprovalType.Expense or ApprovalType.Complimentary
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
        NotifyRequesterResolved(request, approved: false, req.Notes);
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

    /// <summary>Closes the loop for whoever submitted the request — targeted at that one user
    /// (AppNotification.TargetUserId), not the resolver roles Submit broadcast to, since the
    /// outcome is only the requester's business. Added to the ChangeTracker before the caller's
    /// SaveChangesAsync so it rides the same transaction and the same push hook.</summary>
    private void NotifyRequesterResolved(ApprovalRequest request, bool approved, string? notes)
    {
        // Nobody to tell: RequestedById falls back to 0 in Submit when the principal carried no
        // resolvable user id, and telling someone about their own action is just noise (possible
        // for Leave, where a Manager may resolve requests they could also have raised).
        if (request.RequestedById <= 0 || request.RequestedById == CurrentUserId()) return;

        db.Notifications.Add(new AppNotification
        {
            Title = approved ? "Approval granted" : "Approval rejected",
            Body = $"{CurrentUserName()} {(approved ? "approved" : "rejected")} \"{request.Title}\""
                + (string.IsNullOrWhiteSpace(notes) ? "." : $" — {notes.Trim()}"),
            Category = NotificationCategory.Approval,
            Channel = NotificationChannel.InApp,
            ActionUrl = "/approvals",
            TargetUserId = request.RequestedById,
        });
    }

    /// <summary>Both claim types, same as NotificationsController: JwtTokenService issues the id
    /// as "sub", but the JWT handler's default inbound claim map rewrites that to
    /// ClaimTypes.NameIdentifier before it ever reaches User.Claims (nothing sets
    /// MapInboundClaims = false), so a lookup for "sub" alone always comes back null — which is
    /// what left every ApprovalRequest here stored with RequestedById 0 and ResolvedById null.</summary>
    private int? CurrentUserId()
    {
        var claim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }

    private string CurrentUserName() => User.Identity?.Name ?? "System";
}
