using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CafePOS.Api.Infrastructure;

public static class ServiceApiKeyDefaults
{
    public const string AuthenticationScheme = "ServiceApiKey";
    public const string HeaderName = "X-Service-Api-Key";
}

/// <summary>
/// A second auth scheme (alongside the normal JWT bearer scheme) for the standalone Node
/// whatsapp-service to call CafePosApi's api/internal/whatsapp/* endpoints — a background
/// process has no user/tenant JWT to present. The shared secret (WhatsAppService:ApiKey, same
/// value used in both directions — see WhatsAppEventPublisher) only proves "this caller is the
/// trusted whatsapp-service process," NOT "this caller may act as any tenant": every internal
/// endpoint still takes an explicit tenantId and filters server-side, exactly like the existing
/// anonymous PublicController pattern. See Policies.ServiceOnly.
/// </summary>
public class ServiceApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<WhatsAppServiceOptions> whatsAppOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ServiceApiKeyDefaults.HeaderName, out var provided) || provided.Count != 1)
            return Task.FromResult(AuthenticateResult.NoResult());

        var expected = whatsAppOptions.Value.ApiKey;
        if (string.IsNullOrEmpty(expected) || !FixedTimeEquals(provided[0]!, expected))
            return Task.FromResult(AuthenticateResult.Fail("Invalid service API key."));

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "whatsapp-service")], ServiceApiKeyDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), ServiceApiKeyDefaults.AuthenticationScheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return aBytes.Length == bBytes.Length && CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
