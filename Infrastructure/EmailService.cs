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

public interface IEmailService
{
    Task SendOtpAsync(string toEmail, string code);
}

/// <summary>
/// Sends mail via Gmail's SMTP relay (smtp.gmail.com:587) — free, no third-party
/// account needed beyond the Gmail address itself. If unconfigured, falls back to
/// logging the code so local development never gets blocked on email setup.
/// </summary>
public class SmtpEmailService(IOptions<EmailOptions> options, ILogger<SmtpEmailService> logger) : IEmailService
{
    private readonly EmailOptions _options = options.Value;

    public async Task SendOtpAsync(string toEmail, string code)
    {
        if (string.IsNullOrWhiteSpace(_options.GmailAddress) || string.IsNullOrWhiteSpace(_options.GmailAppPassword))
        {
            logger.LogWarning("Email:GmailAddress/GmailAppPassword not configured — OTP for {Email} is {Code}", toEmail, code);
            return;
        }

        using var client = new SmtpClient("smtp.gmail.com", 587)
        {
            Credentials = new NetworkCredential(_options.GmailAddress, _options.GmailAppPassword),
            EnableSsl = true,
        };
        using var message = new MailMessage(_options.GmailAddress, toEmail)
        {
            Subject = "Your CafePOS verification code",
            Body = $"Your verification code is {code}.\n\nIt expires in 10 minutes. If you didn't request this, ignore this email.",
        };
        await client.SendMailAsync(message);
    }
}
