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

    private static (byte[] Bytes, string ContentType) ParseDataUri(string dataUri)
    {
        // "data:image/jpeg;base64,/9j/4AAQSkZJRgABAQ..."
        var match = System.Text.RegularExpressions.Regex.Match(dataUri, @"^data:(?<mime>[\w/+.-]+);base64,(?<data>.+)$", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!match.Success)
            throw new ApiValidationException("That doesn't look like a valid image upload.");
        return (Convert.FromBase64String(match.Groups["data"].Value), match.Groups["mime"].Value);
    }
}
