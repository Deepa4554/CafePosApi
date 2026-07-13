namespace CafePOS.Api.Domain;

public enum TenantStatus { Trial, Active, Suspended }

/// <summary>A single cafe/business on the platform. Every tenant-owned row
/// (menu, orders, tables, staff, ...) carries this entity's Id via TenantId —
/// see ITenantScoped and CafePosDbContext's global query filters.</summary>
public class Tenant
{
    /// <summary>The first tenant ever seeded (see SeedData). Anonymous requests
    /// (no JWT, e.g. the public QR menu today) fall back to this tenant until
    /// the QR flow carries a tenant-aware slug — see docs/multi-tenant-saas-plan.md §9.</summary>
    public const int DefaultTenantId = 1;

    public int Id { get; set; }
    public required string Name { get; set; }
    /// <summary>Unique, URL-safe identifier — reserved for future subdomain/slug-based routing.</summary>
    public required string Slug { get; set; }
    public TenantStatus Status { get; set; } = TenantStatus.Trial;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Marks an entity as belonging to exactly one Tenant. CafePosDbContext applies a
/// global EF Core query filter and auto-stamps TenantId on insert for every type
/// that implements this — so individual controllers never need to filter by tenant
/// themselves. AppUser deliberately does NOT implement this: login must be able to
/// find a user by email across all tenants before a tenant is known.
/// </summary>
public interface ITenantScoped
{
    int TenantId { get; set; }
}
