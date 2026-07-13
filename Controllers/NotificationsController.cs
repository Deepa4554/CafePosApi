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
        var query = db.Notifications.AsQueryable();
        if (!includeArchived) query = query.Where(n => !n.IsArchived);

        var items = await query.OrderByDescending(n => n.CreatedAt).ToListAsync();
        var unreadCount = await db.Notifications.CountAsync(n => !n.IsRead && !n.IsArchived);
        return new { items = items.Select(NotificationDto.From), unreadCount };
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
