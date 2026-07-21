using CafePOS.Api.Domain;

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
// Nullable: the web client sends no body at all on refresh — its refresh token
// lives only in the httpOnly cookie AuthController sets, never in JS-readable
// storage, so there's nothing to put in a request body. Mobile still posts its
// MMKV-stored token here.
public record RefreshRequest(string? RefreshToken);
// Only revokes this one device's session — omit to just no-op the revoke (still
// clears local storage client-side either way).
public record LogoutRequest(string? RefreshToken);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public record UserDto(int Id, string Email, string? Phone, string Name, string Role, string? ProfilePhoto, int TenantId, bool IsPlatformAdmin, string AccessMode, List<string>? AllowedScreens)
{
    // AuthController.Me is polled every few seconds by the app (see useLiveAccessSync)
    // so a staff member's screens update within seconds of an Owner changing them —
    // AccessMode/AllowedScreens must ride along on every /auth/me response for that to work.
    public static UserDto From(AppUser u) => new(u.Id, u.Email, u.Phone, u.Name, u.Role.ToString(), u.ProfilePhoto, u.TenantId, u.IsPlatformAdmin, u.AccessMode.ToString(), u.AllowedScreens);
}

public record AuthResponse(string AccessToken, string RefreshToken, UserDto User);
