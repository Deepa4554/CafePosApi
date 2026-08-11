namespace CafePOS.Api.Domain;

/// <summary>
/// A single uploaded menu PDF for one cafe. At most one row per tenant (enforced by the
/// unique index on TenantId — see CafePosDbContext) so a re-upload replaces the existing
/// row rather than accumulating copies.
///
/// This is the "some cafes hand out a designed PDF menu instead of the live digital menu"
/// feature: when <see cref="Enabled"/> is true, scanning the cafe's general (menu-only) QR
/// redirects the guest straight to this PDF instead of the interactive ordering page (see
/// PublicOrderPageController). Table and delivery QRs are never affected — they always keep
/// their live flow.
///
/// The bytes live inline in the DB on purpose rather than in Supabase image storage: that
/// service is image-only (rejects application/pdf) and may not be configured on every
/// deployment, and a menu PDF is one small file per cafe read only on a public scan — so an
/// inline column is the simplest thing that works everywhere with no external dependency.
/// It is deliberately its own table (not a column on CafeSettings) so the multi-MB byte
/// payload never rides along on the public /api/settings fetch every QR page load makes.
/// </summary>
public class MenuPdf : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    /// <summary>The raw PDF bytes. Served as-is with content-type application/pdf.</summary>
    public required byte[] Data { get; set; }

    /// <summary>Original file name the cafe uploaded, shown in the admin UI and used for the
    /// download's suggested filename. Never trusted for anything security-sensitive.</summary>
    public required string FileName { get; set; }

    /// <summary>Byte length of <see cref="Data"/>, denormalized so the admin status endpoint
    /// can show the size without loading the whole payload.</summary>
    public long SizeBytes { get; set; }

    /// <summary>The ON/OFF switch. True = the general menu QR shows this PDF; false = the PDF
    /// is kept on file but the QR falls back to the normal live digital menu. Defaults true on
    /// upload — a cafe that just uploaded a menu PDF almost certainly wants it live.</summary>
    public bool Enabled { get; set; } = true;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
