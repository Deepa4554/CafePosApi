using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CafePOS.Api.Infrastructure;

public class GeminiOptions
{
    /// <summary>Google AI Studio API key. If unset, AI features fail with a clean
    /// "not configured" error rather than throwing on a null key.</summary>
    public string ApiKey { get; set; } = "";
}

public record AiChatMessage(string Role, string Text);

public interface IGeminiService
{
    bool IsConfigured { get; }

    /// <summary>Multi-turn chat — history + a new user message, returns the model's reply text.</summary>
    Task<string> ChatAsync(string systemPrompt, IReadOnlyList<AiChatMessage> history, string newMessage, CancellationToken ct = default);

    /// <summary>Single-shot generation — one prompt in, one text response out. Used for
    /// non-conversational features (e.g. phrasing a data-driven suggestion in plain English).</summary>
    Task<string> GenerateAsync(string prompt, CancellationToken ct = default);

    /// <summary>Single-shot generation with an image attached alongside the prompt — used for
    /// reading a photo (e.g. extracting menu items from a photographed menu board).</summary>
    Task<string> GenerateWithImageAsync(string prompt, string base64Data, string mimeType, CancellationToken ct = default);
}

/// <summary>
/// Thin wrapper around the Gemini REST API (generativelanguage.googleapis.com). The API
/// key lives only in server config (appsettings.Development.json, gitignored — see
/// Resend/ApiKey for the same pattern) and is never sent to the client; the
/// frontend only ever talks to our own /api/ai/* endpoints.
/// </summary>
public class GeminiService(HttpClient http, IOptions<GeminiOptions> options, ILogger<GeminiService> logger) : IGeminiService
{
    // gemini-flash-latest is the model this project's API key actually has quota for —
    // verified directly against the API; gemini-2.0-flash returned a 0-quota 429 and
    // gemini-1.5-flash/gemini-2.5-flash are both 404 (retired) for this key.
    private const string Model = "gemini-flash-latest";
    private readonly GeminiOptions _options = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public Task<string> ChatAsync(string systemPrompt, IReadOnlyList<AiChatMessage> history, string newMessage, CancellationToken ct = default)
    {
        var contents = history
            .Select(h => new GeminiContent(h.Role == "assistant" ? "model" : "user", [new GeminiPart(h.Text)]))
            .Append(new GeminiContent("user", [new GeminiPart(newMessage)]))
            .ToList();
        return CallAsync(systemPrompt, contents, ct);
    }

    public Task<string> GenerateAsync(string prompt, CancellationToken ct = default) =>
        CallAsync(null, [new GeminiContent("user", [new GeminiPart(prompt)])], ct);

    public Task<string> GenerateWithImageAsync(string prompt, string base64Data, string mimeType, CancellationToken ct = default) =>
        CallAsync(null, [new GeminiContent("user",
            [new GeminiPart(Text: prompt), new GeminiPart(InlineData: new GeminiInlineData(mimeType, base64Data))])], ct);

    private async Task<string> CallAsync(string? systemPrompt, List<GeminiContent> contents, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new ApiConflictException("AI features aren't configured on this server yet.");

        var payload = new GeminiRequest(
            contents,
            systemPrompt is null ? null : new GeminiContent(null, [new GeminiPart(systemPrompt)]));

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent?key={_options.ApiKey}";
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(url, payload, JsonOptions, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Gemini request failed (network)");
            throw new ApiConflictException("Could not reach the AI service. Try again in a moment.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("Gemini API returned {Status}: {Body}", response.StatusCode, body);
            throw new ApiConflictException("The AI service couldn't process that request right now.");
        }

        var result = await response.Content.ReadFromJsonAsync<GeminiResponse>(JsonOptions, ct);
        var text = result?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
        return string.IsNullOrWhiteSpace(text) ? "Sorry, I couldn't come up with a response for that." : text.Trim();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---------- Gemini REST shapes (only the fields we actually use) ----------
    private record GeminiRequest(List<GeminiContent> Contents, [property: JsonPropertyName("systemInstruction")] GeminiContent? SystemInstruction);
    private record GeminiContent(string? Role, List<GeminiPart> Parts);
    private record GeminiPart(string? Text = null, GeminiInlineData? InlineData = null);
    private record GeminiInlineData(string MimeType, string Data);
    private record GeminiResponse(List<GeminiCandidate>? Candidates);
    private record GeminiCandidate(GeminiContent? Content);
}
