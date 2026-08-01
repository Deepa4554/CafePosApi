using System.Security.Cryptography;
using System.Text;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Turns an orderId into one short, tamper-evident token for the public bill-PDF link sent
/// over WhatsApp — a plain /orders/{id}/receipt.pdf URL would let anyone enumerate and view
/// other customers' bills, but a bill isn't sensitive enough to warrant a full encrypted
/// DataProtection token (which runs 130+ characters and made the WhatsApp link/message look
/// broken). A 16-hex-char (64-bit) HMAC-SHA256 signature over the id is still short (whole
/// token ~20 characters) but keeps the brute-force space out of reach of the endpoint's own
/// rate limit (see PublicController.GetReceipt's [EnableRateLimiting]) — the original 8-hex
/// (32-bit) signature was only ~4.3B guesses, feasible to grind through unauthenticated
/// (SECURITY_AUDIT_2026-07-30 finding #3).
///
/// No tenantId in the token: Order.Id is a single globally-unique identity column (not
/// scoped per-tenant), so the id alone is enough to look the order up correctly.
/// </summary>
public class ReceiptTokenService(IConfiguration config)
{
    private const int SignatureHexLength = 16;

    private readonly byte[] _key = Encoding.UTF8.GetBytes(
        string.IsNullOrWhiteSpace(config["Jwt:Secret"])
            ? "dev-only-insecure-secret-key-change-me-before-prod-32chars!"
            : config["Jwt:Secret"]!);

    private string Sign(int orderId) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(orderId.ToString())))[..SignatureHexLength].ToLowerInvariant();

    public string Encode(int orderId) => $"{orderId}-{Sign(orderId)}";

    /// <summary>Null if the token is malformed or the signature doesn't match — callers
    /// should treat that identically to "receipt not found".</summary>
    public int? TryDecode(string token)
    {
        var parts = token.Split('-', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var orderId)) return null;

        // Constant-time compare — a naive string.Equals short-circuits on the first
        // mismatched byte, which leaks how many leading hex characters an attacker's guess
        // got right through response-time differences (SECURITY_AUDIT_2026-07-30 finding #3).
        var expected = Sign(orderId);
        var supplied = parts[1].ToLowerInvariant();
        if (expected.Length != supplied.Length) return null;
        var isMatch = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied));
        return isMatch ? orderId : null;
    }
}
