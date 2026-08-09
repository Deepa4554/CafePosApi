using Microsoft.AspNetCore.DataProtection;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Turns (tenantId, tableCode) into one opaque, tamper-proof token for QR codes/public
/// ordering links — so a scanned URL never reveals the cafe's identity or table number
/// in plain text, and can't be edited to point at a different table/tenant (ASP.NET's
/// Data Protection API authenticates the payload; Unprotect throws if it's been
/// altered). Replaces the earlier plaintext /order/{tenantSlug}/{tableCode} scheme.
/// </summary>
public class QrTokenService(IDataProtectionProvider provider)
{
    /// <summary>
    /// Reserved table code marking a delivery QR — the code the cafe prints on a flyer or
    /// packaging rather than on a seat. Sits in the table-code slot instead of getting its own
    /// token field so the existing token format, protector, and every already-printed QR keep
    /// working untouched; the empty string was already doing exactly this for the menu-only QR.
    ///
    /// The '#' prefix is what makes it safe: table codes are cafe-entered names like "T3" or
    /// "Patio-1", and nothing that resolves a table treats this as one — it matches no row, so a
    /// caller that forgets to check for it fails closed (table not found) rather than open.
    /// </summary>
    public const string DeliveryTableCode = "#DELIVERY";

    private readonly IDataProtector _protector = provider.CreateProtector("CafePOS.QrToken.v1");

    public string Encode(int tenantId, string tableCode) => _protector.Protect($"{tenantId}:{tableCode}");

    /// <summary>Which kind of QR a decoded table code belongs to — "delivery", "menu" (no seat),
    /// or "table". One place, so the public page and the order endpoints can't disagree.</summary>
    public static string ModeFor(string tableCode) => tableCode switch
    {
        DeliveryTableCode => "delivery",
        "" => "menu",
        _ => "table",
    };

    /// <summary>Null if the token is malformed, tampered with, or from a different key
    /// generation — callers should treat that identically to "table not found".</summary>
    public (int TenantId, string TableCode)? TryDecode(string token)
    {
        try
        {
            var raw = _protector.Unprotect(token);
            var parts = raw.Split(':', 2);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var tenantId)) return null;
            return (tenantId, parts[1]);
        }
        catch
        {
            return null;
        }
    }
}
