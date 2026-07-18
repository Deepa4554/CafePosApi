using System.Security.Cryptography;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Issues/hashes/cookies the guest-session token — the doc's "do-token pattern"
/// (Section 2.3): table_code lives in the public QrToken, this is the SECRET half.
/// The raw token only ever exists in the HttpOnly cookie on the guest's browser and in
/// the response that sets it; only its SHA-256 hash is persisted (GuestSession.TokenHash),
/// so a DB leak can't be replayed as a live session. Cookie is scoped to /api/public,
/// where every guest-session endpoint lives.
/// </summary>
public interface IGuestSessionService
{
    (string RawToken, string TokenHash) IssueToken();
    string Hash(string rawToken);
    void SetCookie(HttpResponse response, string rawToken, DateTime expiresAt);
    void ClearCookie(HttpResponse response);
    string? ReadCookie(HttpRequest request);
}

public class GuestSessionService(IHostEnvironment env) : IGuestSessionService
{
    public const string CookieName = "cps_gs";

    public (string RawToken, string TokenHash) IssueToken()
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); // 256-bit
        return (raw, Hash(raw));
    }

    public string Hash(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)));

    public void SetCookie(HttpResponse response, string rawToken, DateTime expiresAt)
    {
        response.Cookies.Append(CookieName, rawToken, new CookieOptions
        {
            HttpOnly = true,
            // Dev runs plain HTTP on localhost/LAN (see Program.cs CORS comment) — a
            // Secure cookie would silently never be set/sent there. Only enforced
            // outside Development.
            Secure = !env.IsDevelopment(),
            SameSite = SameSiteMode.Lax,
            Expires = expiresAt,
            Path = "/api/public",
        });
    }

    public void ClearCookie(HttpResponse response) =>
        response.Cookies.Delete(CookieName, new CookieOptions { Path = "/api/public" });

    public string? ReadCookie(HttpRequest request) =>
        request.Cookies.TryGetValue(CookieName, out var value) ? value : null;
}
