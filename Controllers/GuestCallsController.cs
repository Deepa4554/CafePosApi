using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>Staff-side view of guests asking for something — Call Waiter and Request Bill. The
/// calls themselves are raised anonymously from the QR page (see PublicController.RaiseGuestCall).
///
/// No OwnerOrManager gate, deliberately: answering a table that wants its bill is the most
/// ordinary floor action there is, and putting it behind a manager would leave the alert ringing
/// while the one person who can silence it is elsewhere.</summary>
[ApiController]
[Route("api/guest-calls")]
public class GuestCallsController(CafePosDbContext db) : ControllerBase
{
    /// <summary>Outstanding calls, oldest first — the order they should be answered in. Only
    /// Open ones: an acknowledged call is history, and history belongs in a report, not in the
    /// thing that is ringing.</summary>
    [HttpGet]
    public async Task<IEnumerable<GuestCallDto>> List()
    {
        var calls = await db.GuestCalls.AsNoTracking()
            .Where(c => c.Status == GuestCallStatus.Open)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync();
        return calls.Select(GuestCallDto.From);
    }

    /// <summary>"I've got this" — stops the alert for everyone. Idempotent: two staff tapping the
    /// same card at the same moment is the normal case during a rush, not an error worth showing
    /// one of them, so a call that is already acknowledged answers 204 rather than a conflict.</summary>
    [HttpPost("{id:int}/acknowledge")]
    public async Task<IActionResult> Acknowledge(int id)
    {
        var call = await db.GuestCalls.FirstOrDefaultAsync(c => c.Id == id);
        if (call is null) return NotFound();
        if (call.Status == GuestCallStatus.Acknowledged) return NoContent();

        call.Status = GuestCallStatus.Acknowledged;
        call.AcknowledgedAt = DateTime.UtcNow;
        var idClaim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (idClaim is not null && int.TryParse(idClaim, out var userId)) call.AcknowledgedByUserId = userId;

        await db.SaveChangesAsync();
        return NoContent();
    }
}
