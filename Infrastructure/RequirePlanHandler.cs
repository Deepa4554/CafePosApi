using CafePOS.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Infrastructure;

/// <summary>The server-side half of plan-tier gating — the frontend hides nav entries
/// below a tier for UX, but that's bypassable by calling the API directly, so this is
/// the actual gate. Reads the CURRENT TENANT's subscription (the query filter on
/// CafePosDbContext.Subscriptions already scopes it — no manual tenant lookup needed)
/// and compares against the minimum the endpoint requires.</summary>
public class RequirePlanRequirement(PlanCategory minPlan) : IAuthorizationRequirement
{
    public PlanCategory MinPlan { get; } = minPlan;
}

public class RequirePlanHandler(CafePosDbContext db, ITenantContext tenant, ISubscriptionCache subscriptions)
    : AuthorizationHandler<RequirePlanRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, RequirePlanRequirement requirement)
    {
        // Shares SubscriptionExpiryMiddleware's cache entry, so a plan-gated request now
        // reads this row zero extra times instead of twice. See ISubscriptionCache.
        var sub = await subscriptions.GetAsync(tenant.TenantIdOrDefault, async () =>
        {
            var row = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync();
            return new SubscriptionSnapshot(row?.Plan ?? Domain.SubscriptionTier.FreeTrial, row?.PlanExpiresAt);
        });
        if (sub.Plan.ToCategory() >= requirement.MinPlan)
            context.Succeed(requirement);
    }
}
