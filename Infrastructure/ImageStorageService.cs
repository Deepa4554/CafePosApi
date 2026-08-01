using Microsoft.Extensions.Options;

namespace CafePOS.Api.Infrastructure;

public class SupabaseStorageOptions
{
    public string Url { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string ImagesBucket { get; set; } = "cafepos-images";
}

/// <summary>
/// Abstraction over "wherever uploaded images actually live" — every controller that
/// accepts a photo (menu items, staff, customers, cafe logo) goes through this instead of
/// storing the raw base64 data URI inline in its own DB column. If the storage provider
/// ever changes (self-hosted S3, Cloudflare R2, ...), only the implementation below needs
/// to change — nothing else in the app references Supabase directly.
/// </summary>
public interface IImageStorageService
{
    bool IsConfigured { get; }

    /// <summary>Uploads image bytes under a folder (e.g. "menu-items", "staff") and returns
    /// the public URL to store in the DB. Generates its own unique filename.</summary>
    Task<string> UploadAsync(string folder, byte[] bytes, string contentType, CancellationToken ct = default);

    /// <summary>Convenience wrapper for the shape every controller actually receives from the
    /// client — pickImageAsDataUri() on the frontend always produces a "data:&lt;mime&gt;;base64,&lt;...&gt;"
    /// string. Parses it and uploads in one call.</summary>
    Task<string> UploadDataUriAsync(string folder, string dataUri, CancellationToken ct = default);

    /// <summary>What every Create/Update endpoint that takes a photo field should call: if the
    /// incoming value is a freshly-picked data URI, upload it and return the new storage URL;
    /// otherwise pass it through unchanged (already-stored URL, empty string, or null) — an
    /// edit screen re-submits the existing URL whenever the user didn't pick a new photo, and
    /// that must never be re-uploaded or treated as invalid.</summary>
    Task<string?> ResolveAsync(string folder, string? value, CancellationToken ct = default);
}

/// <summary>
/// Thin wrapper around Supabase's Storage REST API (object upload via plain HTTP — no SDK
/// dependency needed, same style as GeminiService). The secret key lives only in server
/// config (appsettings.Development.json, gitignored) / Render env vars, never sent to the
/// client.
/// </summary>
public class SupabaseImageStorageService(HttpClient http, IOptions<SupabaseStorageOptions> options, ILogger<SupabaseImageStorageService> logger) : IImageStorageService
{
    private readonly SupabaseStorageOptions _options = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Url) && !string.IsNullOrWhiteSpace(_options.SecretKey);

    public async Task<string> UploadAsync(string folder, byte[] bytes, string contentType, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new ApiConflictException("Image storage isn't configured on this server yet.");

        var extension = contentType switch
        {
            "image/png" => "png",
            "image/webp" => "webp",
            "image/gif" => "gif",
            _ => "jpg",
        };
        // Random filename — no relation to any tenant/entity id needed since the DB column
        // stores the full returned URL directly; nothing else has to be able to derive it.
        var path = $"{folder}/{Guid.NewGuid():N}.{extension}";

        var uploadUrl = $"{_options.Url}/storage/v1/object/{_options.ImagesBucket}/{path}";
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

        using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = content };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.SecretKey);
        // Supabase's newer "sb_secret_..." API key format is not a JWT, so the gateway also
        // needs it echoed in the apikey header (Authorization alone gets rejected as
        // "Invalid Compact JWS") — same requirement as the JS client's global headers.
        request.Headers.Add("apikey", _options.SecretKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Supabase Storage upload failed (network)");
            throw new ApiConflictException("Could not reach image storage. Try again in a moment.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("Supabase Storage upload returned {Status}: {Body}", response.StatusCode, body);
            throw new ApiConflictException("Could not upload the image right now.");
        }

        return $"{_options.Url}/storage/v1/object/public/{_options.ImagesBucket}/{path}";
    }

    public Task<string> UploadDataUriAsync(string folder, string dataUri, CancellationToken ct = default)
    {
        var (bytes, contentType) = ParseDataUri(dataUri);
        return UploadAsync(folder, bytes, contentType, ct);
    }

    public async Task<string?> ResolveAsync(string folder, string? value, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith("data:", StringComparison.Ordinal))
            return value;
        return await UploadDataUriAsync(folder, value, ct);
    }

    // Bounds Kestrel's implicit ~28MB request-body default down to something an image
    // upload actually needs — without this, a low-privilege authenticated user could
    // repeatedly post near-max-size payloads for pure memory/cost-amplification DoS
    // (SECURITY_AUDIT_2026-07-30 finding #5).
    private const int MaxImageBytes = 8 * 1024 * 1024; // 8 MB

    // Raster formats only — no image/svg+xml. An SVG is executable content (<script>,
    // onload=, ...): storing one at a public URL and letting it render directly in a
    // browser is a stored-XSS vector in the storage origin (SECURITY_AUDIT_2026-07-30
    // finding #1), not just an image.
    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp", "image/gif",
    };

    private static (byte[] Bytes, string ContentType) ParseDataUri(string dataUri)
    {
        // Reject grossly oversized payloads by string length before doing any base64
        // decoding — decoding allocates ~3/4 of the string length again in bytes, so this
        // bounds worst-case memory use without having to decode first.
        if (dataUri.Length > MaxImageBytes / 3 * 4 + 256)
            throw new ApiValidationException($"Image is too large — max {MaxImageBytes / (1024 * 1024)} MB.");

        // "data:image/jpeg;base64,/9j/4AAQSkZJRgABAQ..."
        var match = System.Text.RegularExpressions.Regex.Match(dataUri, @"^data:(?<mime>[\w/+.-]+);base64,(?<data>.+)$", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!match.Success)
            throw new ApiValidationException("That doesn't look like a valid image upload.");

        var mime = match.Groups["mime"].Value;
        if (!AllowedMimeTypes.Contains(mime))
            throw new ApiValidationException("Only JPEG, PNG, WEBP, and GIF images are allowed.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(match.Groups["data"].Value);
        }
        catch (FormatException)
        {
            throw new ApiValidationException("That doesn't look like a valid image upload.");
        }

        if (bytes.Length > MaxImageBytes)
            throw new ApiValidationException($"Image is too large — max {MaxImageBytes / (1024 * 1024)} MB.");

        // The client-supplied MIME is otherwise trusted as-is (MenuController.cs and every
        // other caller just check a "data:image/" prefix) — verify the bytes actually start
        // with that format's own magic number so a renamed/mislabeled payload can't sneak
        // through as an allowed type.
        if (!MatchesMagicBytes(bytes, mime))
            throw new ApiValidationException("The image data doesn't match its declared type.");

        return (bytes, mime);
    }

    private static bool MatchesMagicBytes(byte[] b, string mime) => mime.ToLowerInvariant() switch
    {
        "image/png" => b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A,
        "image/jpeg" => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF,
        "image/gif" => b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8' && (b[4] == '7' || b[4] == '9') && b[5] == 'a',
        "image/webp" => b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P',
        _ => false,
    };
}
