namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Turns CafeSettings.LogoUrl into the raw bytes a renderer can draw, or null when there
/// isn't a usable logo.
///
/// The stored value is whatever ResolveAsync left behind (see IImageStorageService): a
/// Supabase Storage URL when object storage is configured, or the original "data:image/...;
/// base64,..." string when it isn't. Both have to work, because a cafe that set its logo
/// before storage was wired up still has the data URI sitting in its settings.
///
/// Every failure returns null rather than throwing. A bill is a financial document and a
/// customer is waiting for it — it must never fail to render because a logo host was slow,
/// the image was deleted, or someone pasted a broken URL. Worst case the bill prints exactly
/// as it does today, without the logo.
/// </summary>
public class CafeLogoLoader(IHttpClientFactory httpClientFactory, ILogger<CafeLogoLoader> logger)
{
    /// <summary>Short on purpose: this sits in the request path of a bill download, so a
    /// logo host having a bad day must cost the customer a moment, not the whole receipt.</summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Well past any sane logo (they are resized to ~900px on upload, see the app's
    /// imagePicker) and small enough that a wrong URL pointing at something huge can't chew
    /// through memory on every receipt.</summary>
    private const int MaxBytes = 2 * 1024 * 1024;

    public async Task<byte[]?> LoadAsync(string? logoUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(logoUrl)) return null;

        try
        {
            if (logoUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = logoUrl.IndexOf(',');
                // Only base64 data URIs are produced by the app's own picker; a percent-encoded
                // one would need different decoding, and guessing is worse than no logo.
                if (comma < 0 || !logoUrl[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase)) return null;
                var bytes = Convert.FromBase64String(logoUrl[(comma + 1)..]);
                return bytes.Length is > 0 and <= MaxBytes ? bytes : null;
            }

            if (!Uri.TryCreate(logoUrl, UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(FetchTimeout);
            var http = httpClientFactory.CreateClient();
            var data = await http.GetByteArrayAsync(uri, cts.Token);
            return data.Length is > 0 and <= MaxBytes ? data : null;
        }
        catch (Exception ex)
        {
            // Debug, not Warning: a cafe without a reachable logo is an ordinary state, and
            // this runs on every bill — logging it louder would just bury real problems.
            logger.LogDebug(ex, "Could not load cafe logo from {LogoUrl}", logoUrl);
            return null;
        }
    }
}
