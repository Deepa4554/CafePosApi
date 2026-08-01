namespace CafePOS.Api.Infrastructure;

/// <summary>
/// httpOnly cookie mirror of the access/refresh tokens AuthController also returns in
/// AuthResponse's JSON body. The mobile app (React Native — no in-page script injection
/// surface) keeps using the body tokens via MMKV + a Bearer header; the web build relies on
/// these cookies instead and never persists a token anywhere JS can read (see
/// tokenStore.ts), so a same-origin XSS bug can no longer walk off with a live session
/// (SECURITY_AUDIT_2026-07-30 finding #2, chained off finding #1's stored-SVG vector).
///
/// SameSite=None (not Lax) outside Development: the API deploys on its own onrender.com
/// subdomain (a Public-Suffix-List entry, so it's its own "site" regardless of what shares
/// the onrender.com suffix) and the web frontend isn't guaranteed to share a registrable
/// domain with it — Lax cookies are simply never attached to a cross-site fetch/XHR call
/// (only cross-site top-level GET navigations), which would silently break login the moment
/// frontend and API aren't same-site. None requires Secure, which in turn requires HTTPS —
/// fine in every real deployment, but breaks local http:// dev, so dev keeps Lax instead.
///
/// This doesn't newly expose the app to CSRF: every state-changing endpoint here is bound
/// from a JSON body ([FromBody]), and a JSON Content-Type forces the browser to preflight
/// the request — a page on some other origin can't get a JSON POST past that preflight
/// unless its origin is already in Program.cs's CORS allowlist, and a plain HTML form (the
/// classic CSRF vector, no preflight needed) can't set a JSON Content-Type at all. The CORS
/// origin allowlist is the actual CSRF defense here, independent of SameSite.
/// </summary>
public static class AuthCookies
{
    public const string AccessToken = "pos_access_token";
    public const string RefreshToken = "pos_refresh_token";

    public static void Set(HttpResponse response, string accessToken, string refreshToken, IWebHostEnvironment env, int accessTokenMinutes, int refreshTokenDays)
    {
        var secure = !env.IsDevelopment();
        var sameSite = env.IsDevelopment() ? SameSiteMode.Lax : SameSiteMode.None;
        response.Cookies.Append(AccessToken, accessToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = sameSite,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddMinutes(accessTokenMinutes),
        });
        response.Cookies.Append(RefreshToken, refreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = sameSite,
            // Scoped to just the auth endpoints that ever need to read it (refresh/logout)
            // — even though HttpOnly already keeps page JS from reading it, this keeps it
            // out of every other request's Cookie header too.
            Path = "/api/auth",
            Expires = DateTimeOffset.UtcNow.AddDays(refreshTokenDays),
        });
    }

    public static void Clear(HttpResponse response)
    {
        response.Cookies.Delete(AccessToken, new CookieOptions { Path = "/" });
        response.Cookies.Delete(RefreshToken, new CookieOptions { Path = "/api/auth" });
    }
}
