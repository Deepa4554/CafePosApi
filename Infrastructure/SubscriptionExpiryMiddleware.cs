using CafePOS.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Blocks API access once a tenant's current plan cycle has ended — PlanExpiresAt was
/// previously only ever displayed to the user, never enforced (a plan could run
/// forever, trial or paid). Auth, subscription, settings and the public/anonymous
/// flows stay reachable so an expired tenant can still log in, see billing, and
/// upgrade/renew.
/// </summary>
public class SubscriptionExpiryMiddleware(RequestDelegate next)
{
    private static readonly string[] AllowedPrefixes =
    [
        // /api/payments belongs on this list for the same reason /api/subscription does, and
        // more urgently: an expired tenant renewing is the single most important request this
        // middleware must not block — 402-ing the checkout would leave the only way out of
        // the lockout behind the lockout.
        "/api/auth", "/api/subscription", "/api/payments", "/api/settings", "/api/public", "/order", "/health", "/swagger",
    ];

    public async Task InvokeAsync(HttpContext context, CafePosDbContext db, ITenantContext tenant, ISubscriptionCache subscriptions)
    {
        var path = context.Request.Path.Value ?? "";
        var isAllowlisted = AllowedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        var isPlatformAdmin = context.User.FindFirst("isPlatformAdmin")?.Value == "true";

        if (!isAllowlisted && !isPlatformAdmin && context.User.Identity?.IsAuthenticated == true)
        {
            // Cached — this ran twice per request (here and in RequirePlanHandler) against
            // a row that changes once a billing cycle. See ISubscriptionCache.
            var sub = await subscriptions.GetAsync(tenant.TenantIdOrDefault, async () =>
            {
                var row = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync();
                return new SubscriptionSnapshot(row?.Plan ?? Domain.SubscriptionTier.FreeTrial, row?.PlanExpiresAt);
            });
            var expired = sub.PlanExpiresAt is not null && sub.PlanExpiresAt < DateTime.UtcNow;
            if (expired)
            {
                context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsync(
                    """{"title":"Your plan has expired. Renew or upgrade to keep using CafePOS.","status":402}""");
                return;
            }
        }

        await next(context);
    }
}
