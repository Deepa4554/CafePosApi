using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Marks a controller/action as belonging to one or more screen-catalog keys, so a login in
/// StaffAccessMode.Custom can only call it if at least one of those screens is actually in
/// its AllowedScreens.
///
/// Role policies ([Authorize(Policy = ...)]) answer "may this ROLE do this at all" and stay
/// the primary gate; this answers the separate question "is this screen switched on for
/// THIS login". Without it, Custom screen access was client-side visibility only — an owner
/// could revoke Payroll and the revoked staff member's token would still fetch payslips.
///
/// Several keys means "any of these" — some endpoints legitimately back more than one
/// screen (e.g. /api/vendors feeds both Vendors and the Purchase Orders form). Deliberately
/// opt-in per endpoint rather than a global default: applying it where the client fetches
/// the same data from an unrelated screen would break that screen instead of securing it.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireScreenAttribute(params string[] screens) : Attribute
{
    public string[] Screens { get; } = screens;
}

/// <summary>
/// Enforces <see cref="RequireScreenAttribute"/>. Registered globally (see Program.cs) but
/// no-ops on every endpoint that doesn't carry the attribute, so the AllowedScreens lookup
/// only runs for endpoints that actually opted in.
///
/// Automatic-mode logins are untouched: their visibility is the role default, which the role
/// policies already enforce. Owner logins are untouched too — StaffController.UpdateScreenAccess
/// refuses to put an Owner into Custom mode in the first place, so this mirrors
/// canAccessRoute's Owner short-circuit rather than adding a second, divergent rule.
/// </summary>
public sealed class ScreenAccessFilter(CafePosDbContext db) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        // Something earlier (authentication, a role policy) already rejected this request —
        // don't overwrite a 401 with a less accurate 403.
        if (context.Result is not null) return;

        var required = context.ActionDescriptor.EndpointMetadata.OfType<RequireScreenAttribute>().LastOrDefault();
        if (required is null || required.Screens.Length == 0) return;

        var principal = context.HttpContext.User;
        // Anonymous/[AllowAnonymous] endpoints have no login to check against; the fallback
        // authorization policy handles anything that should have required a token.
        if (principal.Identity?.IsAuthenticated != true) return;
        if (principal.IsInRole(nameof(AppRole.Owner))) return;

        var idClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (idClaim is null || !int.TryParse(idClaim, out var userId)) return;

        var access = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.AccessMode, u.AllowedScreens })
            .FirstOrDefaultAsync();

        if (access is null || access.AccessMode != StaffAccessMode.Custom) return;

        var allowed = access.AllowedScreens ?? [];
        if (required.Screens.Any(allowed.Contains)) return;

        context.Result = new ObjectResult(new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "This screen isn't switched on for your login. Ask the cafe owner to enable it under Screen Access.",
            Instance = context.HttpContext.Request.Path,
        })
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
    }
}
