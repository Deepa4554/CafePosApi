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
        "/api/auth", "/api/subscription", "/api/settings", "/api/public", "/order", "/health", "/swagger",
    ];

    public async Task InvokeAsync(HttpContext context, CafePosDbContext db)
    {
        var path = context.Request.Path.Value ?? "";
        var isAllowlisted = AllowedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        if (!isAllowlisted && context.User.Identity?.IsAuthenticated == true)
        {
            var sub = await db.Subscriptions.FirstOrDefaultAsync();
            var expired = sub?.PlanExpiresAt is not null && sub.PlanExpiresAt < DateTime.UtcNow;
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
