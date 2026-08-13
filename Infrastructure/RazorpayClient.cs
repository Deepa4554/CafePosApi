using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CafePOS.Api.Infrastructure;

public class RazorpayOptions
{
    /// <summary>Public half of the key pair (rzp_test_… / rzp_live_…). Safe to hand to the
    /// browser — PaymentsController returns it with every created order precisely so the
    /// frontend doesn't have to carry a build-time copy of it (see env.ts: this app has no
    /// per-environment public-variable pipeline, its config is checked-in constants).</summary>
    public string KeyId { get; set; } = "";
    /// <summary>Never leaves the server: it's both the API password AND the HMAC key that
    /// makes a payment signature meaningful. Set via the Razorpay__KeySecret env var
    /// (Render) or `dotnet user-secrets` locally — deliberately not in appsettings.*.json,
    /// which is committed.</summary>
    public string KeySecret { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(KeyId) && !string.IsNullOrWhiteSpace(KeySecret);
}

/// <summary>Razorpay answered, but not with a success — carries enough context for the
/// controller to decide the status code and for the log to be actionable.</summary>
public class RazorpayApiException(string message, HttpStatusCode? upstreamStatus = null) : Exception(message)
{
    public HttpStatusCode? UpstreamStatus { get; } = upstreamStatus;
}

/// <summary>
/// The subset of an order Razorpay returns that we actually act on. `Status` walks
/// created -> attempted -> paid; `Notes` is the free-form key/value bag we stamp the
/// tenant and plan onto at creation time, and read back on verification so the server
/// never has to trust the browser for "which plan was this payment for".
/// </summary>
public record RazorpayOrder(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("amount")] long Amount,
    [property: JsonPropertyName("amount_paid")] long AmountPaid,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("receipt")] string? Receipt,
    [property: JsonPropertyName("notes")] Dictionary<string, string>? Notes);

public interface IRazorpayClient
{
    bool IsConfigured { get; }
    string KeyId { get; }
    Task<RazorpayOrder> CreateOrderAsync(long amountPaise, string currency, string receipt, Dictionary<string, string> notes);
    Task<RazorpayOrder> GetOrderAsync(string orderId);
    bool IsValidPaymentSignature(string orderId, string paymentId, string signature);
}

/// <summary>
/// Talks to Razorpay's Orders API over plain HTTPS with HTTP Basic auth (key id as the
/// username, key secret as the password) — the same hand-rolled-typed-client shape every
/// other third party here uses (BrevoEmailService, GeminiService, BorzoClient), rather than
/// pulling in the official SDK for what amounts to two GET/POSTs and one HMAC.
///
/// Only two calls are needed for Standard Checkout: create the order the browser opens the
/// modal against, and re-read that order on the way back to confirm what was actually paid.
/// Everything else about the payment is settled by the signature check below.
/// </summary>
public class RazorpayClient(HttpClient http, IOptions<RazorpayOptions> options, ILogger<RazorpayClient> logger) : IRazorpayClient
{
    private readonly RazorpayOptions _options = options.Value;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool IsConfigured => _options.IsConfigured;
    public string KeyId => _options.KeyId;

    public async Task<RazorpayOrder> CreateOrderAsync(long amountPaise, string currency, string receipt, Dictionary<string, string> notes)
    {
        // Razorpay's own floor. Checked here as well as in the controller so no future caller
        // can get a 400 out of the gateway that reads like an outage in our logs.
        if (amountPaise < 100)
            throw new ArgumentOutOfRangeException(nameof(amountPaise), "Razorpay rejects orders below 100 paise (₹1).");

        var payload = new
        {
            amount = amountPaise,
            currency,
            receipt,
            notes,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.razorpay.com/v1/orders")
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };
        return await SendAsync(request, $"create order for {amountPaise} paise");
    }

    public async Task<RazorpayOrder> GetOrderAsync(string orderId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.razorpay.com/v1/orders/{Uri.EscapeDataString(orderId)}");
        return await SendAsync(request, $"read order {orderId}");
    }

    /// <summary>
    /// The whole point of the checkout round trip: Razorpay hands the browser
    /// razorpay_signature = HMAC-SHA256("{order_id}|{payment_id}", key_secret), and only
    /// someone holding the secret could have produced it. A browser that made the ids up,
    /// or replayed one order id against another payment id, fails here.
    /// </summary>
    public bool IsValidPaymentSignature(string orderId, string paymentId, string signature) =>
        RazorpaySignature.IsValid(orderId, paymentId, signature, _options.KeySecret);

    private async Task<RazorpayOrder> SendAsync(HttpRequestMessage request, string what)
    {
        if (!_options.IsConfigured)
            throw new RazorpayApiException("Online payments are not configured on this server.");

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.KeyId}:{_options.KeySecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request);
        }
        catch (Exception ex)
        {
            // Timeout / DNS / TLS — never the caller's fault, and never something to retry
            // blindly on a payment path (a retried create-order is a second real order).
            logger.LogError(ex, "Razorpay unreachable while trying to {What}", what);
            throw new RazorpayApiException("Could not reach the payment gateway. Please try again.");
        }

        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            // 401 here means OUR Razorpay credentials are wrong or revoked — a server
            // misconfiguration, never a signed-in cafe owner's problem. Logged loudly and
            // distinctly so it isn't mistaken for a card being declined.
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                logger.LogError("Razorpay rejected our credentials ({KeyId}) while trying to {What} — check Razorpay__KeyId/Razorpay__KeySecret", _options.KeyId, what);
            else
                logger.LogError("Razorpay failed to {What}: {Status} {Body}", what, response.StatusCode, body);

            throw new RazorpayApiException(
                response.StatusCode == HttpStatusCode.Unauthorized
                    ? "The payment gateway rejected this server's credentials."
                    : "The payment gateway could not process this request. Please try again.",
                response.StatusCode);
        }

        var order = JsonSerializer.Deserialize<RazorpayOrder>(body, JsonOptions);
        if (order is null || string.IsNullOrWhiteSpace(order.Id))
        {
            logger.LogError("Razorpay returned an unreadable order while trying to {What}: {Body}", what, body);
            throw new RazorpayApiException("The payment gateway returned an unexpected response.");
        }
        return order;
    }
}

/// <summary>
/// Kept separate from RazorpayClient (and static) so the one piece of this integration that
/// decides whether money was really paid can be unit-tested without an HttpClient — see
/// CafePosApi.Tests/RazorpaySignatureTests.cs.
/// </summary>
public static class RazorpaySignature
{
    public static bool IsValid(string? orderId, string? paymentId, string? signature, string? keySecret)
    {
        if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(paymentId)
            || string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(keySecret))
            return false;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(keySecret));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{orderId}|{paymentId}"))).ToLowerInvariant();

        // Fixed-time compare: a plain string == leaks, through how long it takes to fail,
        // how many leading characters of a guessed signature were right.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature.Trim().ToLowerInvariant()));
    }
}
