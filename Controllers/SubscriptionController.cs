using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

[ApiController]
[Route("api/subscription")]
public class SubscriptionController(
    CafePosDbContext db, IAuditService audit, ITenantContext tenantContext, ISubscriptionCache subscriptions) : ControllerBase
{
    [HttpGet]
    public async Task<SubscriptionDto> Get()
    {
        var sub = await db.Subscriptions.FirstAsync();
        var branchCount = await db.Branches.CountAsync();
        var staffCount = await db.Staff.CountAsync();
        var monthlyOrdersUsed = await MonthlyOrdersUsedAsync();
        return SubscriptionDto.From(sub, branchCount, staffCount, monthlyOrdersUsed);
    }

    /// <summary>
    /// Live count instead of a persisted counter — no order-creation code path has to
    /// remember to increment anything, and there's no monthly-reset job to maintain.
    /// </summary>
    private async Task<int> MonthlyOrdersUsedAsync()
    {
        var startOfMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return await db.Orders.CountAsync(o => o.CreatedAt >= startOfMonth);
    }

    /// <summary>
    /// Deliberately PlatformAdminOnly, not OwnerOnly: there's no real payment gateway
    /// yet, so letting a cafe Owner call this themselves would let them grant their own
    /// plan/renewal for free. Until billing is wired up, only you (the platform
    /// operator) can move a tenant onto a plan — today that's a manual DB update after
    /// confirming payment out-of-band; this endpoint exists for when an admin panel is
    /// built, not for cafe owners to self-serve.
    /// </summary>
    [Authorize(Policy = Policies.PlatformAdminOnly)]
    [HttpPost("change-plan")]
    public async Task<ActionResult<SubscriptionDto>> ChangePlan(ChangePlanRequest req)
    {
        var sub = await db.Subscriptions.FirstAsync();
        var oldPlan = sub.Plan;

        if (!string.IsNullOrWhiteSpace(req.CouponCode) && req.CouponCode.Equals("INVALID", StringComparison.OrdinalIgnoreCase))
            throw new ApiValidationException("Coupon code is invalid or expired.");

        sub.Plan = req.Plan;
        sub.ActiveCouponCode = req.CouponCode;
        sub.UpdatedAt = DateTime.UtcNow;
        // Every plan runs on a cycle — 14 days for the free trial, 1 month for any paid
        // tier — reset from "now" on every change (this doubles as "renew" today, since
        // there's no payment gateway yet to trigger renewal on an actual billing date).
        sub.PlanExpiresAt = req.Plan == SubscriptionTier.FreeTrial
            ? DateTime.UtcNow.AddDays(14)
            : DateTime.UtcNow.AddMonths(1);

        await db.SaveChangesAsync();
        // The plan gates read through a short-TTL cache (see ISubscriptionCache) — drop the
        // entry so an upgrade unlocks its screens on the very next request, not a minute later.
        subscriptions.Invalidate(tenantContext.TenantIdOrDefault);
        await audit.LogAsync(AuditAction.SubscriptionChange, AuditResource.Subscription, sub.Id.ToString(),
            $"Changed plan from {oldPlan} to {req.Plan}.", AuditSeverity.High);

        var branchCount = await db.Branches.CountAsync();
        var staffCount = await db.Staff.CountAsync();
        var monthlyOrdersUsed = await MonthlyOrdersUsedAsync();
        return SubscriptionDto.From(sub, branchCount, staffCount, monthlyOrdersUsed);
    }
}
