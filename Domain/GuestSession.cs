namespace CafePOS.Api.Domain;

public enum GuestSessionStatus { Active, Locked, Closed, Expired, Revoked }

public enum SessionCloseReason { Settled, StaffClosed, Timeout, Abuse }

/// <summary>
/// A guest's live QR-ordering session on one table — see docs/qr-ordering-session-plan
/// for the full lifecycle. Server-side is the only thing that ever creates/closes one;
/// the guest's cookie is just a key into this row (see GuestSessionService). OrderId is
/// set on the FIRST cart-add, not on first KOT fire — an OrderItem always needs a parent
/// Order.Id, so the cart itself is an unfired Order (see OrderBuildingService).
/// </summary>
public class GuestSession : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int TableId { get; set; }
    public int? OrderId { get; set; }
    public GuestSessionStatus Status { get; set; } = GuestSessionStatus.Active;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Sliding-expiry marker — bumped on every validated request, from any device.</summary>
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    /// <summary>Hard TTL — the session dies at this instant no matter how active it's been.</summary>
    public DateTime ExpiresAt { get; set; }
    public SessionCloseReason? ClosedReason { get; set; }
    public DateTime? ClosedAt { get; set; }
}

/// <summary>One browser holding a credential onto a GuestSession — the FIRST scan and every
/// later "JOIN" (doc CASE 3) each get their own row/token here; GuestSession itself carries
/// no token. This is deliberate: token hashes are stored one-way (see GuestSessionService),
/// so the server can never re-issue an *existing* raw token to a newly-joining phone — each
/// device instead gets its own credential that authenticates against the same shared
/// session, which the "JOIN" flow (POST session/join) is what mints.</summary>
public class SessionDevice : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int SessionId { get; set; }
    /// <summary>SHA-256 hash of this device's own random 256-bit token — the raw token
    /// itself is never persisted (see GuestSessionService).</summary>
    public required string TokenHash { get; set; }
    public string? UserAgent { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
}
