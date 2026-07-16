using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace CafePOS.Api.Infrastructure;

public class ResendOptions
{
    /// <summary>API key from resend.com (Dashboard → API Keys). Free tier covers OTP volume.</summary>
    public string ApiKey { get; set; } = "";
    /// <summary>
    /// Sender address. Until a custom domain is verified in Resend, this must stay
    /// "onboarding@resend.dev" — Resend's shared test sender, which works for any
    /// recipient with no domain setup required.
    /// </summary>
    public string FromEmail { get; set; } = "onboarding@resend.dev";
}

public interface IEmailService
{
    Task SendOtpAsync(string toEmail, string code);
}

/// <summary>
/// Sends mail via Resend's HTTP API (api.resend.com) instead of raw SMTP — cloud hosts
/// like Render frequently block or silently stall outbound SMTP ports, which left the
/// old Gmail-SMTP implementation hanging or failing unpredictably. A plain HTTPS POST
/// has none of that risk. If unconfigured, falls back to logging the code so local
/// development never gets blocked on email setup.
/// </summary>
public class ResendEmailService(HttpClient http, IOptions<ResendOptions> options, ILogger<ResendEmailService> logger) : IEmailService
{
    private readonly ResendOptions _options = options.Value;

    public async Task SendOtpAsync(string toEmail, string code)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            logger.LogWarning("Resend:ApiKey not configured — OTP for {Email} is {Code}", toEmail, code);
            return;
        }

        var body = $"Hi there,\n\n" +
                   $"This is to inform you that you have registered successfully with PrabandhOS.\n\n" +
                   $"Here is your OTP to verify your email:-\n\n" +
                   $"{code}\n\n" +
                   $"This code expires in 10 minutes. If you didn't request this, you can safely ignore this email.\n\n" +
                   $"Regards,\n" +
                   $"PrabandhOS Team";

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
        {
            Content = JsonContent.Create(new
            {
                from = _options.FromEmail,
                to = new[] { toEmail },
                subject = "Verify your email",
                text = body,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        try
        {
            var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                logger.LogError("Resend API returned {Status} sending OTP to {Email}: {Body}", response.StatusCode, toEmail, responseBody);
            }
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Resend request failed (network) sending OTP to {Email}", toEmail);
        }
    }
}
