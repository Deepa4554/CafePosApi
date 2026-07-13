using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CafePOS.Api.Controllers;

/// <summary>
/// Every endpoint here is auth-sensitive (login, OTP, password reset, cafe
/// registration) — the whole controller gets the strict per-IP AuthLimiter
/// policy instead of only the generous 200/min global one, so password/OTP
/// brute-forcing against a single account actually gets blocked.
/// </summary>
[ApiController]
[Route("api/auth")]
[EnableRateLimiting("AuthLimiter")]
public class AuthController(
    CafePosDbContext db,
    IJwtTokenService tokenService,
    IOptions<JwtOptions> jwtOptions,
    IPasswordHasher<AppUser> hasher,
    IAuditService audit,
    IEmailService email) : ControllerBase
{
    /// <summary>
    /// Step 1 of cafe signup: emails a 6-digit code to prove the caller controls this
    /// address before /register-cafe will create an account with it. Reused on resend —
    /// each call invalidates any previous unused code for the same email.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("request-otp")]
    public async Task<IActionResult> RequestOtp(RequestOtpRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
            throw new ApiValidationException("A valid email is required.");

        var normalizedEmail = req.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == normalizedEmail))
            throw new ApiConflictException("An account with this email already exists.");

        await IssueOtpAsync(normalizedEmail);
        return NoContent();
    }

    /// <summary>
    /// Step 1 of password reset: emails the same kind of 6-digit code, but only if the
    /// address actually has an account. Always returns 204 either way — never reveal
    /// whether an email is registered, or this becomes an account-enumeration probe.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("forgot-password/request-otp")]
    public async Task<IActionResult> ForgotPasswordRequestOtp(ForgotPasswordRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
            throw new ApiValidationException("A valid email is required.");

        var normalizedEmail = req.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == normalizedEmail))
            await IssueOtpAsync(normalizedEmail);

        return NoContent();
    }

    /// <summary>Step 2 of password reset: verifies the code and sets the new password.
    /// Also revokes the refresh token, so a reset logs the account out everywhere —
    /// otherwise a stolen device/session would survive a password change.</summary>
    [AllowAnonymous]
    [HttpPost("forgot-password/reset")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest req)
    {
        if (req.NewPassword.Length < 6)
            throw new ApiValidationException("New password must be at least 6 characters.");

        var normalizedEmail = req.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
        // Same generic error as a wrong code either way — don't reveal account existence.
        if (user is null)
            throw new ApiValidationException("Incorrect verification code.");

        await ConsumeOtpAsync(normalizedEmail, req.Otp);

        user.PasswordHash = hasher.HashPassword(user, req.NewPassword);
        user.RefreshToken = null;
        user.RefreshTokenExpiresAt = null;
        await db.SaveChangesAsync();
        await audit.LogAsync(AuditAction.PasswordChange, AuditResource.Auth, user.Id.ToString(),
            $"{user.Name} reset their password via email verification.", AuditSeverity.High, user.Id, user.Name, user.TenantId);

        return NoContent();
    }

    private async Task IssueOtpAsync(string normalizedEmail)
    {
        var stale = db.EmailOtps.Where(o => o.Email == normalizedEmail && !o.Used);
        db.EmailOtps.RemoveRange(stale);

        var code = Random.Shared.Next(100_000, 999_999).ToString();
        db.EmailOtps.Add(new EmailOtp { Email = normalizedEmail, Code = code, ExpiresAt = DateTime.UtcNow.AddMinutes(10) });
        await db.SaveChangesAsync();

        await email.SendOtpAsync(normalizedEmail, code);
    }

    private async Task ConsumeOtpAsync(string normalizedEmail, string suppliedOtp)
    {
        var otp = await db.EmailOtps
            .Where(o => o.Email == normalizedEmail && !o.Used)
            .OrderByDescending(o => o.Id)
            .FirstOrDefaultAsync();
        if (otp is null || otp.ExpiresAt < DateTime.UtcNow)
            throw new ApiValidationException("Verification code has expired or wasn't requested. Request a new one.");
        if (otp.Attempts >= 5)
            throw new ApiValidationException("Too many incorrect attempts. Request a new code.");
        if (otp.Code != suppliedOtp.Trim())
        {
            otp.Attempts++;
            await db.SaveChangesAsync();
            throw new ApiValidationException("Incorrect verification code.");
        }
        otp.Used = true;
    }

    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
            throw new ApiValidationException("A valid email is required.");
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ApiValidationException("Name is required.");
        if (req.Password.Length < 6)
            throw new ApiValidationException("Password must be at least 6 characters.");

        var normalizedEmail = req.Email.Trim().ToLowerInvariant();

        // This endpoint always attaches the new account to the default tenant (below) —
        // that's only safe for the app's own demo/role-switcher accounts (fixed emails
        // like "owner@cafepos.local", matching AuthRepository.demoEmailFor). Without this
        // check, anyone could self-register with role=Owner against a real cafe's tenant.
        // Real cafes must sign up via /register-cafe, which always creates a fresh,
        // isolated tenant instead of attaching to an existing one.
        var expectedDemoEmail = $"{req.Role.ToString().ToLowerInvariant()}@cafepos.local";
        if (!normalizedEmail.Equals(expectedDemoEmail, StringComparison.Ordinal))
            throw new ApiValidationException("Self-registration is only available for demo accounts. Use /auth/register-cafe to create a new cafe.");

        if (await db.Users.AnyAsync(u => u.Email == normalizedEmail))
            throw new ApiConflictException("An account with this email already exists.");

        var user = new AppUser
        {
            // Always the default tenant — see the demo-email check above for why
            // that's the only safe case for this endpoint.
            TenantId = Tenant.DefaultTenantId,
            Email = normalizedEmail,
            Name = req.Name.Trim(),
            Role = req.Role,
            PasswordHash = "",
        };
        user.PasswordHash = hasher.HashPassword(user, req.Password);

        db.Users.Add(user);
        await IssueTokensAsync(user);
        await db.SaveChangesAsync();

        return Ok(BuildResponse(user, tokenService.CreateAccessToken(user)));
    }

    /// <summary>
    /// Onboards a brand-new cafe: creates an isolated Tenant, its Owner account, and
    /// default CafeSettings/Subscription (14-day trial) — all scoped to the new
    /// tenant so it starts completely empty and can never see another cafe's data.
    /// Use this (not /register) for "create your cafe" signup; /register is kept for
    /// the existing demo/role-switcher accounts, which stay on the default tenant.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("register-cafe")]
    public async Task<ActionResult<AuthResponse>> RegisterCafe(RegisterCafeRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.CafeName))
            throw new ApiValidationException("Cafe name is required.");
        if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
            throw new ApiValidationException("A valid email is required.");
        if (string.IsNullOrWhiteSpace(req.OwnerName))
            throw new ApiValidationException("Owner name is required.");
        if (req.Password.Length < 6)
            throw new ApiValidationException("Password must be at least 6 characters.");
        var normalizedPhone = (req.Phone ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(normalizedPhone, @"^\d{10}$"))
            throw new ApiValidationException("A valid 10-digit mobile number is required.");

        var normalizedEmail = req.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == normalizedEmail))
            throw new ApiConflictException("An account with this email already exists.");

        await ConsumeOtpAsync(normalizedEmail, req.Otp);

        var tenant = new Tenant { Name = req.CafeName.Trim(), Slug = await GenerateUniqueSlugAsync(req.CafeName) };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(); // assigns tenant.Id before anything below references it

        var user = new AppUser
        {
            TenantId = tenant.Id,
            Email = normalizedEmail,
            Phone = normalizedPhone,
            Name = req.OwnerName.Trim(),
            Role = AppRole.Owner,
            PasswordHash = "",
        };
        user.PasswordHash = hasher.HashPassword(user, req.Password);
        db.Users.Add(user);

        db.Settings.Add(new CafeSettings { TenantId = tenant.Id, BusinessName = tenant.Name });
        db.Subscriptions.Add(new Subscription
        {
            TenantId = tenant.Id,
            Plan = SubscriptionTier.FreeTrial,
            PlanExpiresAt = DateTime.UtcNow.AddDays(7),
        });

        await IssueTokensAsync(user);
        await db.SaveChangesAsync();

        return Ok(BuildResponse(user, tokenService.CreateAccessToken(user)));
    }

    private async Task<string> GenerateUniqueSlugAsync(string cafeName)
    {
        var baseSlug = new string(cafeName.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (baseSlug.Contains("--")) baseSlug = baseSlug.Replace("--", "-");
        baseSlug = baseSlug.Trim('-');
        if (string.IsNullOrEmpty(baseSlug)) baseSlug = "cafe";

        var slug = baseSlug;
        var suffix = 1;
        while (await db.Tenants.AnyAsync(t => t.Slug == slug))
            slug = $"{baseSlug}-{++suffix}";
        return slug;
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest req)
    {
        var normalizedEmail = req.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
        if (user is null || !user.IsActive)
        {
            await audit.LogAsync(AuditAction.FailedLogin, AuditResource.Auth, null, $"Failed login attempt for {normalizedEmail}.", AuditSeverity.Critical);
            throw new UnauthorizedAccessException("Invalid email or password.");
        }

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            await audit.LogAsync(AuditAction.FailedLogin, AuditResource.Auth, user.Id.ToString(), $"Failed login attempt for {normalizedEmail}.", AuditSeverity.Critical, user.Id, user.Name, user.TenantId);
            throw new UnauthorizedAccessException("Invalid email or password.");
        }

        await IssueTokensAsync(user);
        await db.SaveChangesAsync();
        await audit.LogAsync(AuditAction.Login, AuditResource.Auth, user.Id.ToString(), $"{user.Name} signed in.", AuditSeverity.Low, user.Id, user.Name, user.TenantId);

        return Ok(BuildResponse(user, tokenService.CreateAccessToken(user)));
    }

    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh(RefreshRequest req)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.RefreshToken == req.RefreshToken);
        if (user is null || user.RefreshTokenExpiresAt is null || user.RefreshTokenExpiresAt < DateTime.UtcNow)
            throw new UnauthorizedAccessException("Refresh token is invalid or expired.");

        await IssueTokensAsync(user);
        await db.SaveChangesAsync();

        return Ok(BuildResponse(user, tokenService.CreateAccessToken(user)));
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var user = await CurrentUserAsync();
        user.RefreshToken = null;
        user.RefreshTokenExpiresAt = null;
        await db.SaveChangesAsync();
        await audit.LogAsync(AuditAction.Logout, AuditResource.Auth, user.Id.ToString(), $"{user.Name} signed out.", AuditSeverity.Low, user.Id, user.Name);
        return NoContent();
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<UserDto>> Me()
    {
        var user = await CurrentUserAsync();
        return UserDto.From(user);
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest req)
    {
        var user = await CurrentUserAsync();
        if (hasher.VerifyHashedPassword(user, user.PasswordHash, req.CurrentPassword) == PasswordVerificationResult.Failed)
            throw new ApiValidationException("Current password is incorrect.");
        if (req.NewPassword.Length < 6)
            throw new ApiValidationException("New password must be at least 6 characters.");

        user.PasswordHash = hasher.HashPassword(user, req.NewPassword);
        await db.SaveChangesAsync();
        await audit.LogAsync(AuditAction.PasswordChange, AuditResource.Auth, user.Id.ToString(), $"{user.Name} changed their password.", AuditSeverity.Medium, user.Id, user.Name);
        return NoContent();
    }

    private async Task<AppUser> CurrentUserAsync()
    {
        var idClaim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var id = int.Parse(idClaim!);
        return await db.Users.FindAsync(id) ?? throw new KeyNotFoundException("User not found.");
    }

    private async Task IssueTokensAsync(AppUser user)
    {
        user.RefreshToken = tokenService.CreateRefreshToken();
        user.RefreshTokenExpiresAt = DateTime.UtcNow.AddDays(jwtOptions.Value.RefreshTokenDays);
        await Task.CompletedTask;
    }

    private static AuthResponse BuildResponse(AppUser user, string accessToken) =>
        new(accessToken, user.RefreshToken!, UserDto.From(user));
}
