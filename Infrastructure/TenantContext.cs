using CafePOS.Api.Domain;

namespace CafePOS.Api.Infrastructure;

/// <summary>Resolves the current request's tenant from the validated JWT — never
/// from client-supplied input. Scoped per-request; injected into CafePosDbContext
/// so every query/save automatically targets the right cafe.</summary>
public interface ITenantContext
{
    /// <summary>Null for anonymous requests (no JWT) or tokens without a tenantId claim.</summary>
    int? CurrentTenantId { get; }
    /// <summary>CurrentTenantId, falling back to the seeded default tenant for anonymous
    /// flows (public QR menu, settings) that haven't been made tenant-aware yet.</summary>
    int TenantIdOrDefault { get; }
}

public class TenantContext : ITenantContext
{
    public int? CurrentTenantId { get; }
    public int TenantIdOrDefault => CurrentTenantId ?? Tenant.DefaultTenantId;

    public TenantContext(IHttpContextAccessor accessor)
    {
        var claim = accessor.HttpContext?.User?.FindFirst("tenantId")?.Value;
        CurrentTenantId = int.TryParse(claim, out var id) ? id : null;
    }
}
