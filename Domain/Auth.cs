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

/// <summary>Automatic = follow the role-based default (see permissions.ts's
/// FLOOR_STAFF_HIDDEN_ROUTES / canAccessRoute — the same rule every login used before
/// per-staff screen access existed). Custom = ignore the role default entirely and use
/// only AppUser.AllowedScreens as the exact set of visible screens for this login.</summary>
public enum StaffAccessMode
{
    Automatic,
    Custom,
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

    /// <summary>Per-login screen-visibility override, set by an Owner/Manager from the
    /// Staff Access screen. Owner logins ignore this entirely (always full access) — see
    /// canAccessRoute/ScreenCatalog. Defaults to Automatic so every existing login keeps
    /// today's role-based behavior until an Owner explicitly opts a staff member into
    /// Custom mode.</summary>
    public StaffAccessMode AccessMode { get; set; } = StaffAccessMode.Automatic;

    /// <summary>Explicit allow-list of ScreenCatalog keys, only consulted when
    /// AccessMode is Custom. Null/empty in Automatic mode. Validated against
    /// ScreenCatalog (unknown keys, and keys above the tenant's current plan, are
    /// rejected) on every write — see StaffController.UpdateScreenAccess.</summary>
    public List<string>? AllowedScreens { get; set; }
}

/// <summary>
/// One row per logged-in device/browser, not one per user — this is what lets the
/// same account stay signed in on a phone, a second phone, and a web tab all at
/// once. Previously AppUser had a single RefreshToken field, so every new login or
/// silent refresh (anywhere) overwrote it and silently kicked out every other
/// device; see AuthController.Refresh/Logout for how sessions are now looked up
/// and revoked individually instead. Rotated on every /auth/refresh call (the used
/// row is marked RevokedAt and a fresh row takes its place for that same device) —
/// reusing an already-revoked token is treated as invalid, never as "log back in
/// the old session".
/// </summary>
public class RefreshTokenEntry
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public required string Token { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }
}
