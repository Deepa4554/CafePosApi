namespace CafePOS.Api.Domain;

/// <summary>
/// A CafePOS-the-startup business expense (not a cafe/tenant's own expense — those
/// belong to individual cafes and have no home here). Deliberately global, not
/// ITenantScoped: this is the platform owners' own books, visible only via
/// PlatformAdminOnly, and has nothing to do with any single tenant's data.
/// </summary>
public class PlatformExpense
{
    public int Id { get; set; }
    public decimal Amount { get; set; }
    /// <summary>Who the money was spent by (a founder/co-owner name) — free text, not
    /// tied to a login, since not everyone logging an expense necessarily has one.</summary>
    public required string SpentBy { get; set; }
    /// <summary>What it was for, e.g. "Bike petrol".</summary>
    public required string Purpose { get; set; }
    public DateTime SpentAt { get; set; } = DateTime.UtcNow;
    /// <summary>The platform-admin account that actually logged this entry — for
    /// accountability, distinct from SpentBy (who the money went to/for).</summary>
    public int RecordedByUserId { get; set; }
    public string RecordedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
