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

    /// <summary>Same entry as LogAsync, but only added to the tracked context — no
    /// SaveChangesAsync of its own. For a hot path that's about to call
    /// SaveChangesAsync anyway (e.g. AttendanceController.Mark), staging lets the audit
    /// row commit in that same round trip instead of a second one, which is the
    /// difference between one DB round trip and two on every call. Only usable when
    /// resourceId doesn't depend on something SaveChanges itself is about to generate
    /// (a brand-new row's Id isn't assigned until that call returns) — pass null in
    /// that case, same as LogAsync's own multi-resource callers already do.</summary>
    void Stage(AuditAction action, AuditResource resource, string? resourceId, string details,
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
        Stage(action, resource, resourceId, details, severity, userId, userName, tenantId);
        await db.SaveChangesAsync();
    }

    public void Stage(AuditAction action, AuditResource resource, string? resourceId, string details,
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
    }
}
