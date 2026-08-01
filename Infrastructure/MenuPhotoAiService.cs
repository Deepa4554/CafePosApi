using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CafePOS.Api.Contracts;
using Google.Cloud.Vision.V1;

namespace CafePOS.Api.Infrastructure;

public interface IMenuPhotoAiService
{
    Task<List<CreateMenuItemRequest>> ExtractMenuItemsAsync(string imageDataUri, CancellationToken ct = default);

    /// <summary>Categorizes menu text that's already been OCR'd elsewhere (the client's own
    /// on-device Tesseract.js pass — see menuPhotoImport.ts) via Groq alone, skipping Google
    /// Vision entirely. Exists because Vision requires a Google Cloud billing account linked
    /// before it'll serve any request at all (even within its free tier), which is a real
    /// setup hurdle Tesseract.js + Groq doesn't have — this works with just GROQ_API_KEY.</summary>
    Task<List<CreateMenuItemRequest>> CategorizeOcrTextAsync(string ocrText, CancellationToken ct = default);
}

/// <summary>
/// Parses a photographed menu into categorized items via a two-tier AI fallback chain,
/// reading keys directly off these flat (non-nested) env vars — set on Render, no
/// appsettings.json section:
///   - ANTHROPIC_API_KEY: Claude vision — reads the image directly, best accuracy, but
///     paid (this project runs on Anthropic's free trial credit; once that's spent,
///     every call here just fails and falls through to the tier below).
///   - GOOGLE_APPLICATION_CREDENTIALS: a full service-account JSON (not a file path,
///     despite the name Google's own SDKs usually expect — read as a raw string,
///     turned into a GoogleCredential in-memory, no temp file needed) for Google Cloud
///     Vision, which OCRs the image to plain text.
///   - GROQ_API_KEY: Groq's free/fast LLM inference, given that OCR'd text and asked to
///     categorize it the same way Claude would from the image directly.
/// Google Vision + Groq together are this project's durable free tier once the Claude
/// trial credit runs out — see the docs this was planned against.
///
/// Every tier is best-effort: any failure (bad/missing key, quota exhausted, network)
/// just logs a warning and falls through to the next rather than throwing, so a single
/// misconfigured or exhausted provider never blocks a menu import outright. If every
/// tier comes back empty, the caller (MenuController) returns an empty list — the
/// frontend's own further fallback to fully-offline on-device OCR is what actually
/// keeps this working with zero providers configured at all (see menuPhotoImport.ts).
/// </summary>
public class MenuPhotoAiService(HttpClient http, IConfiguration configuration, ILogger<MenuPhotoAiService> logger) : IMenuPhotoAiService
{
    private const string ClaudeModel = "claude-3-5-sonnet-20241022";
    private const string GroqModel = "llama-3.3-70b-versatile";

    private static readonly Regex DataUriPattern = new(@"^data:(image/[a-zA-Z0-9+.-]+);base64,(.+)$", RegexOptions.Singleline);

    private const string Prompt = """
        This is a photo of a cafe/restaurant menu. Extract every item and respond with ONLY JSON, no other text, no markdown fences.

        For each item, identify: name, price, and category. Use these categories (pick the closest match, or "Other" if nothing fits):
        Coffee, Tea, Snacks, Desserts, Beverages, Food, Other.

        Rules:
        - Price as a plain number (no currency symbol). If no price is visible for an item, use null.
        - Translate any non-English item names to English.
        - Skip section headers, addresses, phone numbers, and anything that isn't an actual menu item.

        Respond with exactly this JSON shape:
        {"items": [{"name": "...", "price": 120.0, "category": "Coffee", "description": ""}]}
        """;

    public async Task<List<CreateMenuItemRequest>> ExtractMenuItemsAsync(string imageDataUri, CancellationToken ct = default)
    {
        var (mediaType, base64) = ParseDataUri(imageDataUri);

        var anthropicKey = configuration["ANTHROPIC_API_KEY"];
        if (!string.IsNullOrWhiteSpace(anthropicKey))
        {
            try
            {
                var items = await CallClaudeAsync(anthropicKey, mediaType, base64, ct);
                if (items.Count > 0) return items;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Claude menu-photo parse failed — falling back to Google Vision + Groq");
            }
        }

        var googleCredentialsJson = configuration["GOOGLE_APPLICATION_CREDENTIALS"];
        var groqKey = configuration["GROQ_API_KEY"];
        if (!string.IsNullOrWhiteSpace(googleCredentialsJson) && !string.IsNullOrWhiteSpace(groqKey))
        {
            try
            {
                var ocrText = await CallGoogleVisionAsync(googleCredentialsJson, base64, ct);
                if (!string.IsNullOrWhiteSpace(ocrText)) return await CallGroqAsync(groqKey, ocrText, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Google Vision + Groq menu-photo parse failed");
            }
        }

        return [];
    }

    public async Task<List<CreateMenuItemRequest>> CategorizeOcrTextAsync(string ocrText, CancellationToken ct = default)
    {
        var groqKey = configuration["GROQ_API_KEY"];
        if (string.IsNullOrWhiteSpace(groqKey)) return [];

        try
        {
            return await CallGroqAsync(groqKey, ocrText, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Groq menu-text categorization failed");
            return [];
        }
    }

    // Same cap as ImageStorageService — bounds both the in-memory base64 decode and, since
    // this payload also gets forwarded whole to paid Claude/Vision APIs, the per-request
    // API cost a low-privilege authenticated user could otherwise run up by repeatedly
    // posting near-max-size photos (SECURITY_AUDIT_2026-07-30 finding #5).
    private const int MaxImageBytes = 8 * 1024 * 1024; // 8 MB

    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp", "image/gif",
    };

    private static (string MediaType, string Base64) ParseDataUri(string dataUri)
    {
        if (dataUri.Length > MaxImageBytes / 3 * 4 + 256)
            throw new ApiValidationException($"Image is too large — max {MaxImageBytes / (1024 * 1024)} MB.");

        var match = DataUriPattern.Match(dataUri);
        if (!match.Success) throw new ApiValidationException("Expected a base64 image data URI.");

        var mediaType = match.Groups[1].Value;
        if (!AllowedMimeTypes.Contains(mediaType))
            throw new ApiValidationException("Only JPEG, PNG, WEBP, and GIF images are allowed.");

        var base64 = match.Groups[2].Value;
        // Rough byte-length check straight off the base64 string (no full decode needed
        // just to bound size) — every 4 base64 chars encode 3 bytes.
        if (base64.Length / 4.0 * 3 > MaxImageBytes)
            throw new ApiValidationException($"Image is too large — max {MaxImageBytes / (1024 * 1024)} MB.");

        return (mediaType, base64);
    }

    private async Task<List<CreateMenuItemRequest>> CallClaudeAsync(string apiKey, string mediaType, string base64, CancellationToken ct)
    {
        var payload = new
        {
            model = ClaudeModel,
            max_tokens = 4096,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image", source = new { type = "base64", media_type = mediaType, data = base64 } },
                        new { type = "text", text = Prompt },
                    },
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Claude API returned {response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
        return ParseItemsJson(text);
    }

    private static async Task<string> CallGoogleVisionAsync(string credentialsJson, string base64, CancellationToken ct)
    {
#pragma warning disable CS0618 // FromJson is obsolete in favor of CredentialFactory, which (as of this SDK version) has no documented direct equivalent for "load from a raw JSON string" — same tradeoff as FcmPushNotificationSender's GoogleCredential.FromStream in Program.cs.
        var credential = Google.Apis.Auth.OAuth2.GoogleCredential.FromJson(credentialsJson);
#pragma warning restore CS0618
        var client = await new ImageAnnotatorClientBuilder { GoogleCredential = credential }.BuildAsync(ct);
        var image = Image.FromBytes(Convert.FromBase64String(base64));
        var response = await client.DetectDocumentTextAsync(image);
        return response?.Text ?? "";
    }

    private async Task<List<CreateMenuItemRequest>> CallGroqAsync(string apiKey, string ocrText, CancellationToken ct)
    {
        var payload = new
        {
            model = GroqModel,
            messages = new object[]
            {
                new { role = "system", content = "You extract structured menu data from raw OCR text and respond with JSON only." },
                new { role = "user", content = $"{Prompt}\n\nRaw OCR text from the menu photo:\n{ocrText}" },
            },
            response_format = new { type = "json_object" },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Groq API returned {response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        return ParseItemsJson(text);
    }

    /// <summary>Claude occasionally wraps its JSON in a ```json fence despite being told
    /// not to — strip that before deserializing rather than failing the whole import.</summary>
    private static List<CreateMenuItemRequest> ParseItemsJson(string text)
    {
        var cleaned = text.Trim();
        if (cleaned.StartsWith("```"))
        {
            var firstNewline = cleaned.IndexOf('\n');
            cleaned = firstNewline >= 0 ? cleaned[(firstNewline + 1)..] : cleaned;
            var lastFence = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0) cleaned = cleaned[..lastFence];
        }

        var parsed = JsonSerializer.Deserialize<ParsedMenuResponse>(cleaned, JsonOpts);
        if (parsed?.Items is null) return [];

        return parsed.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .Select(i => new CreateMenuItemRequest(
                Name: i.Name.Trim(),
                Category: string.IsNullOrWhiteSpace(i.Category) ? "Other" : i.Category.Trim(),
                Price: i.Price ?? 0,
                Icon: null,
                Subtitle: null,
                Image: null,
                Description: string.IsNullOrWhiteSpace(i.Description) ? null : i.Description))
            .ToList();
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private record ParsedMenuResponse(List<ParsedMenuItem>? Items);
    private record ParsedMenuItem(string Name, decimal? Price, string? Category, string? Description);
}
