namespace CafePOS.Api.Domain;

public enum TenantStatus { Trial, Active, Suspended }

/// <summary>PlanDefault = every screen the tenant's current plan includes is available
/// (today's only behavior, and still the default for every existing/new tenant) — the
/// per-staff Automatic/Custom split underneath is untouched. Custom = the platform admin
/// has curated an explicit subset of the plan's screens for this one cafe (e.g. a
/// bakery-only cafe that shouldn't see KDS/Tables even though its plan technically
/// includes them); see Tenant.EnabledScreens and ScreenCatalog. Mirrors AppUser's
/// StaffAccessMode one level up — same shape, same reasoning, one level higher in the
/// containment chain (plan → tenant → role/staff).</summary>
public enum TenantScreenMode
{
    PlanDefault,
    Custom,
}

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

    /// <summary>Platform-admin-only override of which plan screens this cafe actually gets.
    /// Defaults to PlanDefault so every existing cafe keeps today's behavior untouched.
    /// Set from SuperAdmin's Tenant Screen Access tab — never by the cafe's own Owner.</summary>
    public TenantScreenMode ScreenMode { get; set; } = TenantScreenMode.PlanDefault;

    /// <summary>Explicit allow-list of ScreenCatalog keys, only consulted when ScreenMode is
    /// Custom. Null/empty in PlanDefault mode. Validated + cascade-normalized against
    /// ScreenCatalog on every write (see SuperAdminController.UpdateTenantScreenAccess) so it
    /// can never persist a key above the tenant's plan or a child whose parent is missing.
    /// This is the ceiling every staff login's own screen access (AppUser.AllowedScreens)
    /// gets intersected with — see ScreenAccessFilter and the client's canAccessRoute.</summary>
    public List<string>? EnabledScreens { get; set; }
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
