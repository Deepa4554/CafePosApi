using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>Staff-side view of the waitlist (TableManagementScreen's "Waiting" tab). Entries
/// themselves are created anonymously by the customer-facing QR page — see
/// PublicController.JoinWaitlist. No OwnerOrManager gate here: seating/cancelling a waiting
/// party is a floor action any staff member already does by opening/closing orders.</summary>
[ApiController]
[Route("api/waitlist")]
public class WaitlistController(CafePosDbContext db, QrTokenService qrTokens, ITenantContext tenant) : ControllerBase
{
    /// <summary>Still-waiting parties, oldest first — the order staff should seat them in.</summary>
    [HttpGet]
    public async Task<IEnumerable<WaitlistEntryDto>> List()
    {
        var entries = await db.WaitlistEntries.AsNoTracking()
            .Where(w => w.Status == WaitlistStatus.Waiting)
            .OrderBy(w => w.CreatedAt)
            .ToListAsync();
        return entries.Select(WaitlistEntryDto.From);
    }

    /// <summary>Token for the entrance QR — printed once, reused indefinitely (see
    /// TablesController.GetMenuOnlyQrToken/GetDeliveryQrToken for the same pattern).</summary>
    [HttpGet("qr-token")]
    public ActionResult<object> GetQrToken()
    {
        return new { token = qrTokens.Encode(tenant.TenantIdOrDefault, QrTokenService.WaitlistTableCode) };
    }

    /// <summary>Marks a waiting party seated at the given table. Deliberately thin: it only
    /// stamps this row, it never touches CafeTable or creates an Order — staff still open the
    /// table's normal New Order flow afterward, same as seating any other walk-in.</summary>
    [HttpPost("{id:int}/seat")]
    public async Task<IActionResult> Seat(int id, SeatWaitlistEntryRequest req)
    {
        var entry = await db.WaitlistEntries.FirstOrDefaultAsync(w => w.Id == id);
        if (entry is null) return NotFound();
        if (entry.Status != WaitlistStatus.Waiting) throw new ApiConflictException("This party is no longer waiting.");

        var table = await db.Tables.FirstOrDefaultAsync(t => t.Id == req.TableId);
        if (table is null) throw new ApiValidationException("Table not found.");

        entry.Status = WaitlistStatus.Seated;
        entry.SeatedTableId = table.Id;
        entry.SeatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>A no-show or a mis-scan — removes the party from the Waiting tab without
    /// seating them anywhere.</summary>
    [HttpPost("{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id)
    {
        var entry = await db.WaitlistEntries.FirstOrDefaultAsync(w => w.Id == id);
        if (entry is null) return NotFound();
        if (entry.Status != WaitlistStatus.Waiting) throw new ApiConflictException("This party is no longer waiting.");

        entry.Status = WaitlistStatus.Cancelled;
        entry.CancelledAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return NoContent();
    }
}
