using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace CafePOS.Api.Infrastructure;

/// <summary>Domain-level "this request is invalid" error — maps to 400 with a clean message.</summary>
public class ApiValidationException(string message) : Exception(message);

/// <summary>Domain-level "conflicts with current state" error — maps to 409.</summary>
public class ApiConflictException(string message) : Exception(message);

/// <summary>
/// Catches every unhandled exception and converts it into a consistent
/// application/problem+json response instead of leaking stack traces.
/// </summary>
public class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
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

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = title,
            Instance = httpContext.Request.Path,
        }, cancellationToken);

        return true;
    }
}
