namespace CafePOS.Api.Domain;

// Matches the RN app's User['role'] union exactly (src/features/auth/domain/entities/User.ts).
public enum AppRole
{
    Owner,
    Manager,
    Cashier,
    Chef,
    Waiter,
    KitchenStaff,
    Accountant,
}

public class AppUser
{
    public int Id { get; set; }
    /// <summary>
    /// Which cafe this login belongs to. Deliberately NOT ITenantScoped/auto-filtered:
    /// login must find a user by email across all tenants before a tenant is known
    /// (that's how the tenant is resolved in the first place). Every other query on
    /// AppUser goes by Id, which is already globally unique.
    /// </summary>
    public int TenantId { get; set; }
    public required string Email { get; set; }
    /// <summary>Required for /register-cafe (real cafe owners); nullable only because
    /// the demo/role-switcher accounts created via /register never collect one.</summary>
    public string? Phone { get; set; }
    public required string Name { get; set; }
    public required string PasswordHash { get; set; }
    public AppRole Role { get; set; } = AppRole.Owner;
    /// <summary>
    /// True only for the platform's own operator account (you) — completely separate
    /// from AppRole.Owner, which just means "owns this one cafe". Cafe owners created
    /// via /register-cafe never get this set, no matter what role they have. There is
    /// no self-service way to set this; see SeedData for how the one bootstrap account
    /// is provisioned.
    /// </summary>
    public bool IsPlatformAdmin { get; set; }
    public string? ProfilePhoto { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? RefreshToken { get; set; }
    public DateTime? RefreshTokenExpiresAt { get; set; }
}
