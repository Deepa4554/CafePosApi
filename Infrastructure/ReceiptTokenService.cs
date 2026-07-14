using Microsoft.AspNetCore.DataProtection;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Turns (tenantId, orderId) into one opaque, tamper-proof token for the public bill-PDF
/// link sent over WhatsApp — an order id is otherwise small and sequential, so a plain
/// /orders/{id}/receipt.pdf URL would let anyone enumerate and view other customers'
/// bills. Same idea as QrTokenService but a separate DataProtection "purpose" string, so
/// the two token schemes are never interchangeable.
/// </summary>
public class ReceiptTokenService(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("CafePOS.ReceiptToken.v1");

    public string Encode(int tenantId, int orderId) => _protector.Protect($"{tenantId}:{orderId}");

    /// <summary>Null if the token is malformed, tampered with, or from a different key
    /// generation — callers should treat that identically to "receipt not found".</summary>
    public (int TenantId, int OrderId)? TryDecode(string token)
    {
        try
        {
            var raw = _protector.Unprotect(token);
            var parts = raw.Split(':', 2);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var tenantId) || !int.TryParse(parts[1], out var orderId))
                return null;
            return (tenantId, orderId);
        }
        catch
        {
            return null;
        }
    }
}
