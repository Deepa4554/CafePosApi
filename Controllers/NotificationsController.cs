using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

[ApiController]
[Route("api/notifications")]
public class NotificationsController(CafePosDbContext db, ITenantContext tenantContext) : ControllerBase
{
    [HttpGet]
    public async Task<object> List([FromQuery] bool includeArchived = false)
    {
        var currentUserId = CurrentUserId();

        var query = db.Notifications.AsQueryable();
        if (!includeArchived) query = query.Where(n => !n.IsArchived);

        var unreadQuery = db.Notifications.Where(n => !n.IsRead && !n.IsArchived);

        // A targeted notification (TargetUserId set — e.g. "task assigned to you") is only
        // ever this one user's business, regardless of category or kitchen-role filtering
        // below; every OTHER notification keeps today's tenant-wide visibility.
        query = query.Where(n => n.TargetUserId == null || n.TargetUserId == currentUserId);
        unreadQuery = unreadQuery.Where(n => n.TargetUserId == null || n.TargetUserId == currentUserId);

        // Role-scoped notification (TargetRolesCsv set — e.g. an approval only the Owner may
        // resolve) — kept in step with the identical narrowing FcmPushNotificationSender applies
        // to the push, so the bell badge can't advertise something this user can't open. Both
        // sides of the comparison are comma-wrapped so "Staff" never matches "KitchenStaff".
        var roleTag = CurrentRole() is AppRole role ? $",{role}," : null;
        query = query.Where(n => n.TargetRolesCsv == null || (roleTag != null && ("," + n.TargetRolesCsv + ",").Contains(roleTag)));
        unreadQuery = unreadQuery.Where(n => n.TargetRolesCsv == null || (roleTag != null && ("," + n.TargetRolesCsv + ",").Contains(roleTag)));

        // This user's own muted categories (see UserNotificationPreference) — the in-app half of
        // the same filter FcmPushNotificationSender applies to the push, so a muted category
        // disappears from the list and the unread badge together instead of one lagging the
        // other. Anything addressed to this user personally (TargetUserId) is exempt: that's
        // direct correspondence, not a feed, and a task assigned to you shouldn't vanish because
        // you muted the category it happens to carry.
        var mutedCategories = await db.UserNotificationPreferences
            .Where(p => p.UserId == currentUserId && !p.Enabled)
            .Select(p => p.Category)
            .ToListAsync();
        if (mutedCategories.Count > 0)
        {
            query = query.Where(n => n.TargetUserId != null || !mutedCategories.Contains(n.Category));
            unreadQuery = unreadQuery.Where(n => n.TargetUserId != null || !mutedCategories.Contains(n.Category));
        }

        // Kitchen-facing roles only need to know a new order came in — not billing,
        // inventory, staff, or any other category meant for front-of-house/management.
        if (User.IsInRole(nameof(AppRole.Chef)) || User.IsInRole(nameof(AppRole.KitchenStaff)))
        {
            query = query.Where(n => n.Category == NotificationCategory.OrderPlaced || n.TargetUserId == currentUserId);
            unreadQuery = unreadQuery.Where(n => n.Category == NotificationCategory.OrderPlaced || n.TargetUserId == currentUserId);
        }

        var items = await query.OrderByDescending(n => n.CreatedAt).ToListAsync();
        var unreadCount = await unreadQuery.CountAsync();
        return new { items = items.Select(NotificationDto.From), unreadCount };
    }

    private int? CurrentUserId()
    {
        var idClaim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return idClaim is not null && int.TryParse(idClaim, out var id) ? id : null;
    }

    /// <summary>Asked via IsInRole rather than by reading a claim directly, so it stays correct
    /// whichever claim type the JWT actually carries the role in — the same way the kitchen-role
    /// check below already does it. Null for a principal holding no recognised AppRole, which
    /// then sees only untargeted notifications.</summary>
    private AppRole? CurrentRole() =>
        Enum.GetValues<AppRole>().Cast<AppRole?>().FirstOrDefault(r => User.IsInRole(r!.Value.ToString()));

    /// <summary>This user's own per-category mutes. Lives here rather than on SettingsController
    /// because that whole controller's writes are Owner/Manager-gated — these are deliberately
    /// every staff member's own to change, including a Waiter's. Walks the enum rather than a
    /// hardcoded list so a category added later shows up here with no change to this method.</summary>
    [HttpGet("my-preferences")]
    public async Task<ActionResult<IEnumerable<MyNotificationPreferenceDto>>> GetMyPreferences()
    {
        var currentUserId = CurrentUserId();
        if (currentUserId is null) return Unauthorized();

        var settings = await db.Settings.FirstAsync();
        var mine = await db.UserNotificationPreferences
            .Where(p => p.UserId == currentUserId)
            .ToDictionaryAsync(p => p.Category, p => p.Enabled);

        return Ok(Enum.GetValues<NotificationCategory>()
            .Select(c => new MyNotificationPreferenceDto(
                c,
                mine.TryGetValue(c, out var enabled) ? enabled : true,
                NotificationPreferences.IsEnabled(settings, c))));
    }

    [HttpPut("my-preferences")]
    public async Task<IActionResult> UpdateMyPreference(UpdateMyNotificationPreferenceRequest req)
    {
        var currentUserId = CurrentUserId();
        if (currentUserId is null) return Unauthorized();

        // Upsert against the unique (UserId, Category) index — a toggle flipped back and forth
        // must keep reusing one row, not pile up history nobody reads.
        var existing = await db.UserNotificationPreferences
            .FirstOrDefaultAsync(p => p.UserId == currentUserId && p.Category == req.Category);

        if (existing is not null) existing.Enabled = req.Enabled;
        else db.UserNotificationPreferences.Add(new UserNotificationPreference
        {
            UserId = currentUserId.Value,
            Category = req.Category,
            Enabled = req.Enabled,
        });

        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost]
    public async Task<ActionResult<NotificationDto>> Create(CreateNotificationRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            throw new ApiValidationException("Title is required.");
        if (string.IsNullOrWhiteSpace(req.Body))
            throw new ApiValidationException("Body is required.");
        // TargetUserId here is caller-supplied (unlike TasksController/ApprovalsController,
        // which only ever pass an ID they just looked up themselves) — verified against this
        // tenant's own users so one tenant can't use this endpoint to probe/target another
        // tenant's user IDs.
        if (req.TargetUserId is int targetUserId &&
            !await db.Users.AnyAsync(u => u.Id == targetUserId && u.TenantId == tenantContext.TenantIdOrDefault))
            throw new ApiValidationException("Target user not found.");

        var notification = new AppNotification
        {
            Title = req.Title,
            Body = req.Body,
            Category = req.Category,
            Channel = req.Channel,
            ActionUrl = req.ActionUrl,
            TargetUserId = req.TargetUserId,
            // Same comma-wrapped format NotificationsController.List/FcmPushNotificationSender
            // already match on (see AppNotification.TargetRolesCsv) — empty list is treated the
            // same as omitted (null = tenant-wide), not an impossible-to-match empty string.
            TargetRolesCsv = req.TargetRoles is { Count: > 0 } roles ? string.Join(',', roles) : null,
        };
        db.Notifications.Add(notification);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(List), NotificationDto.From(notification));
    }

    [HttpPatch("{id:int}/read")]
    public async Task<IActionResult> MarkRead(int id)
    {
        var n = await db.Notifications.FindAsync(id);
        if (n is null) return NotFound();
        n.IsRead = true;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead()
    {
        // Plain load-and-save rather than ExecuteUpdateAsync: the bulk-update API
        // isn't supported by the InMemory provider used before Supabase is wired
        // up, and the notification list is small enough that this is cheap on
        // Postgres too.
        var unread = await db.Notifications.Where(n => !n.IsRead).ToListAsync();
        foreach (var n in unread) n.IsRead = true;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPatch("{id:int}/archive")]
    public async Task<IActionResult> Archive(int id)
    {
        var n = await db.Notifications.FindAsync(id);
        if (n is null) return NotFound();
        n.IsArchived = true;
        n.IsRead = true;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id:int}/retry")]
    public async Task<ActionResult<NotificationDto>> Retry(int id)
    {
        var n = await db.Notifications.FindAsync(id);
        if (n is null) return NotFound();
        n.DeliveryStatus = DeliveryStatus.Retrying;
        await db.SaveChangesAsync();
        return NotificationDto.From(n);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var n = await db.Notifications.FindAsync(id);
        if (n is null) return NotFound();
        db.Notifications.Remove(n);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
