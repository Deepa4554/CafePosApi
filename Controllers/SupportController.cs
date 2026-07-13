using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>
/// Cafe-side support tickets — powers the Help Center's "raise a ticket" flow. Every
/// query here is naturally tenant-scoped by the global query filter, so a cafe only
/// ever sees its own tickets. The Super Admin's cross-tenant inbox lives separately in
/// SuperAdminController (uses IgnoreQueryFilters(), by design never reachable here).
/// </summary>
[ApiController]
[Route("api/support")]
[Authorize]
public class SupportController(CafePosDbContext db) : ControllerBase
{
    [HttpGet("tickets")]
    public async Task<IEnumerable<SupportTicketDto>> ListTickets()
    {
        var tickets = await db.SupportTickets.Include(t => t.Messages)
            .OrderByDescending(t => t.UpdatedAt).ToListAsync();
        return tickets.Select(t => SupportTicketDto.From(t));
    }

    [HttpGet("tickets/{id:int}")]
    public async Task<ActionResult<SupportTicketDetailDto>> GetTicket(int id)
    {
        var ticket = await db.SupportTickets.Include(t => t.Messages).FirstOrDefaultAsync(t => t.Id == id);
        return ticket is null ? NotFound() : SupportTicketDetailDto.From(ticket);
    }

    [HttpPost("tickets")]
    public async Task<ActionResult<SupportTicketDetailDto>> CreateTicket(CreateTicketRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Subject))
            throw new ApiValidationException("Subject is required.");
        if (string.IsNullOrWhiteSpace(req.Message))
            throw new ApiValidationException("Message is required.");

        var name = CurrentUserName();
        var ticket = new SupportTicket
        {
            Subject = req.Subject.Trim(),
            CreatedByUserId = CurrentUserId() ?? 0,
            CreatedByName = name,
        };
        ticket.Messages.Add(new SupportTicketMessage { FromAdmin = false, SenderName = name, Body = req.Message.Trim() });
        db.SupportTickets.Add(ticket);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetTicket), new { id = ticket.Id }, SupportTicketDetailDto.From(ticket));
    }

    [HttpPost("tickets/{id:int}/messages")]
    public async Task<ActionResult<SupportTicketDetailDto>> AddMessage(int id, AddTicketMessageRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Body))
            throw new ApiValidationException("Message cannot be empty.");

        var ticket = await db.SupportTickets.Include(t => t.Messages).FirstOrDefaultAsync(t => t.Id == id);
        if (ticket is null) return NotFound();

        ticket.Messages.Add(new SupportTicketMessage { FromAdmin = false, SenderName = CurrentUserName(), Body = req.Body.Trim() });
        // A cafe replying to a resolved ticket re-opens it — otherwise their follow-up
        // would sit unseen in a ticket the admin has already filed away as done.
        ticket.Status = SupportTicketStatus.Open;
        ticket.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return SupportTicketDetailDto.From(ticket);
    }

    private int? CurrentUserId()
    {
        var claim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }

    private string CurrentUserName() => User.Identity?.Name ?? "Cafe Staff";
}
