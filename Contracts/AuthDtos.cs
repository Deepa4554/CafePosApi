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
public record RefreshRequest(string RefreshToken);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public record UserDto(int Id, string Email, string? Phone, string Name, string Role, string? ProfilePhoto, int TenantId, bool IsPlatformAdmin)
{
    public static UserDto From(AppUser u) => new(u.Id, u.Email, u.Phone, u.Name, u.Role.ToString(), u.ProfilePhoto, u.TenantId, u.IsPlatformAdmin);
}

public record AuthResponse(string AccessToken, string RefreshToken, UserDto User);
