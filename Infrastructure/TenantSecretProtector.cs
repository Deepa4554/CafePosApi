using Microsoft.AspNetCore.DataProtection;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Encrypts the third-party credentials a CAFE hands us — currently its own Razorpay key and
/// webhook secrets (see CafeSettings.RazorpayKeySecretEnc).
///
/// These are the first secrets in this system that belong to someone else. The platform's own
/// keys live in environment variables and never touch the database; a cafe's cannot, because
/// each tenant has different ones and an Owner types them into a settings screen. Storing them
/// in plain columns would mean a database dump — a backup file, a support export, a read-only
/// analytics connection — handing over the ability to forge a paid bill for every cafe at once.
///
/// Uses the same DataProtection provider QrTokenService already relies on, under its own
/// purpose string so a QR token can never be decrypted as a credential or vice versa. Keys are
/// therefore tied to the app's DataProtection key ring: if that is lost or rotated out, every
/// stored secret becomes unreadable and each cafe re-enters theirs. That is the intended
/// failure — unreadable is the correct outcome for a secret we can no longer prove is ours.
/// </summary>
public class TenantSecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("CafePOS.TenantSecret.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    /// <summary>Null for anything that can't be unprotected — a value written under a key ring
    /// that has since gone, or a column someone edited by hand. Callers must treat that exactly
    /// like "not configured" rather than falling back to an empty secret, which would make every
    /// signature check trivially passable.</summary>
    public string? TryUnprotect(string? ciphertext)
    {
        if (string.IsNullOrWhiteSpace(ciphertext)) return null;
        try
        {
            return _protector.Unprotect(ciphertext);
        }
        catch
        {
            return null;
        }
    }
}
