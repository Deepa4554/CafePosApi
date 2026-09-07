using System.Security.Cryptography;
using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>
/// The owner-facing half of the Zomato/Swiggy bridge: setting it up and mapping its menu.
///
/// Split from DynoWebhookController because the two have opposite security models — that one is
/// anonymous and authenticated only by a URL token, this one is ordinary Owner/Manager JWT with
/// the normal tenant query filter doing the isolation. Keeping them in one class would put an
/// [AllowAnonymous] route next to privileged ones, which is exactly the mistake that only shows
/// up once somebody adds a route and forgets the attribute.
/// </summary>
[ApiController]
[Route("api/dyno-admin")]
[Authorize(Policy = Policies.OwnerOrManager)]
public class DynoAdminController(CafePosDbContext db, IAuditService audit) : ControllerBase
{
    [HttpGet("settings")]
    public async Task<DynoSettingsDto> GetSettings()
    {
        var s = await db.Settings.AsNoTracking().FirstAsync();
        return Describe(s, BaseUrl());
    }

    [HttpPut("settings")]
    public async Task<DynoSettingsDto> UpdateSettings(UpdateDynoSettingsRequest req)
    {
        var s = await db.Settings.FirstAsync();

        if (req.Enabled is bool enabled) s.DynoEnabled = enabled;
        if (req.AutoAccept is bool autoAccept) s.DynoAutoAccept = autoAccept;
        if (req.ZomatoRestaurantId is not null)
            s.ZomatoRestaurantId = Blank(req.ZomatoRestaurantId);
        if (req.SwiggyRestaurantId is not null)
            s.SwiggyRestaurantId = Blank(req.SwiggyRestaurantId);
        if (req.DefaultPrepTimeMins is int prep)
        {
            // A prep time the aggregator shows the customer. Bounded rather than free: a typo'd
            // "300" promises a five-hour wait on a live listing, and "0" promises the impossible.
            if (prep is < 5 or > 120)
                throw new ApiValidationException("Prep time must be between 5 and 120 minutes.");
            s.DynoDefaultPrepTimeMins = prep;
        }

        // Turning the bridge on without a token would leave a webhook URL nobody can call, so
        // mint one on first enable rather than making it a separate step the owner can miss.
        if (s.DynoEnabled && string.IsNullOrWhiteSpace(s.DynoBridgeToken))
            s.DynoBridgeToken = NewToken();

        await db.SaveChangesAsync();
        await audit.LogAsync(AuditAction.SettingsChange, AuditResource.Settings, null,
            "Updated Zomato/Swiggy (Dyno) bridge settings");
        return Describe(s, BaseUrl());
    }

    /// <summary>Issues a new webhook token and invalidates the old one immediately.
    ///
    /// The old URL stops working the moment this returns, so the cafe's Dyno install goes deaf
    /// until someone pastes the new one in — that is the intended behaviour for a leaked token,
    /// but it means this is never something to call casually.</summary>
    [HttpPost("settings/regenerate-token")]
    public async Task<DynoSettingsDto> RegenerateToken()
    {
        var s = await db.Settings.FirstAsync();
        s.DynoBridgeToken = NewToken();
        await db.SaveChangesAsync();
        await audit.LogAsync(AuditAction.SettingsChange, AuditResource.Settings, null,
            "Regenerated the Zomato/Swiggy bridge webhook token");
        return Describe(s, BaseUrl());
    }

    // ---------- Menu mapping ----------

    /// <summary>The aggregator's menu as last dumped by the bridge, each entry carrying the
    /// CafePOS item it's linked to (if any) plus a suggestion when it isn't.</summary>
    [HttpGet("catalog")]
    public async Task<ActionResult<List<PlatformCatalogRowDto>>> GetCatalog([FromQuery] string? provider)
    {
        var query = db.PlatformCatalogEntries.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(provider) && Enum.TryParse<PlatformProviderKind>(provider, true, out var kind))
            query = query.Where(c => c.Provider == kind);

        var entries = await query.OrderBy(c => c.IsCategory).ThenBy(c => c.Name).ToListAsync();
        var mappings = await db.PlatformMenuMappings.AsNoTracking().ToListAsync();
        var menuItems = await db.MenuItems.AsNoTracking()
            .Select(m => new { m.Id, m.Name })
            .ToListAsync();

        var mapByKey = mappings.ToDictionary(
            m => (m.Provider, m.ResId, m.PlatformEntityId, m.IsCategory),
            m => m.MenuItemId);
        var menuNames = menuItems.ToDictionary(m => m.Id, m => m.Name);

        return entries.Select(e =>
        {
            mapByKey.TryGetValue((e.Provider, e.ResId, e.PlatformEntityId, e.IsCategory), out var mappedId);
            return new PlatformCatalogRowDto(
                e.Provider.ToString(),
                e.ResId,
                e.PlatformEntityId,
                e.Name,
                e.IsCategory,
                e.PlatformPrice,
                mappedId,
                mappedId is not null && menuNames.TryGetValue(mappedId.Value, out var n) ? n : null,
                // Only suggest for unmapped rows — an existing mapping is somebody's decision and
                // shouldn't be second-guessed by a string match.
                mappedId is null && !e.IsCategory ? SuggestMenuItemId(e.Name, menuItems.Select(m => (m.Id, m.Name))) : null);
        }).ToList();
    }

    /// <summary>Links (or with a null MenuItemId, unlinks) one aggregator entry.</summary>
    [HttpPut("mapping")]
    public async Task<IActionResult> SetMapping(SetPlatformMappingRequest req)
    {
        if (!Enum.TryParse<PlatformProviderKind>(req.Provider, true, out var provider))
            throw new ApiValidationException("Unknown delivery platform.");
        if (string.IsNullOrWhiteSpace(req.ResId) || string.IsNullOrWhiteSpace(req.PlatformEntityId))
            throw new ApiValidationException("Missing platform item reference.");

        if (req.MenuItemId is int menuItemId)
        {
            var exists = await db.MenuItems.AnyAsync(m => m.Id == menuItemId);
            if (!exists) throw new ApiValidationException("That menu item no longer exists.");
        }

        var existing = await db.PlatformMenuMappings.FirstOrDefaultAsync(m =>
            m.Provider == provider && m.ResId == req.ResId
            && m.PlatformEntityId == req.PlatformEntityId && m.IsCategory == req.IsCategory);

        if (req.MenuItemId is null)
        {
            if (existing is not null) db.PlatformMenuMappings.Remove(existing);
        }
        else if (existing is not null)
        {
            existing.MenuItemId = req.MenuItemId;
        }
        else
        {
            db.PlatformMenuMappings.Add(new PlatformMenuMapping
            {
                Provider = provider,
                ResId = req.ResId,
                PlatformEntityId = req.PlatformEntityId,
                IsCategory = req.IsCategory,
                MenuItemId = req.MenuItemId,
            });
        }

        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Drops the stored catalog so the bridge re-requests the outlet's full menu on its
    /// next poll (see DynoWebhookController.GetItemCommands, which asks for a dump precisely when
    /// it has nothing stored). Mappings survive — they're keyed on the aggregator's own ids, so a
    /// refresh that returns the same menu leaves every link intact.</summary>
    [HttpPost("catalog/refresh")]
    public async Task<IActionResult> RefreshCatalog()
    {
        var entries = await db.PlatformCatalogEntries.ToListAsync();
        db.PlatformCatalogEntries.RemoveRange(entries);
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Recent aggregator orders whose lines couldn't be read or matched — the queue an
    /// owner works through, and the signal that a mapping is missing.</summary>
    [HttpGet("unmatched-orders")]
    public async Task<ActionResult<List<UnmatchedPlatformOrderDto>>> GetUnmatchedOrders()
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        return await db.PlatformOrderPayloads.AsNoTracking()
            .Where(p => !p.LinesParsed && p.ReceivedAt >= cutoff)
            .OrderByDescending(p => p.ReceivedAt)
            .Take(100)
            .Select(p => new UnmatchedPlatformOrderDto(
                p.OrderId, p.Provider.ToString(), p.PlatformOrderId, p.ReceivedAt))
            .ToListAsync();
    }

    // ---------- Helpers ----------

    /// <summary>Read off the live request rather than configured, so the URL an owner copies is
    /// right in every environment without a setting to keep in sync — same approach as
    /// DeliveryController's Borzo callback URL.</summary>
    private string BaseUrl() => $"{Request.Scheme}://{Request.Host}";

    /// <summary>192 bits of URL-safe randomness. Long because it is the entire credential for an
    /// unauthenticated endpoint and it travels in a URL that gets pasted, screenshotted, and left
    /// in a config box on a Windows box at a cafe.</summary>
    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

    private static string? Blank(string v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>Best-effort "did they mean this item" for the mapping screen. Case- and
    /// punctuation-insensitive exact match first, then a containment match — deliberately not a
    /// fuzzy distance score, because a wrong suggestion that looks confident is worse here than
    /// no suggestion: accepting one mis-links a dish and every future order for it.</summary>
    private static int? SuggestMenuItemId(string platformName, IEnumerable<(int Id, string Name)> menuItems)
    {
        var needle = Normalise(platformName);
        if (needle.Length < 3) return null;

        var items = menuItems.Select(m => (m.Id, Norm: Normalise(m.Name))).ToList();
        var exact = items.Where(m => m.Norm == needle).ToList();
        if (exact.Count == 1) return exact[0].Id;

        var contains = items.Where(m => m.Norm.Length >= 3 && (m.Norm.Contains(needle) || needle.Contains(m.Norm))).ToList();
        return contains.Count == 1 ? contains[0].Id : null;
    }

    private static string Normalise(string s) =>
        new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static DynoSettingsDto Describe(CafeSettings s, string baseUrl) => new(
        s.DynoEnabled,
        s.ZomatoRestaurantId,
        s.SwiggyRestaurantId,
        s.DynoDefaultPrepTimeMins,
        s.DynoAutoAccept,
        s.DynoLastContactAt,
        // Shown in full, not masked: the whole point is for the owner to copy it into Dyno's
        // config box, and it's already only visible to Owner/Manager. Same call DeliveryController
        // makes for the Borzo callback URL.
        string.IsNullOrWhiteSpace(s.DynoBridgeToken) ? null : $"{baseUrl}/api/dyno/{s.DynoBridgeToken}",
        // "Ready" means a webhook call would actually be accepted and land somewhere: enabled,
        // has a token, and at least one outlet id configured to match incoming orders against.
        s.DynoEnabled
            && !string.IsNullOrWhiteSpace(s.DynoBridgeToken)
            && (!string.IsNullOrWhiteSpace(s.ZomatoRestaurantId) || !string.IsNullOrWhiteSpace(s.SwiggyRestaurantId)));
}
