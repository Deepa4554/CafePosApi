using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

[ApiController]
[Route("api/tables")]
public class TablesController(CafePosDbContext db, QrTokenService qrTokens, ITenantContext tenant, IAuditService audit) : ControllerBase
{
    /// <summary>A QR token with no table attached — for browsing/ordering when every
    /// table is occupied, or for a counter/takeaway QR that isn't tied to a seat.
    /// PublicController/OrdersController.CreatePublic both treat an empty table code
    /// as "no table" (order comes back TAKEAWAY instead of DINE_IN).</summary>
    [HttpGet("menu-qr-token")]
    public ActionResult<object> GetMenuOnlyQrToken()
    {
        return new { token = qrTokens.Encode(tenant.TenantIdOrDefault, "") };
    }

    /// <summary>Tables with live occupancy — a table only shows "empty" once its order
    /// is BOTH paid AND served (paying alone doesn't free it; the guest may still be
    /// sitting there waiting on food). Each table also carries an encrypted QrToken —
    /// staff-only (this endpoint requires auth) — for building the customer-facing
    /// ordering link without exposing the tenant or table code in plain text.</summary>
    [HttpGet]
    public async Task<IEnumerable<object>> List()
    {
        var tables = await db.Tables.OrderBy(t => t.Id).ToListAsync();
        var openOrders = await db.Orders
            .Where(o => !o.Cancelled && (!o.Paid || o.Status != OrderStatus.Served) && o.TableCode != null)
            .Select(o => new { o.Id, o.TableCode, o.Status, o.Total, o.GuestName, o.GuestPhone })
            .ToListAsync();
        // Informational only — deliberately does NOT change Status (empty/occupied) above,
        // which stays order-based exactly as before. A guest can have a live QR session on
        // a table before ever placing an order; this just lets staff see/revoke it (see
        // GetSession/RevokeSession below) without changing what "occupied" means anywhere
        // else in the app.
        var activeSessionsByTable = await db.GuestSessions
            .Where(s => s.Status == GuestSessionStatus.Active || s.Status == GuestSessionStatus.Locked)
            .ToDictionaryAsync(s => s.TableId, s => s.Id);

        return tables.Select(t =>
        {
            var order = openOrders.FirstOrDefault(o => o.TableCode == t.Code);
            return new
            {
                t.Id,
                t.Code,
                t.Zone,
                t.Seats,
                Status = order is null ? "empty" : "occupied",
                OrderId = order?.Id,
                OrderStatus = order?.Status.ToString().ToUpperInvariant(),
                Bill = order?.Total,
                GuestName = order?.GuestName,
                GuestPhone = order?.GuestPhone,
                QrToken = qrTokens.Encode(t.TenantId, t.Code),
                ActiveSessionId = activeSessionsByTable.GetValueOrDefault(t.Id),
            };
        });
    }

    /// <summary>Session + joined-device details for one table — the staff-facing view onto
    /// the QR guest session (doc Section 8, GET /staff/tables/{id}/session).</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpGet("{id:int}/session")]
    public async Task<ActionResult<object>> GetSession(int id)
    {
        var session = await db.GuestSessions
            .Where(s => s.TableId == id && (s.Status == GuestSessionStatus.Active || s.Status == GuestSessionStatus.Locked))
            .FirstOrDefaultAsync();
        if (session is null) return NotFound();

        var devices = await db.SessionDevices
            .Where(d => d.SessionId == session.Id)
            .Select(d => new { d.Id, d.UserAgent, d.JoinedAt, d.LastSeen })
            .ToListAsync();

        return new
        {
            session.Id,
            Status = session.Status.ToString().ToUpperInvariant(),
            session.OrderId,
            session.CreatedAt,
            session.LastActivity,
            session.ExpiresAt,
            Devices = devices,
        };
    }

    /// <summary>Manual end (doc Section 5.6) — abuse, a wrong-table scan, or a guest request.
    /// Immediately revokes every device on the session; the next request from any of them
    /// gets 410 (see ValidateGuestSessionAttribute).</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("{id:int}/session/revoke")]
    public async Task<IActionResult> RevokeSession(int id, RevokeSessionRequest req)
    {
        var session = await db.GuestSessions
            .Where(s => s.TableId == id && (s.Status == GuestSessionStatus.Active || s.Status == GuestSessionStatus.Locked))
            .FirstOrDefaultAsync();
        if (session is null) return NotFound();

        session.Status = GuestSessionStatus.Revoked;
        session.ClosedReason = SessionCloseReason.StaffClosed;
        session.ClosedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await audit.LogAsync(AuditAction.Delete, AuditResource.Table, id.ToString(),
            $"Guest session on table {id} manually ended. Reason: {req.Reason ?? "not specified"}.", AuditSeverity.Medium);

        return NoContent();
    }

    /// <summary>Add a table — Owner/Manager only, matches canManageTables() in the RN app.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost]
    public async Task<ActionResult<CafeTable>> Create(CreateTableRequest req)
    {
        if (req.Seats <= 0)
            throw new ApiValidationException("Seats must be at least 1.");

        var maxNum = (await db.Tables.Select(t => t.Code).ToListAsync())
            .Select(c => int.TryParse(c.TrimStart('T'), out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();

        var table = new CafeTable
        {
            Code = $"T{maxNum + 1}",
            Zone = string.IsNullOrWhiteSpace(req.Zone) ? "Indoor" : req.Zone.Trim(),
            Seats = req.Seats,
        };
        db.Tables.Add(table);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(List), new { id = table.Id }, table);
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var table = await db.Tables.FindAsync(id);
        if (table is null) return NotFound();

        var busy = await db.Orders.AnyAsync(o => o.TableCode == table.Code && !o.Cancelled && (!o.Paid || o.Status != OrderStatus.Served));
        if (busy) throw new ApiConflictException($"Table {table.Code} has an open order.");

        db.Tables.Remove(table);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
