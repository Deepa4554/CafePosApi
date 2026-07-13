using CafePOS.Api.Data;
using CafePOS.Api.Domain;

namespace CafePOS.Api.Infrastructure;

public interface IAuditService
{
    /// <param name="tenantId">Only needed on requests with no JWT-derived tenant yet
    /// (e.g. login, password reset) — pass the acted-on user's TenantId explicitly so
    /// the entry doesn't get mis-stamped onto the fallback default tenant. Authenticated
    /// requests resolve the tenant from the token automatically and can omit this.</param>
    Task LogAsync(AuditAction action, AuditResource resource, string? resourceId, string details,
        AuditSeverity severity = AuditSeverity.Low, int? userId = null, string userName = "System", int? tenantId = null);
}

/// <summary>
/// Centralized audit trail writer — called from controllers whenever a sensitive
/// action happens (login, refund, discount, role change, settings change, ...).
/// Keeps AuditLogController purely read-only, matching the RN AuditLogScreen.
/// </summary>
public class AuditService(CafePosDbContext db) : IAuditService
{
    public async Task LogAsync(AuditAction action, AuditResource resource, string? resourceId, string details,
        AuditSeverity severity = AuditSeverity.Low, int? userId = null, string userName = "System", int? tenantId = null)
    {
        var entry = new AuditLogEntry
        {
            Action = action,
            Resource = resource,
            ResourceId = resourceId,
            Details = details,
            Severity = severity,
            UserId = userId,
            UserName = userName,
        };
        if (tenantId is not null) entry.TenantId = tenantId.Value;
        db.AuditLog.Add(entry);
        await db.SaveChangesAsync();
    }
}
