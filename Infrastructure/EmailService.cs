using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CafePOS.Api.Infrastructure;

public class EmailOptions
{
    /// <summary>Brevo (formerly Sendinblue) transactional-email API key — Dashboard →
    /// SMTP & API → API Keys.</summary>
    public string BrevoApiKey { get; set; } = "";
    /// <summary>Must be a sender verified in Brevo (Senders, Domains & Dedicated IPs →
    /// Senders), or Brevo rejects the send.</summary>
    public string SenderEmail { get; set; } = "";
    public string SenderName { get; set; } = "PrabandhOS";
}

/// <summary>What the OTP is for — the two flows share the same code/expiry mechanics
/// but need different wording, since "you have registered successfully" is flat-out
/// wrong to say to someone who's resetting the password on an existing account.</summary>
public enum OtpPurpose
{
    Signup,
    PasswordReset,
}

public interface IEmailService
{
    Task SendOtpAsync(string toEmail, string code, OtpPurpose purpose);
}

/// <summary>
/// Sends mail via Brevo's transactional email HTTP API (api.brevo.com) rather than SMTP.
/// Render blocks outbound SMTP ports (25/465/587) at the network/firewall level for every
/// service on the platform, for spam-abuse reasons — confirmed here first by an instant
/// "Network is unreachable" and, after trying a different mail client, a hard connect
/// timeout. No .NET SMTP client can get through from Render regardless of library. Brevo's
/// API runs over plain HTTPS (443), which isn't blocked. If unconfigured, falls back to
/// logging the code so local dev isn't blocked on setup.
/// </summary>
public class BrevoEmailService(HttpClient http, IOptions<EmailOptions> options, ILogger<BrevoEmailService> logger) : IEmailService
{
    private readonly EmailOptions _options = options.Value;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task SendOtpAsync(string toEmail, string code, OtpPurpose purpose)
    {
        if (string.IsNullOrWhiteSpace(_options.BrevoApiKey) || string.IsNullOrWhiteSpace(_options.SenderEmail))
        {
            logger.LogWarning("Email:BrevoApiKey/SenderEmail not configured — OTP for {Email} is {Code}", toEmail, code);
            return;
        }

        var intro = purpose == OtpPurpose.PasswordReset
            ? "We received a request to reset the password on your PrabandhOS account."
            : "This is to inform you that you have registered successfully with PrabandhOS.";

        var payload = new BrevoSendRequest(
            new BrevoSender(_options.SenderName, _options.SenderEmail),
            [new BrevoRecipient(toEmail)],
            purpose == OtpPurpose.PasswordReset ? "Reset your password" : "Verify your email",
            $"Hi there,\n\n" +
            $"{intro}\n\n" +
            $"Here is your OTP to verify your email:-\n\n" +
            $"{code}\n\n" +
            $"This code expires in 10 minutes. If you didn't request this, you can safely ignore this email.\n\n" +
            $"Regards,\n" +
            $"PrabandhOS Team");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email")
            {
                Content = JsonContent.Create(payload, options: JsonOptions),
            };
            request.Headers.Add("api-key", _options.BrevoApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                logger.LogError("Brevo send failed for OTP to {Email}: {Status} {Body}", toEmail, response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Brevo send failed for OTP to {Email}", toEmail);
        }
    }

    private record BrevoSendRequest(BrevoSender Sender, List<BrevoRecipient> To, string Subject, string TextContent);
    private record BrevoSender(string Name, string Email);
    private record BrevoRecipient(string Email);
}
