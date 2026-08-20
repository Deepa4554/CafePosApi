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

        sub.ActiveCouponCode = req.CouponCode;
        // Every plan runs on a term — 14 days for the free trial, then whichever cycle was
        // asked for — reset from "now" on every change (this doubles as "renew" for a payment
        // taken out-of-band, where there's no gateway callback to drive the billing date).
        SubscriptionPricing.StartTerm(sub, req.Plan, req.Cycle, DateTime.UtcNow);

        await db.SaveChangesAsync();
        // The plan gates read through a short-TTL cache (see ISubscriptionCache) — drop the
        // entry so an upgrade unlocks its screens on the very next request, not a minute later.
        subscriptions.Invalidate(tenantContext.TenantIdOrDefault);
        await audit.LogAsync(AuditAction.SubscriptionChange, AuditResource.Subscription, sub.Id.ToString(),
            $"Changed plan from {oldPlan} to {req.Plan} for {SubscriptionPricing.CycleLabel(sub.Cycle)}.", AuditSeverity.High);

        var branchCount = await db.Branches.CountAsync();
        var staffCount = await db.Staff.CountAsync();
        var monthlyOrdersUsed = await MonthlyOrdersUsedAsync();
        return SubscriptionDto.From(sub, branchCount, staffCount, monthlyOrdersUsed);
    }
}
