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
public class ApprovalsController(CafePosDbContext db, IAuditService audit) : ControllerBase
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

    [HttpPatch("{id:int}/approve")]
    public async Task<ActionResult<ApprovalDto>> Approve(int id, ResolveApprovalRequest req)
    {
        var request = await db.Approvals.FindAsync(id);
        if (request is null) return NotFound();
        if (request.Status != ApprovalStatus.Pending && request.Status != ApprovalStatus.Escalated)
            throw new ApiConflictException("Only pending or escalated requests can be approved.");

        request.Status = ApprovalStatus.Approved;
        request.ResolvedAt = DateTime.UtcNow;
        request.ResolvedById = CurrentUserId();
        request.Notes = req.Notes;
        await db.SaveChangesAsync();

        await audit.LogAsync(AuditAction.ApprovalGranted, AuditResource.Order, request.Id.ToString(),
            $"Approved '{request.Title}' ({request.Amount:C})", AuditSeverity.Medium, CurrentUserId(), CurrentUserName());

        return ApprovalDto.From(request);
    }

    [HttpPatch("{id:int}/reject")]
    public async Task<ActionResult<ApprovalDto>> Reject(int id, ResolveApprovalRequest req)
    {
        var request = await db.Approvals.FindAsync(id);
        if (request is null) return NotFound();
        if (request.Status != ApprovalStatus.Pending && request.Status != ApprovalStatus.Escalated)
            throw new ApiConflictException("Only pending or escalated requests can be rejected.");

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
