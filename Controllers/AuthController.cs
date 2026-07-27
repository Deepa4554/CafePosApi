using System.Data;
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
using Npgsql;

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
    IEmailService email,
    ILogger<AuthController> logger) : ControllerBase
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

        await IssueOtpAsync(normalizedEmail, OtpPurpose.Signup);
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
            await IssueOtpAsync(normalizedEmail, OtpPurpose.PasswordReset);

        return NoContent();
    }

    /// <summary>Step 2 of password reset: verifies the code and sets the new password.
    /// Also revokes every device's refresh token, so a reset logs the account out
    /// everywhere — otherwise a stolen device/session would survive a password change.</summary>
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
        await RevokeAllSessionsAsync(user.Id);
        await db.SaveChangesAsync();
        await audit.LogAsync(AuditAction.PasswordChange, AuditResource.Auth, user.Id.ToString(),
            $"{user.Name} reset their password via email verification.", AuditSeverity.High, user.Id, user.Name, user.TenantId);

        return NoContent();
    }

    private async Task IssueOtpAsync(string normalizedEmail, OtpPurpose purpose)
    {
        var stale = db.EmailOtps.Where(o => o.Email == normalizedEmail && !o.Used);
        db.EmailOtps.RemoveRange(stale);

        // Crypto RNG, not Random.Shared — this is a security code, and a seeded/predictable
        // generator would let OTPs be guessed. Upper bound exclusive: covers 100000–999999.
        var code = System.Security.Cryptography.RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString();
        db.EmailOtps.Add(new EmailOtp { Email = normalizedEmail, Code = code, ExpiresAt = DateTime.UtcNow.AddMinutes(10) });
        await db.SaveChangesAsync();

        // The OTP is already persisted, so the request doesn't need to wait on the
        // email actually going out — awaiting it here held the whole HTTP response
        // (and the mobile client) hostage whenever the SMTP connection was slow or
        // silently blocked, since SmtpClient.Timeout doesn't reliably abort a hung
        // TCP connect on Linux. IEmailService is a singleton, so it's safe to use
        // after this request's scope ends.
        _ = email.SendOtpAsync(normalizedEmail, code, purpose).ContinueWith(
            t => logger.LogError(t.Exception, "Failed to send OTP email to {Email}", normalizedEmail),
            TaskContinuationOptions.OnlyOnFaulted);
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
        // like "owner@prabandhos.local", matching AuthRepository.demoEmailFor). Without this
        // check, anyone could self-register with role=Owner against a real cafe's tenant.
        // Real cafes must sign up via /register-cafe, which always creates a fresh,
        // isolated tenant instead of attaching to an existing one.
        var expectedDemoEmail = $"{req.Role.ToString().ToLowerInvariant()}@prabandhos.local";
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
        await db.SaveChangesAsync(); // assigns user.Id before IssueTokensAsync references it
        var refreshToken = await IssueTokensAsync(user);
        await db.SaveChangesAsync();

        return Ok(BuildResponse(user, tokenService.CreateAccessToken(user), refreshToken));
    }

    /// <summary>
    /// Onboards a brand-new cafe: creates an isolated Tenant, its Owner account, and
    /// default CafeSettings/Subscription (7-day trial) — all scoped to the new
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

        await db.SaveChangesAsync(); // assigns user.Id before IssueTokensAsync references it
        var refreshToken = await IssueTokensAsync(user);
        await db.SaveChangesAsync();

        return Ok(BuildResponse(user, tokenService.CreateAccessToken(user), refreshToken));
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

        var refreshToken = await IssueTokensAsync(user);
        await db.SaveChangesAsync();
        await audit.LogAsync(AuditAction.Login, AuditResource.Auth, user.Id.ToString(), $"{user.Name} signed in.", AuditSeverity.Low, user.Id, user.Name, user.TenantId);

        return Ok(BuildResponse(user, tokenService.CreateAccessToken(user), refreshToken));
    }

    /// <summary>
    /// Rotates this one device's session: the presented token is marked revoked and
    /// a fresh one takes its place for the same device, so every other device's own
    /// session (see RefreshTokenEntry's doc comment) is completely untouched. A
    /// revoked or expired token — including one that's already been rotated away by
    /// this same device a moment earlier — is always just "invalid", never silently
    /// re-accepted.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh([FromBody] RefreshRequest? req)
    {
        var presentedToken = req?.RefreshToken;
        if (string.IsNullOrWhiteSpace(presentedToken))
            throw new UnauthorizedAccessException("Refresh token is invalid or expired.");

        var entry = await db.RefreshTokens.FirstOrDefaultAsync(t => t.Token == presentedToken);
        if (entry is null || entry.RevokedAt is not null || entry.ExpiresAt < DateTime.UtcNow)
            throw new UnauthorizedAccessException("Refresh token is invalid or expired.");

        var user = await db.Users.FindAsync(entry.UserId);
        if (user is null || !user.IsActive)
            throw new UnauthorizedAccessException("Refresh token is invalid or expired.");

        entry.RevokedAt = DateTime.UtcNow;
        var newRefreshToken = await IssueTokensAsync(user);
        await db.SaveChangesAsync();

        return Ok(BuildResponse(user, tokenService.CreateAccessToken(user), newRefreshToken));
    }

    /// <summary>Revokes only the calling device's own session — every other device
    /// this account is logged in on stays signed in. The whole request body is
    /// optional (not just RefreshToken inside it): an already-cleared client (local
    /// storage wiped some other way) or an older app build that predates this
    /// endpoint taking a body at all can both still call this with no body and no
    /// 400 — there's just nothing to revoke server-side in that case.</summary>
    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest? req)
    {
        var user = await CurrentUserAsync();
        var presentedToken = req?.RefreshToken;
        if (!string.IsNullOrWhiteSpace(presentedToken))
        {
            var entry = await db.RefreshTokens.FirstOrDefaultAsync(t => t.Token == presentedToken && t.UserId == user.Id);
            if (entry is not null) entry.RevokedAt = DateTime.UtcNow;
        }
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
        // Same log-out-everywhere rule as the OTP reset flow above — a password change
        // that leaves every existing refresh token alive would let a stolen session
        // survive the very action meant to end it. The caller's current ACCESS token
        // stays valid until it expires (≤ AccessTokenMinutes); the next refresh
        // requires signing in with the new password.
        await RevokeAllSessionsAsync(user.Id);
        await db.SaveChangesAsync();
        await audit.LogAsync(AuditAction.PasswordChange, AuditResource.Auth, user.Id.ToString(), $"{user.Name} changed their password.", AuditSeverity.Medium, user.Id, user.Name);
        return NoContent();
    }

    /// <summary>
    /// Self-service, irreversible: permanently deletes this Owner's entire cafe — every
    /// order, menu item, inventory record, staff login, everything tenant-scoped — plus
    /// the Tenant row itself and every AppUser login under it (this one included). Owner-only
    /// (Policies.OwnerOnly, not OwnerOrManager) since a Manager account should never be able
    /// to end the whole cafe. No platform-admin tooling exists for this yet, so it's built as
    /// self-service instead: naturally scoped to the caller's own tenant only, can never touch
    /// another cafe's data, and needs no elevated role beyond what an Owner already has.
    ///
    /// Deletion order isn't hand-maintained (54+ tenant-scoped tables and growing) — it's
    /// discovered at request time from Postgres' own catalogs: every table with a "TenantId"
    /// column, plus the real FK constraints between them, topologically sorted so a
    /// referencing row is always deleted before the row it points at. That self-corrects as
    /// the schema grows, instead of silently drifting out of date the way a hard-coded list
    /// would. RefreshTokens is the one table that references Users without carrying its own
    /// TenantId column, so it's cleaned up explicitly by UserId lookup first.
    /// </summary>
    [Authorize(Policy = Policies.OwnerOnly)]
    [HttpPost("delete-my-account")]
    public async Task<ActionResult<DeleteAccountPlanDto>> DeleteMyAccount(DeleteAccountRequest req, [FromQuery] bool dryRun = false)
    {
        var user = await CurrentUserAsync();
        if (hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password) == PasswordVerificationResult.Failed)
            throw new ApiValidationException("Password is incorrect.");
        if (!db.Database.IsRelational())
            throw new ApiValidationException("Account deletion isn't available in this environment.");

        var tenantId = user.TenantId;

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var wasClosed = conn.State != ConnectionState.Open;
        if (wasClosed) await conn.OpenAsync();
        try
        {
            var tenantTables = new List<string>();
            await using (var cmd = new NpgsqlCommand(
                "SELECT table_name FROM information_schema.columns WHERE table_schema = 'public' AND column_name = 'TenantId'",
                conn))
            await using (var reader = await cmd.ExecuteReaderAsync())
                while (await reader.ReadAsync()) tenantTables.Add(reader.GetString(0));

            var edges = new List<(string Child, string Parent)>();
            await using (var cmd = new NpgsqlCommand("""
                SELECT tc.table_name, ccu.table_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.constraint_column_usage ccu ON tc.constraint_name = ccu.constraint_name
                WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = 'public'
                """, conn))
            await using (var reader = await cmd.ExecuteReaderAsync())
                while (await reader.ReadAsync()) edges.Add((reader.GetString(0), reader.GetString(1)));

            var order = TopologicalDeletionOrder(tenantTables, edges);

            // dryRun inspects the exact plan (table order + how many rows each holds for this
            // tenant right now) without deleting anything — call this first to sanity-check
            // before the real, irreversible run. Read-only, no transaction needed.
            if (dryRun)
            {
                var counts = new List<TablePlanDto>();
                foreach (var table in order)
                {
                    await using var cmd = new NpgsqlCommand($"""SELECT COUNT(*) FROM "{table}" WHERE "TenantId" = @tid""", conn);
                    cmd.Parameters.AddWithValue("tid", tenantId);
                    var n = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
                    counts.Add(new TablePlanDto(table, n));
                }
                return new DeleteAccountPlanDto(tenantId, counts);
            }

            logger.LogWarning("Deleting tenant {TenantId} ({Email}) — self-service account deletion.", tenantId, user.Email);

            await using var txn = await conn.BeginTransactionAsync();
            try
            {
                // RefreshTokens references Users.UserId but has no TenantId column of its own,
                // so it's invisible to the discovery query above — delete by explicit lookup
                // before Users (which the discovery/topological sort below does cover).
                await using (var cmd = new NpgsqlCommand("""
                    DELETE FROM "RefreshTokens" WHERE "UserId" IN (SELECT "Id" FROM "Users" WHERE "TenantId" = @tid)
                    """, conn, txn))
                {
                    cmd.Parameters.AddWithValue("tid", tenantId);
                    await cmd.ExecuteNonQueryAsync();
                }

                foreach (var table in order)
                {
                    await using var cmd = new NpgsqlCommand($"""DELETE FROM "{table}" WHERE "TenantId" = @tid""", conn, txn);
                    cmd.Parameters.AddWithValue("tid", tenantId);
                    await cmd.ExecuteNonQueryAsync();
                }

                await using (var cmd = new NpgsqlCommand("""DELETE FROM "Tenants" WHERE "Id" = @tid""", conn, txn))
                {
                    cmd.Parameters.AddWithValue("tid", tenantId);
                    await cmd.ExecuteNonQueryAsync();
                }

                await txn.CommitAsync();
            }
            catch
            {
                await txn.RollbackAsync();
                throw;
            }
        }
        finally
        {
            if (wasClosed) await conn.CloseAsync();
        }

        logger.LogWarning("Tenant {TenantId} ({Email}) deleted.", tenantId, user.Email);
        return NoContent();
    }

    /// <summary>Kahn's-algorithm topological sort over the FK graph restricted to
    /// <paramref name="tables"/> — a table with an outgoing edge (it holds the FK) must be
    /// deleted before the table it points at. Self-referencing edges (e.g. CafeTable's own
    /// MergedIntoTableId) are dropped: a single DELETE removing every row for one tenant in
    /// one statement is safe against its own self-references. Any table left over after the
    /// sort (a genuine cross-table cycle, none exist in this schema today) is appended in its
    /// original order rather than dropped, so nothing silently goes unswept.</summary>
    private static List<string> TopologicalDeletionOrder(List<string> tables, List<(string Child, string Parent)> edges)
    {
        var tableSet = tables.ToHashSet();
        var relevantEdges = edges.Where(e => e.Child != e.Parent && tableSet.Contains(e.Child) && tableSet.Contains(e.Parent)).ToList();

        var blockedBy = tables.ToDictionary(t => t, _ => 0);
        var unblocks = tables.ToDictionary(t => t, _ => new List<string>());
        foreach (var (child, parent) in relevantEdges)
        {
            unblocks[child].Add(parent);
            blockedBy[parent]++;
        }

        var queue = new Queue<string>(tables.Where(t => blockedBy[t] == 0));
        var result = new List<string>();
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            result.Add(t);
            foreach (var next in unblocks[t])
                if (--blockedBy[next] == 0) queue.Enqueue(next);
        }

        if (result.Count < tables.Count) result.AddRange(tables.Except(result));
        return result;
    }

    private async Task<AppUser> CurrentUserAsync()
    {
        var idClaim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var id = int.Parse(idClaim!);
        return await db.Users.FindAsync(id) ?? throw new KeyNotFoundException("User not found.");
    }

    /// <summary>Adds a brand-new session row for this device rather than overwriting
    /// any existing one — see RefreshTokenEntry's doc comment.</summary>
    private async Task<string> IssueTokensAsync(AppUser user)
    {
        var token = tokenService.CreateRefreshToken();
        db.RefreshTokens.Add(new RefreshTokenEntry
        {
            UserId = user.Id,
            Token = token,
            ExpiresAt = DateTime.UtcNow.AddDays(jwtOptions.Value.RefreshTokenDays),
        });
        await Task.CompletedTask;
        return token;
    }

    /// <summary>"Log out everywhere" — used after a password reset/change so a
    /// stolen device/session can't survive it. Only touches this user's own rows.</summary>
    private async Task RevokeAllSessionsAsync(int userId)
    {
        var active = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync();
        foreach (var entry in active) entry.RevokedAt = DateTime.UtcNow;
    }

    private static AuthResponse BuildResponse(AppUser user, string accessToken, string refreshToken) =>
        new(accessToken, refreshToken, UserDto.From(user));
}
