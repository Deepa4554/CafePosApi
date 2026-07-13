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
    private readonly IDataProtector _protector = provider.CreateProtector("CafePOS.QrToken.v1");

    public string Encode(int tenantId, string tableCode) => _protector.Protect($"{tenantId}:{tableCode}");

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
