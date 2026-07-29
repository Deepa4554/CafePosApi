using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace CafePOS.Api.Infrastructure;

public record WhatsAppNodeStatusDto(string Status, string? PhoneNumberE164, DateTime? ConnectedAt);
public record WhatsAppNodePairDto(string? QrDataUrl, string Status);

/// <summary>
/// The ONLY thing in CafePosApi that talks to the Node whatsapp-service for the staff-facing
/// pairing flow (WhatsAppController proxies through this, never lets the RN app reach Node
/// directly) — keeps CafePosApi the single authenticated ingress, so tenant auth is enforced
/// once, in one place, before any WhatsApp detail leaves the server. Same shared-secret scheme
/// as WhatsAppEventPublisher, just the reverse direction.
/// </summary>
public class WhatsAppNodeClient(HttpClient http, IOptions<WhatsAppServiceOptions> options)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.BaseUrl);

    private void AddAuthHeader(HttpRequestMessage request) =>
        request.Headers.Add(ServiceApiKeyDefaults.HeaderName, options.Value.ApiKey);

    public async Task<WhatsAppNodeStatusDto?> GetStatusAsync(int tenantId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/whatsapp/{tenantId}/status");
        AddAuthHeader(request);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<WhatsAppNodeStatusDto>(cancellationToken: ct);
    }

    public async Task<WhatsAppNodePairDto?> PairAsync(int tenantId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/whatsapp/{tenantId}/pair");
        AddAuthHeader(request);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<WhatsAppNodePairDto>(cancellationToken: ct);
    }

    public async Task<bool> LogoutAsync(int tenantId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/whatsapp/{tenantId}/logout");
        AddAuthHeader(request);
        using var response = await http.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Opens Node's live QR/status SSE stream for proxying straight through to the RN
    /// client (see WhatsAppController.QrStream) — ResponseHeadersRead so this returns as soon
    /// as headers arrive instead of buffering the whole (never-ending) stream first.</summary>
    public async Task<HttpResponseMessage> OpenQrStreamAsync(int tenantId, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/whatsapp/{tenantId}/qr-stream");
        AddAuthHeader(request);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }
}
