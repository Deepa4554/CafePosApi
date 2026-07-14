using System.Security.Cryptography;
using System.Text;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Turns an orderId into one short, tamper-evident token for the public bill-PDF link sent
/// over WhatsApp — a plain /orders/{id}/receipt.pdf URL would let anyone enumerate and view
/// other customers' bills, but a bill isn't sensitive enough to warrant a full encrypted
/// DataProtection token (which runs 130+ characters and made the WhatsApp link/message look
/// broken). An 8-hex-char HMAC-SHA256 signature over the id is short (whole token ~12 chars)
/// and still can't be forged or guessed without the signing key.
///
/// No tenantId in the token: Order.Id is a single globally-unique identity column (not
/// scoped per-tenant), so the id alone is enough to look the order up correctly.
/// </summary>
public class ReceiptTokenService(IConfiguration config)
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(
        string.IsNullOrWhiteSpace(config["Jwt:Secret"])
            ? "dev-only-insecure-secret-key-change-me-before-prod-32chars!"
            : config["Jwt:Secret"]!);

    private string Sign(int orderId) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(orderId.ToString())))[..8].ToLowerInvariant();

    public string Encode(int orderId) => $"{orderId}-{Sign(orderId)}";

    /// <summary>Null if the token is malformed or the signature doesn't match — callers
    /// should treat that identically to "receipt not found".</summary>
    public int? TryDecode(string token)
    {
        var parts = token.Split('-', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var orderId)) return null;
        return string.Equals(Sign(orderId), parts[1], StringComparison.OrdinalIgnoreCase) ? orderId : null;
    }
}
