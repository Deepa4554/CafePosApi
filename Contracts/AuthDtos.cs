using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;

namespace CafePOS.Api.Contracts;

public record RegisterRequest(string Email, string Password, string Name, AppRole Role = AppRole.Owner);
/// <summary>Creates a brand-new, isolated cafe (Tenant) plus its Owner account —
/// the real multi-cafe signup flow. CafeName becomes the tenant's display name and
/// the initial CafeSettings.BusinessName. Otp must match a code previously issued by
/// POST /auth/request-otp for this exact Email.</summary>
public record RegisterCafeRequest(string CafeName, string Email, string Password, string OwnerName, string Phone, string Otp);
public record RequestOtpRequest(string Email);
public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(string Email, string Otp, string NewPassword);
public record LoginRequest(string Email, string Password);
public record RefreshRequest(string? RefreshToken);
// Only revokes this one device's session — omit to just no-op the revoke (still
// clears local storage client-side either way).
public record LogoutRequest(string? RefreshToken);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record DeleteAccountRequest(string Password);
public record TablePlanDto(string Table, long RowCount);
public record DeleteAccountPlanDto(int TenantId, List<TablePlanDto> Tables);

public record UserDto(
    int Id, string Email, string? Phone, string Name, string Role, string? ProfilePhoto, int TenantId,
    bool IsPlatformAdmin, string AccessMode, List<string>? AllowedScreens, int? AssignedStationId,
    string TenantScreenMode, List<string>? TenantEnabledScreens)
{
    // AuthController.Me is polled every few seconds by the app (see useLiveAccessSync)
    // so a staff member's screens (and, likewise, KDS kitchen assignment) update within
    // seconds of an Owner changing them — every one of these fields must ride along on
    // every /auth/me response for that to work. TenantScreenMode/TenantEnabledScreens are
    // the cafe-level ceiling a platform admin can set (see Tenant.ScreenMode) — the same
    // "Automatic vs Custom" shape as AccessMode/AllowedScreens one level up the containment
    // chain, so the client's canAccessRoute can AND the two together the same way both times.
    public static UserDto From(AppUser u, TenantScreenSnapshot tenantScreens) => new(
        u.Id, u.Email, u.Phone, u.Name, u.Role.ToString(), u.ProfilePhoto, u.TenantId, u.IsPlatformAdmin,
        u.AccessMode.ToString(), u.AllowedScreens, u.AssignedStationId,
        tenantScreens.Mode.ToString(), tenantScreens.EnabledScreens);
}

public record AuthResponse(string AccessToken, string RefreshToken, UserDto User);
