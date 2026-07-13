using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CafePOS.Api.Domain;
using Microsoft.IdentityModel.Tokens;

namespace CafePOS.Api.Infrastructure;

public class JwtOptions
{
    public string Secret { get; set; } = "";
    public string Issuer { get; set; } = "CafePOS.Api";
    public string Audience { get; set; } = "CafePOS.App";
    public int AccessTokenMinutes { get; set; } = 30;
    public int RefreshTokenDays { get; set; } = 30;
}

public interface IJwtTokenService
{
    string CreateAccessToken(AppUser user);
    string CreateRefreshToken();
    SigningCredentials SigningCredentials { get; }
}

public class JwtTokenService(Microsoft.Extensions.Options.IOptions<JwtOptions> options) : IJwtTokenService
{
    private readonly JwtOptions _options = options.Value;

    public SigningCredentials SigningCredentials =>
        new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Secret)), SecurityAlgorithms.HmacSha256);

    public string CreateAccessToken(AppUser user)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(ClaimTypes.Name, user.Name),
            new(ClaimTypes.Role, user.Role.ToString()),
            new("tenantId", user.TenantId.ToString()),
            new("isPlatformAdmin", user.IsPlatformAdmin ? "true" : "false"),
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes),
            signingCredentials: SigningCredentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string CreateRefreshToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
}
