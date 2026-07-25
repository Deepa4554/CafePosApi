using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

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
/// Uses MailKit rather than the legacy System.Net.Mail.SmtpClient: on Render's Linux
/// containers, smtp.gmail.com resolves to an IPv6 address first, but the container has
/// no outbound IPv6 route — System.Net.Mail.SmtpClient only tries the first resolved
/// address and fails outright (SocketException 101 "Network is unreachable"), which is
/// exactly the silent production failure this class used to have. MailKit's ConnectAsync
/// walks every resolved address until one connects, so it falls through to IPv4 fine.
///
/// Note: the caller (AuthController.IssueOtpAsync) fires this without awaiting it, so a
/// slow/hung connection attempt can't make the HTTP response (and the mobile client) hang
/// along with it.
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

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_options.GmailAddress));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = purpose == OtpPurpose.PasswordReset ? "Reset your password" : "Verify your email";
        message.Body = new TextPart("plain")
        {
            Text = $"Hi there,\n\n" +
                   $"{intro}\n\n" +
                   $"Here is your OTP to verify your email:-\n\n" +
                   $"{code}\n\n" +
                   $"This code expires in 10 minutes. If you didn't request this, you can safely ignore this email.\n\n" +
                   $"Regards,\n" +
                   $"PrabandhOS Team",
        };

        using var client = new SmtpClient { Timeout = 8000 };
        try
        {
            await client.ConnectAsync("smtp.gmail.com", 587, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(_options.GmailAddress, _options.GmailAppPassword);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SMTP send failed for OTP to {Email}", toEmail);
        }
    }
}
