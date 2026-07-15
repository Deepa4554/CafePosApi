using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>
/// Cross-tenant platform administration — everything here is deliberately
/// PlatformAdminOnly, never reachable by a cafe Owner. Queries against tenant-scoped
/// tables use IgnoreQueryFilters() because the whole point is to see/act across every
/// tenant, not just the caller's own (which is what the normal global query filter
/// would otherwise restrict them to).
/// </summary>
[ApiController]
[Route("api/superadmin")]
[Authorize(Policy = Policies.PlatformAdminOnly)]
public class SuperAdminController(CafePosDbContext db) : ControllerBase
{
    [HttpGet("tenants")]
    public async Task<IEnumerable<TenantSummaryDto>> ListTenants()
    {
        var tenants = await db.Tenants.OrderBy(t => t.Name).ToListAsync();
        var subs = await db.Subscriptions.IgnoreQueryFilters().ToListAsync();
        var staffCounts = await db.Staff.IgnoreQueryFilters()
            .GroupBy(s => s.TenantId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        var branchCounts = await db.Branches.IgnoreQueryFilters()
            .GroupBy(b => b.TenantId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();

        return tenants.Select(t => TenantSummaryDto.From(
            t,
            subs.FirstOrDefault(s => s.TenantId == t.Id),
            staffCounts.FirstOrDefault(x => x.Key == t.Id)?.Count ?? 0,
            branchCounts.FirstOrDefault(x => x.Key == t.Id)?.Count ?? 0));
    }

    /// <summary>
    /// The real fix for the manual-DB workflow: pick a tenant, pick a plan, done —
    /// same 7-day-trial/1-month-paid cycle rule as the tenant's own (locked-down)
    /// /subscription/change-plan. Still no payment gateway, so this stays a manual
    /// "I confirmed they paid" action by you, just via API instead of raw SQL.
    /// </summary>
    [HttpPost("tenants/{tenantId:int}/change-plan")]
    public async Task<ActionResult<TenantSummaryDto>> ChangeTenantPlan(int tenantId, AdminChangePlanRequest req)
    {
        var tenant = await db.Tenants.FindAsync(tenantId);
        if (tenant is null) return NotFound();

        var sub = await db.Subscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.TenantId == tenantId);
        if (sub is null) return NotFound("No subscription found for this tenant.");

        var oldPlan = sub.Plan;
        sub.Plan = req.Plan;
        sub.UpdatedAt = DateTime.UtcNow;
        sub.PlanExpiresAt = req.Plan == SubscriptionTier.FreeTrial
            ? DateTime.UtcNow.AddDays(7)
            : DateTime.UtcNow.AddMonths(1);

        // Tagged to the AFFECTED tenant, not yours — the shared IAuditService would
        // auto-stamp your own tenant id instead, which would be the wrong cafe's log.
        db.AuditLog.Add(new AuditLogEntry
        {
            TenantId = tenantId,
            Action = AuditAction.SubscriptionChange,
            Resource = AuditResource.Subscription,
            ResourceId = sub.Id.ToString(),
            Details = $"[Platform Admin] Changed plan from {oldPlan} to {req.Plan}.",
            Severity = AuditSeverity.High,
        });

        await db.SaveChangesAsync();

        var staffCount = await db.Staff.IgnoreQueryFilters().CountAsync(s => s.TenantId == tenantId);
        var branchCount = await db.Branches.IgnoreQueryFilters().CountAsync(b => b.TenantId == tenantId);
        return TenantSummaryDto.From(tenant, sub, staffCount, branchCount);
    }

    /// <summary>
    /// Real per-tenant sales — daily for the last 30 days, monthly for the last 12 —
    /// computed from that tenant's actual paid orders. IgnoreQueryFilters() because
    /// this deliberately reaches into a cafe that isn't the caller's own tenant.
    /// </summary>
    [HttpGet("tenants/{tenantId:int}/sales")]
    public async Task<ActionResult<TenantSalesDto>> TenantSales(int tenantId)
    {
        var tenant = await db.Tenants.FindAsync(tenantId);
        if (tenant is null) return NotFound();

        var now = DateTime.UtcNow;
        var historyStart = now.Date.AddDays(-29);

        var recentOrders = await db.Orders.IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId && o.Paid && o.CreatedAt >= historyStart)
            .Select(o => new { o.CreatedAt, o.Total })
            .ToListAsync();

        var allTimePaid = await db.Orders.IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId && o.Paid)
            .Select(o => new { o.CreatedAt, o.Total })
            .ToListAsync();

        var daily = Enumerable.Range(0, 30).Select(offset =>
        {
            var day = historyStart.AddDays(offset);
            var dayOrders = recentOrders.Where(o => o.CreatedAt.Date == day).ToList();
            return new DailySalesDto(day.ToString("yyyy-MM-dd"), dayOrders.Sum(o => o.Total), dayOrders.Count);
        }).ToList();

        var monthly = Enumerable.Range(0, 12).Select(offset =>
        {
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-(11 - offset));
            var monthEnd = monthStart.AddMonths(1);
            var monthOrders = allTimePaid.Where(o => o.CreatedAt >= monthStart && o.CreatedAt < monthEnd).ToList();
            return new MonthlySalesDto(monthStart.ToString("MMM yyyy"), monthOrders.Sum(o => o.Total), monthOrders.Count);
        }).ToList();

        var todayRevenue = allTimePaid.Where(o => o.CreatedAt.Date == now.Date).Sum(o => o.Total);
        var thisMonthRevenue = allTimePaid.Where(o => o.CreatedAt.Year == now.Year && o.CreatedAt.Month == now.Month).Sum(o => o.Total);

        return new TenantSalesDto(
            tenantId, tenant.Name,
            todayRevenue, thisMonthRevenue,
            allTimePaid.Sum(o => o.Total), allTimePaid.Count,
            daily, monthly);
    }

    /// <summary>Cross-tenant support inbox — every cafe's ticket, newest activity first,
    /// so you can see who needs a reply across the whole platform at a glance.</summary>
    [HttpGet("tickets")]
    public async Task<IEnumerable<SupportTicketDto>> ListTickets()
    {
        var tickets = await db.SupportTickets.IgnoreQueryFilters().Include(t => t.Messages)
            .OrderByDescending(t => t.UpdatedAt).ToListAsync();
        var tenantNames = await db.Tenants.ToDictionaryAsync(t => t.Id, t => t.Name);
        return tickets.Select(t => SupportTicketDto.From(t, tenantNames.GetValueOrDefault(t.TenantId, "Unknown Cafe")));
    }

    [HttpGet("tickets/{id:int}")]
    public async Task<ActionResult<SupportTicketDetailDto>> GetTicket(int id)
    {
        var ticket = await db.SupportTickets.IgnoreQueryFilters().Include(t => t.Messages).FirstOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();
        var tenant = await db.Tenants.FindAsync(ticket.TenantId);
        return SupportTicketDetailDto.From(ticket, tenant?.Name ?? "Unknown Cafe");
    }

    /// <summary>Platform admin's reply — tagged FromAdmin so the cafe's chat UI can tell
    /// it apart from their own messages, same idea as a support-desk chat widget.</summary>
    [HttpPost("tickets/{id:int}/messages")]
    public async Task<ActionResult<SupportTicketDetailDto>> ReplyToTicket(int id, AddTicketMessageRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Body))
            throw new ApiValidationException("Message cannot be empty.");

        var ticket = await db.SupportTickets.IgnoreQueryFilters().Include(t => t.Messages).FirstOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();

        ticket.Messages.Add(new SupportTicketMessage
        {
            TenantId = ticket.TenantId,
            FromAdmin = true,
            SenderName = "CafePOS Support",
            Body = req.Body.Trim(),
        });
        ticket.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var tenant = await db.Tenants.FindAsync(ticket.TenantId);
        return SupportTicketDetailDto.From(ticket, tenant?.Name ?? "Unknown Cafe");
    }

    [HttpPatch("tickets/{id:int}/status")]
    public async Task<ActionResult<SupportTicketDetailDto>> SetTicketStatus(int id, SetTicketStatusRequest req)
    {
        if (!Enum.TryParse<SupportTicketStatus>(req.Status, ignoreCase: true, out var status))
            throw new ApiValidationException($"Unknown status '{req.Status}'.");

        var ticket = await db.SupportTickets.IgnoreQueryFilters().Include(t => t.Messages).FirstOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();

        ticket.Status = status;
        ticket.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var tenant = await db.Tenants.FindAsync(ticket.TenantId);
        return SupportTicketDetailDto.From(ticket, tenant?.Name ?? "Unknown Cafe");
    }

    /// <summary>CafePOS-the-startup's own running expenses (petrol, hosting, whatever the
    /// founders are spending on the business) — nothing to do with any cafe's own books.
    /// See PlatformExpense for why this is deliberately not tenant-scoped.</summary>
    [HttpGet("expenses")]
    public async Task<ActionResult<PlatformExpenseSummaryDto>> ListExpenses()
    {
        var all = await db.PlatformExpenses.OrderByDescending(e => e.SpentAt).ToListAsync();
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return new PlatformExpenseSummaryDto(
            all.Sum(e => e.Amount),
            all.Where(e => e.SpentAt >= monthStart).Sum(e => e.Amount),
            all.Select(PlatformExpenseDto.From).ToList());
    }

    [HttpPost("expenses")]
    public async Task<ActionResult<PlatformExpenseDto>> AddExpense(CreatePlatformExpenseRequest req)
    {
        if (req.Amount <= 0)
            throw new ApiValidationException("Amount must be greater than 0.");
        if (string.IsNullOrWhiteSpace(req.SpentBy))
            throw new ApiValidationException("Enter who this was spent by.");
        if (string.IsNullOrWhiteSpace(req.Purpose))
            throw new ApiValidationException("Enter what this expense was for.");

        var idClaim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var recordedBy = await db.Users.FindAsync(int.Parse(idClaim!));

        var expense = new PlatformExpense
        {
            Amount = req.Amount,
            SpentBy = req.SpentBy.Trim(),
            Purpose = req.Purpose.Trim(),
            SpentAt = req.SpentAt ?? DateTime.UtcNow,
            RecordedByUserId = recordedBy?.Id ?? 0,
            RecordedByName = recordedBy?.Name ?? "Platform Admin",
        };
        db.PlatformExpenses.Add(expense);
        await db.SaveChangesAsync();
        return PlatformExpenseDto.From(expense);
    }

    [HttpDelete("expenses/{id:int}")]
    public async Task<IActionResult> DeleteExpense(int id)
    {
        var expense = await db.PlatformExpenses.FindAsync(id);
        if (expense is null) return NotFound();
        db.PlatformExpenses.Remove(expense);
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Every failed request across every tenant — see ApiFailureLog's doc
    /// comment. Filterable so a spike of routine 401s (e.g. everyone re-logging-in
    /// after a session-model change) doesn't bury a real 500.</summary>
    [HttpGet("api-failures")]
    public async Task<PagedResult<ApiFailureLogDto>> ListApiFailures(
        [FromQuery] int? statusCode, [FromQuery] int? tenantId, [FromQuery] DateTime? since,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var query = db.ApiFailureLogs.AsQueryable();
        if (statusCode is not null) query = query.Where(f => f.StatusCode == statusCode);
        if (tenantId is not null) query = query.Where(f => f.TenantId == tenantId);
        if (since is not null) query = query.Where(f => f.Timestamp >= since);

        var paged = await query.OrderByDescending(f => f.Timestamp).ToPagedResultAsync(page, pageSize);
        return new PagedResult<ApiFailureLogDto>(paged.Items.Select(ApiFailureLogDto.From).ToList(), paged.Page, paged.PageSize, paged.TotalCount);
    }
}
