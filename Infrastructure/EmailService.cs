using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace CafePOS.Api.Infrastructure;

public class EmailOptions
{
    /// <summary>The Gmail address emails are sent from, e.g. "yourcafe@gmail.com".</summary>
    public string GmailAddress { get; set; } = "";
    /// <summary>
    /// A Gmail "App Password" (16 characters, generated under Google Account →
    /// Security → 2-Step Verification → App passwords) — NOT your normal Gmail
    /// password. Gmail requires 2-Step Verification to be on before it'll offer this.
    /// </summary>
    public string GmailAppPassword { get; set; } = "";
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
/// Sends mail via Gmail's SMTP relay (smtp.gmail.com:587) — free, no third-party
/// account needed beyond the Gmail address itself. If unconfigured, falls back to
/// logging the code so local development never gets blocked on email setup.
///
/// Note: the caller (AuthController.IssueOtpAsync) fires this without awaiting it —
/// SmtpClient.Timeout doesn't reliably abort a hung TCP connect on Linux, so if Gmail's
/// SMTP is slow/blocked from the host, this call can still hang well past the 8s below;
/// the fire-and-forget wrapper is what actually keeps the HTTP response (and the mobile
/// client) from hanging along with it, regardless of whether this timeout kicks in.
/// </summary>
public class SmtpEmailService(IOptions<EmailOptions> options, ILogger<SmtpEmailService> logger) : IEmailService
{
    private readonly EmailOptions _options = options.Value;

    public async Task SendOtpAsync(string toEmail, string code, OtpPurpose purpose)
    {
        if (string.IsNullOrWhiteSpace(_options.GmailAddress) || string.IsNullOrWhiteSpace(_options.GmailAppPassword))
        {
            logger.LogWarning("Email:GmailAddress/GmailAppPassword not configured — OTP for {Email} is {Code}", toEmail, code);
            return;
        }

        var intro = purpose == OtpPurpose.PasswordReset
            ? "We received a request to reset the password on your PrabandhOS account."
            : "This is to inform you that you have registered successfully with PrabandhOS.";

        using var client = new SmtpClient("smtp.gmail.com", 587)
        {
            Credentials = new NetworkCredential(_options.GmailAddress, _options.GmailAppPassword),
            EnableSsl = true,
            Timeout = 8000,
        };
        using var message = new MailMessage(_options.GmailAddress, toEmail)
        {
            Subject = purpose == OtpPurpose.PasswordReset ? "Reset your password" : "Verify your email",
            Body = $"Hi there,\n\n" +
                   $"{intro}\n\n" +
                   $"Here is your OTP to verify your email:-\n\n" +
                   $"{code}\n\n" +
                   $"This code expires in 10 minutes. If you didn't request this, you can safely ignore this email.\n\n" +
                   $"Regards,\n" +
                   $"PrabandhOS Team",
        };

        try
        {
            await client.SendMailAsync(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SMTP send failed for OTP to {Email}", toEmail);
        }
    }
}
