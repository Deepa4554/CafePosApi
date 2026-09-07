using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace CafePOS.Api.Infrastructure;

/// <summary>One cafe's own Razorpay credentials, already decrypted (see TenantSecretProtector).
/// Passed per call rather than injected, because unlike the platform's single key pair there is
/// a different one per tenant and the right one is only known once the order is loaded.</summary>
public record RazorpayCredentials(string KeyId, string KeySecret, string? WebhookSecret)
{
    public bool CanCharge => !string.IsNullOrWhiteSpace(KeyId) && !string.IsNullOrWhiteSpace(KeySecret);
    public bool CanVerifyWebhook => !string.IsNullOrWhiteSpace(WebhookSecret);
}

public interface ITenantRazorpayClient
{
    Task<RazorpayOrder> CreateOrderAsync(RazorpayCredentials creds, long amountPaise, string receipt, Dictionary<string, string> notes);
    Task<RazorpayOrder> GetOrderAsync(RazorpayCredentials creds, string orderId);
}

/// <summary>
/// The same two Razorpay Orders API calls <see cref="RazorpayClient"/> makes, but charged
/// against a CAFE's account instead of the platform's — this is what makes a guest's bill
/// payment land in the restaurant's bank account and not in ours.
///
/// Deliberately a sibling of RazorpayClient rather than a refactor of it. That one is bound to
/// IOptions&lt;RazorpayOptions&gt; and serves subscription billing, where the credentials are
/// process-wide and the money is genuinely the platform's; threading an optional credential
/// through every one of its call sites would blur two flows that should never be confused with
/// each other. The signature maths is shared, though — both go through
/// <see cref="RazorpaySignature"/>, so a payment can never be verified two different ways.
/// </summary>
public class TenantRazorpayClient(HttpClient http, ILogger<TenantRazorpayClient> logger) : ITenantRazorpayClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<RazorpayOrder> CreateOrderAsync(RazorpayCredentials creds, long amountPaise, string receipt, Dictionary<string, string> notes)
    {
        // Razorpay's own floor, checked here so a ₹0.50 bill fails as our validation message
        // rather than as a gateway 400 that reads like an outage in the logs.
        if (amountPaise < 100)
            throw new RazorpayApiException("This bill is below the ₹1 minimum for an online payment.");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.razorpay.com/v1/orders")
        {
            Content = JsonContent.Create(new { amount = amountPaise, currency = "INR", receipt, notes }, options: JsonOptions),
        };
        return await SendAsync(creds, request, $"create order for {amountPaise} paise");
    }

    public async Task<RazorpayOrder> GetOrderAsync(RazorpayCredentials creds, string orderId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.razorpay.com/v1/orders/{Uri.EscapeDataString(orderId)}");
        return await SendAsync(creds, request, $"read order {orderId}");
    }

    private async Task<RazorpayOrder> SendAsync(RazorpayCredentials creds, HttpRequestMessage request, string what)
    {
        if (!creds.CanCharge)
            throw new RazorpayApiException("This cafe hasn't finished setting up online payments.");

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{creds.KeyId}:{creds.KeySecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Razorpay was unreachable trying to {What}", what);
            throw new RazorpayApiException("Couldn't reach the payment gateway. Please try again.");
        }

        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            // The cafe's own key being wrong is by far the likeliest cause of a 401 here, and it
            // is worth saying plainly — the Owner typed it, and only they can fix it.
            logger.LogWarning("Razorpay refused to {What}: {Status} {Body}", what, response.StatusCode, body);
            throw new RazorpayApiException(
                response.StatusCode == HttpStatusCode.Unauthorized
                    ? "This cafe's Razorpay keys were rejected. Check them in Cafe Settings."
                    : "The payment gateway couldn't take this request.",
                response.StatusCode);
        }

        return JsonSerializer.Deserialize<RazorpayOrder>(body, JsonOptions)
            ?? throw new RazorpayApiException("The payment gateway sent back something unreadable.");
    }
}
