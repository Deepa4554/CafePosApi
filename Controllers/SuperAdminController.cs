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
public class SuperAdminController(CafePosDbContext db, ISubscriptionCache subscriptions, ITenantScreenAccessCache tenantScreenAccess, IRealtimeNotifier realtime) : ControllerBase
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
    /// same 14-day-trial/1-month-paid cycle rule as the tenant's own (locked-down)
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
            ? DateTime.UtcNow.AddDays(14)
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
        // Affected tenant's entry, not the admin's own — see ISubscriptionCache.
        subscriptions.Invalidate(tenantId);

        var staffCount = await db.Staff.IgnoreQueryFilters().CountAsync(s => s.TenantId == tenantId);
        var branchCount = await db.Branches.IgnoreQueryFilters().CountAsync(b => b.TenantId == tenantId);
        return TenantSummaryDto.From(tenant, sub, staffCount, branchCount);
    }

    /// <summary>
    /// Current cafe-level screen ceiling — see Tenant.ScreenMode/EnabledScreens. PlanDefault
    /// (the default for every existing cafe) means "everything the plan includes"; Custom
    /// means only EnabledScreens, which is always a subset of what the plan currently allows.
    /// </summary>
    [HttpGet("tenants/{tenantId:int}/screen-access")]
    public async Task<ActionResult<TenantScreenAccessDto>> GetTenantScreenAccess(int tenantId)
    {
        var tenant = await db.Tenants.FindAsync(tenantId);
        if (tenant is null) return NotFound();

        var sub = await db.Subscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.TenantId == tenantId);
        return TenantScreenAccessDto.From(tenant, sub);
    }

    /// <summary>
    /// Sets which plan screens this one cafe actually gets — the ceiling every staff login
    /// in it (Owner included) is intersected with, one level above per-staff Custom access
    /// (see StaffController.UpdateScreenAccess). Custom mode's EnabledScreens goes through the
    /// same unknown-key/above-plan/cascade validation as a staff allow-list, then is
    /// cascade-normalized (ScreenCatalog.Normalize) so a child can never survive without its
    /// parent. Switching back to PlanDefault clears the stored list so a later plan change
    /// can't leave a stale key behind. Every connected device in the cafe picks this up
    /// within moments via a tenant-wide "accessChanged" push — see
    /// NotifyTenantAccessChangedAsync — falling back to useLiveAccessSync's safety-net poll.
    /// </summary>
    [HttpPatch("tenants/{tenantId:int}/screen-access")]
    public async Task<ActionResult<TenantScreenAccessDto>> UpdateTenantScreenAccess(int tenantId, UpdateTenantScreenAccessRequest req)
    {
        var tenant = await db.Tenants.FindAsync(tenantId);
        if (tenant is null) return NotFound();

        var sub = await db.Subscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.TenantId == tenantId);
        var tenantPlan = (sub?.Plan ?? SubscriptionTier.FreeTrial).ToCategory();

        if (req.ScreenMode == TenantScreenMode.Custom)
        {
            var requested = (req.EnabledScreens ?? []).Distinct().ToList();

            var unknown = requested.Where(k => !ScreenCatalog.IsValidKey(k)).ToList();
            if (unknown.Count > 0) throw new ApiValidationException($"Unknown screen(s): {string.Join(", ", unknown)}.");

            var abovePlan = requested.Where(k => !ScreenCatalog.IsAssignableAt(k, tenantPlan)).ToList();
            if (abovePlan.Count > 0) throw new ApiValidationException($"These screens need a higher plan than this cafe currently has: {string.Join(", ", abovePlan)}.");

            tenant.EnabledScreens = ScreenCatalog.Normalize(requested);
        }
        else
        {
            tenant.EnabledScreens = null;
        }
        tenant.ScreenMode = req.ScreenMode;

        // Tagged to the AFFECTED tenant, same reasoning as ChangeTenantPlan above.
        db.AuditLog.Add(new AuditLogEntry
        {
            TenantId = tenantId,
            Action = AuditAction.PermissionChange,
            Resource = AuditResource.Settings,
            ResourceId = tenantId.ToString(),
            Details = req.ScreenMode == TenantScreenMode.Custom
                ? $"[Platform Admin] Set custom screen access for this cafe ({(tenant.EnabledScreens ?? []).Count} screen(s))."
                : "[Platform Admin] Reset this cafe's screen access to plan default.",
            Severity = AuditSeverity.High,
        });

        await db.SaveChangesAsync();
        tenantScreenAccess.Invalidate(tenantId);
        _ = realtime.NotifyTenantAccessChangedAsync(tenantId);

        return TenantScreenAccessDto.From(tenant, sub);
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

        // Days and months are the cafe's own (IST — see IstClock), not UTC's. On UTC boundaries
        // every bar here would start at 5:30am IST, so a tenant's after-midnight trade would be
        // credited to the previous day and "today's revenue" would omit it entirely.
        var nowIst = IstClock.NowIst;
        var historyStartIst = nowIst.Date.AddDays(-29);
        var historyStart = historyStartIst - IstClock.Offset;

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
            var day = historyStartIst.AddDays(offset);
            var dayOrders = recentOrders.Where(o => IstClock.ToIst(o.CreatedAt).Date == day).ToList();
            return new DailySalesDto(day.ToString("yyyy-MM-dd"), dayOrders.Sum(o => o.Total), dayOrders.Count);
        }).ToList();

        var monthly = Enumerable.Range(0, 12).Select(offset =>
        {
            var monthStartIst = new DateTime(nowIst.Year, nowIst.Month, 1).AddMonths(-(11 - offset));
            var monthStartUtc = monthStartIst - IstClock.Offset;
            var monthEndUtc = monthStartIst.AddMonths(1) - IstClock.Offset;
            var monthOrders = allTimePaid.Where(o => o.CreatedAt >= monthStartUtc && o.CreatedAt < monthEndUtc).ToList();
            return new MonthlySalesDto(monthStartIst.ToString("MMM yyyy"), monthOrders.Sum(o => o.Total), monthOrders.Count);
        }).ToList();

        var todayRevenue = allTimePaid.Where(o => IstClock.ToIst(o.CreatedAt).Date == nowIst.Date).Sum(o => o.Total);
        var thisMonthStartUtc = new DateTime(nowIst.Year, nowIst.Month, 1) - IstClock.Offset;
        var thisMonthRevenue = allTimePaid.Where(o => o.CreatedAt >= thisMonthStartUtc).Sum(o => o.Total);

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

    /// <summary>Roster for one tenant — feeds the staff picker for the per-user screen
    /// access override below. IgnoreQueryFilters() for the same cross-tenant reach as
    /// everywhere else in this controller.</summary>
    [HttpGet("tenants/{tenantId:int}/staff")]
    public async Task<ActionResult<IEnumerable<StaffDto>>> ListTenantStaff(int tenantId)
    {
        var tenant = await db.Tenants.FindAsync(tenantId);
        if (tenant is null) return NotFound();

        var staff = await db.Staff.IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId)
            .OrderBy(s => s.Name)
            .ToListAsync();
        return staff.Select(s => StaffDto.From(s, includeCompensation: false)).ToList();
    }

    /// <summary>Same shape as StaffController.GetScreenAccess, just reachable cross-tenant
    /// by tenantId+staffId instead of relying on the caller's own ambient tenant (a platform
    /// admin's JWT carries none it should be scoped to).</summary>
    [HttpGet("tenants/{tenantId:int}/staff/{staffId:int}/screen-access")]
    public async Task<ActionResult<StaffScreenAccessDto>> GetTenantStaffScreenAccess(int tenantId, int staffId)
    {
        var staff = await db.Staff.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == staffId && s.TenantId == tenantId);
        if (staff is null) return NotFound();
        if (staff.UserId is null) throw new ApiValidationException("This staff member doesn't have app access yet.");

        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == staff.UserId.Value);
        if (user is null) throw new ApiValidationException("This staff member doesn't have app access yet.");

        return StaffScreenAccessDto.From(staff.Id, user);
    }

    /// <summary>Same validation as StaffController.UpdateScreenAccess (unknown-key/
    /// above-plan/cascade-normalize), but checked against the TARGET tenant's plan — a
    /// platform admin has no plan of their own to fall back to — and reachable regardless
    /// of the admin's role, which the Owner/Manager-gated staff endpoint can't do.</summary>
    [HttpPatch("tenants/{tenantId:int}/staff/{staffId:int}/screen-access")]
    public async Task<ActionResult<StaffScreenAccessDto>> UpdateTenantStaffScreenAccess(int tenantId, int staffId, UpdateStaffScreenAccessRequest req)
    {
        var staff = await db.Staff.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == staffId && s.TenantId == tenantId);
        if (staff is null) return NotFound();
        if (staff.UserId is null) throw new ApiValidationException("This staff member doesn't have app access yet.");

        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == staff.UserId.Value);
        if (user is null) throw new ApiValidationException("This staff member doesn't have app access yet.");
        if (user.Role == AppRole.Owner) throw new ApiValidationException("An Owner login always has full access and can't be restricted.");

        if (req.AccessMode == StaffAccessMode.Custom)
        {
            var sub = await db.Subscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.TenantId == tenantId);
            var tenantPlan = (sub?.Plan ?? SubscriptionTier.FreeTrial).ToCategory();
            var requested = (req.AllowedScreens ?? []).Distinct().ToList();

            var unknown = requested.Where(k => !ScreenCatalog.IsValidKey(k)).ToList();
            if (unknown.Count > 0) throw new ApiValidationException($"Unknown screen(s): {string.Join(", ", unknown)}.");

            var abovePlan = requested.Where(k => !ScreenCatalog.IsAssignableAt(k, tenantPlan)).ToList();
            if (abovePlan.Count > 0) throw new ApiValidationException($"These screens need a higher plan than this cafe currently has: {string.Join(", ", abovePlan)}.");

            user.AllowedScreens = ScreenCatalog.Normalize(requested);
        }
        else
        {
            user.AllowedScreens = null;
        }
        user.AccessMode = req.AccessMode;

        // Tagged to the AFFECTED tenant, same reasoning as ChangeTenantPlan above.
        db.AuditLog.Add(new AuditLogEntry
        {
            TenantId = tenantId,
            Action = AuditAction.PermissionChange,
            Resource = AuditResource.Staff,
            ResourceId = staff.Id.ToString(),
            Details = req.AccessMode == StaffAccessMode.Custom
                ? $"[Platform Admin] Set custom screen access for {staff.Name} ({(user.AllowedScreens ?? []).Count} screen(s))."
                : $"[Platform Admin] Reset {staff.Name}'s screen access to Automatic (role default).",
            Severity = AuditSeverity.High,
        });

        await db.SaveChangesAsync();
        _ = realtime.NotifyAccessChangedAsync(user.TenantId, user.Id);

        return StaffScreenAccessDto.From(staff.Id, user);
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
