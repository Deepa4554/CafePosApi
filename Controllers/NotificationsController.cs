using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

[ApiController]
[Route("api/notifications")]
public class NotificationsController(CafePosDbContext db) : ControllerBase
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

    [HttpPost]
    public async Task<ActionResult<NotificationDto>> Create(CreateNotificationRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            throw new ApiValidationException("Title is required.");
        if (string.IsNullOrWhiteSpace(req.Body))
            throw new ApiValidationException("Body is required.");

        var notification = new AppNotification
        {
            Title = req.Title,
            Body = req.Body,
            Category = req.Category,
            Channel = req.Channel,
            ActionUrl = req.ActionUrl,
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
