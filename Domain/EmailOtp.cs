namespace CafePOS.Api.Domain;

/// <summary>
/// A short-lived, single-use code proving someone controls an email address before
/// /register-cafe lets them create a tenant with it. Not ITenantScoped — this exists
/// before any tenant does.
/// </summary>
public class EmailOtp
{
    public int Id { get; set; }
    public required string Email { get; set; }
    public required string Code { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool Used { get; set; }
    /// <summary>Wrong-code guesses against this record — capped to stop brute-forcing a 6-digit code.</summary>
    public int Attempts { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
