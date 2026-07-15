using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CafePOS.Api.Infrastructure;

/// <summary>Domain-level "this request is invalid" error — maps to 400 with a clean message.</summary>
public class ApiValidationException(string message) : Exception(message);

/// <summary>Domain-level "conflicts with current state" error — maps to 409.</summary>
public class ApiConflictException(string message) : Exception(message);

/// <summary>
/// Catches every unhandled exception, converts it into a consistent
/// application/problem+json response instead of leaking stack traces, and — unlike
/// before, when only 500s got recorded anywhere — writes every single failure (400s
/// included) to ApiFailureLog, so "which API is failing and why" is a query instead
/// of a repro session. IExceptionHandler is registered as a singleton (AddExceptionHandler),
/// so it takes an IServiceScopeFactory rather than CafePosDbContext directly — the
/// scoped DbContext gets created fresh per failure, not shared across the app's lifetime.
/// </summary>
public class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IServiceScopeFactory scopeFactory) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            ApiValidationException => (StatusCodes.Status400BadRequest, exception.Message),
            ApiConflictException => (StatusCodes.Status409Conflict, exception.Message),
            KeyNotFoundException => (StatusCodes.Status404NotFound, exception.Message),
            // The message here is already deliberately generic where it needs to be
            // (e.g. AuthController's "Invalid email or password." never says which
            // part was wrong) — no reason to further flatten it to "Unauthorized."
            // and lose that context for the user.
            UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, exception.Message),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred."),
        };

        if (status == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception on {Path}", httpContext.Request.Path);
        }

        await LogFailureAsync(httpContext, exception, status, title);

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = title,
            Instance = httpContext.Request.Path,
        }, cancellationToken);

        return true;
    }

    private async Task LogFailureAsync(HttpContext httpContext, Exception exception, int status, string reason)
    {
        try
        {
            var idClaim = httpContext.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                ?? httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            var tenantClaim = httpContext.User.FindFirst("tenantId")?.Value;

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CafePosDbContext>();

            db.ApiFailureLogs.Add(new ApiFailureLog
            {
                Method = httpContext.Request.Method,
                Path = httpContext.Request.Path.Value ?? "",
                StatusCode = status,
                ExceptionType = exception.GetType().Name,
                Reason = reason,
                StackTrace = status == StatusCodes.Status500InternalServerError ? exception.ToString() : null,
                UserId = int.TryParse(idClaim, out var uid) ? uid : null,
                TenantId = int.TryParse(tenantClaim, out var tid) ? tid : null,
                UserName = httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value,
            });
            await db.SaveChangesAsync();
        }
        catch (Exception logEx)
        {
            // Never let the act of logging a failure become a second failure — the
            // real error response above still goes out either way.
            logger.LogWarning(logEx, "Could not write to ApiFailureLog");
        }
    }
}
