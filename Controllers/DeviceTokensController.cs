using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>Registers/unregisters this device's FCM token against the signed-in AppUser —
/// see DeviceToken's own doc comment for why Token (not (UserId, DeviceId)) is the natural key.
/// Called from the RN app's usePushNotifications hook on login and token-refresh, and from
/// AuthRepository.logout on sign-out.</summary>
[ApiController]
[Route("api/device-tokens")]
public class DeviceTokensController(CafePosDbContext db, ITenantContext tenant) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Register(RegisterDeviceTokenRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Token))
            throw new ApiValidationException("Token is required.");

        var currentUserId = CurrentUserId();
        if (currentUserId is null) return Unauthorized();

        var existing = await db.DeviceTokens.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Token == req.Token);
        if (existing is not null)
        {
            // Re-registering an already-known token just repoints it — see DeviceToken's doc
            // comment for why (same token can resurface under a different account, possibly a
            // different tenant entirely on a shared device, or just be this same account
            // logging back in). TenantId is set explicitly since StampTenantIds only stamps
            // newly-Added rows, not an update like this one.
            existing.UserId = currentUserId.Value;
            existing.TenantId = tenant.TenantIdOrDefault;
            existing.Platform = req.Platform;
            existing.LastSeenAt = DateTime.UtcNow;
        }
        else
        {
            db.DeviceTokens.Add(new DeviceToken { UserId = currentUserId.Value, Token = req.Token, Platform = req.Platform });
        }

        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("unregister")]
    public async Task<IActionResult> Unregister(UnregisterDeviceTokenRequest req)
    {
        var existing = await db.DeviceTokens.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Token == req.Token);
        if (existing is null) return NoContent();

        db.DeviceTokens.Remove(existing);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private int? CurrentUserId()
    {
        var idClaim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(idClaim, out var id) ? id : null;
    }
}
