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

    /// <summary>A QR token for home delivery — printed on flyers, packaging or a shopfront
    /// board rather than a table. Scanning it opens the same ordering page in delivery mode,
    /// where the customer gives an address and shares their location, and the order comes
    /// through as DELIVERY (see PublicController.CreateDeliveryOrder). Kept as a separate code
    /// from the table and menu-only QRs precisely so the dine-in flow is not involved at all.</summary>
    [HttpGet("delivery-qr-token")]
    public ActionResult<object> GetDeliveryQrToken()
    {
        return new { token = qrTokens.Encode(tenant.TenantIdOrDefault, QrTokenService.DeliveryTableCode) };
    }

    /// <summary>Tables with live occupancy — a table only shows "empty" once its order
    /// is BOTH paid AND served (paying alone doesn't free it; the guest may still be
    /// sitting there waiting on food). Each table also carries an encrypted QrToken —
    /// staff-only (this endpoint requires auth) — for building the customer-facing
    /// ordering link without exposing the tenant or table code in plain text.</summary>
    [HttpGet]
    public async Task<IEnumerable<object>> List()
    {
        // Not paginated on purpose: a floor plan is a fixed physical thing, and every table
        // has to be on screen at once for the grid to mean anything — page 2 of a seating
        // chart would be a worse product, not a safer one. What this endpoint IS careful
        // about is staying flat as ORDER history grows: the open-orders query below is
        // filtered to live orders and projected to six columns, never the full graph.
        // AsNoTracking throughout — nothing here is written back, and this is polled.
        var allTables = await db.Tables.AsNoTracking().OrderBy(t => t.Id).ToListAsync();
        // A merged-in "guest" table (see CafeTable.MergedIntoTableId) is hidden from the
        // grid entirely — its seats fold into its host's MergedSeats below instead — since
        // merging is a floor-plan concept (temporarily one bookable unit), not a second row
        // to show alongside its host.
        var tables = allTables.Where(t => t.MergedIntoTableId is null).ToList();
        var guestsByHost = allTables
            .Where(t => t.MergedIntoTableId is not null)
            .GroupBy(t => t.MergedIntoTableId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        var openOrders = await db.Orders
            .Where(o => !o.Cancelled && (!o.Paid || o.Status != OrderStatus.Served) && o.TableCode != null)
            .Select(o => new { o.Id, o.BillNumber, o.TableCode, o.Status, o.Total, o.GuestName, o.GuestPhone })
            .ToListAsync();
        // Informational only — deliberately does NOT change Status (empty/occupied) above,
        // which stays order-based exactly as before. A guest can have a live QR session on
        // a table before ever placing an order; this just lets staff see/revoke it (see
        // GetSession/RevokeSession below) without changing what "occupied" means anywhere
        // else in the app.
        var activeSessionsByTable = await db.GuestSessions.AsNoTracking()
            .Where(s => s.Status == GuestSessionStatus.Active || s.Status == GuestSessionStatus.Locked)
            .ToDictionaryAsync(s => s.TableId, s => s.Id);

        return tables.Select(t =>
        {
            var order = openOrders.FirstOrDefault(o => o.TableCode == t.Code);
            var guests = guestsByHost.GetValueOrDefault(t.Id);
            return new
            {
                t.Id,
                t.Code,
                t.Zone,
                t.Seats,
                Status = order is null ? "empty" : "occupied",
                OrderId = order?.Id,
                // Sent formatted rather than as a bare int so the tile can't go back to deriving
                // "#{1000 + orderId}" client-side — that derivation is exactly what printed a new
                // cafe's first table as "Order #1455". See OrderNumberFormat.
                OrderNumber = order is null ? null : OrderNumberFormat.Bill(order.BillNumber, order.Id),
                OrderStatus = order?.Status.ToString().ToUpperInvariant(),
                Bill = order?.Total,
                GuestName = order?.GuestName,
                GuestPhone = order?.GuestPhone,
                QrToken = qrTokens.Encode(t.TenantId, t.Code),
                ActiveSessionId = activeSessionsByTable.GetValueOrDefault(t.Id),
                // t.Seats itself is never touched by a merge (see MergedIntoTableId's doc
                // comment) — MergedSeats is the read-time sum, so Unmerge needs no reconciliation.
                MergedSeats = guests is null ? t.Seats : t.Seats + guests.Sum(g => g.Seats),
                // Id included (not just Code) — Unmerge is called per-guest by id.
                MergedWith = guests?.Select(g => new { g.Id, g.Code }).ToList() ?? [],
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

        var codes = await db.Tables.Select(t => t.Code).ToListAsync();

        // A typed-in name wins; blank falls back to the auto "T{n}" numbering. Custom names
        // (e.g. "Corner Booth") simply don't parse as a number below, so they contribute 0 to
        // maxNum and never disturb the auto sequence for the tables that do use it.
        var requested = req.Code?.Trim();
        string code;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (requested.Length > 20)
                throw new ApiValidationException("Table name can be at most 20 characters.");
            // Code identifies the table on every order (Order.TableCode), so a duplicate would
            // make two tables indistinguishable — the (TenantId, Code) unique index would reject
            // it anyway, this just turns that into a readable message.
            if (codes.Any(c => string.Equals(c, requested, StringComparison.OrdinalIgnoreCase)))
                throw new ApiConflictException($"A table named \"{requested}\" already exists.");
            code = requested;
        }
        else
        {
            var maxNum = codes
                .Select(c => int.TryParse(c.TrimStart('T'), out var n) ? n : 0)
                .DefaultIfEmpty(0)
                .Max();
            code = $"T{maxNum + 1}";
        }

        var table = new CafeTable
        {
            Code = code,
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

        if (table.MergedIntoTableId is not null) throw new ApiConflictException($"Table {table.Code} is merged with another table — unmerge it first.");
        if (await db.Tables.AnyAsync(t => t.MergedIntoTableId == id)) throw new ApiConflictException($"Table {table.Code} has other tables merged into it — unmerge them first.");

        var busy = await db.Orders.AnyAsync(o => o.TableCode == table.Code && !o.Cancelled && (!o.Paid || o.Status != OrderStatus.Served));
        if (busy) throw new ApiConflictException($"Table {table.Code} has an open order.");

        db.Tables.Remove(table);
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Temporarily folds one currently-empty table's seating into another's — for a
    /// party bigger than one table holds, before anyone's ordered anything (see
    /// CafeTable.MergedIntoTableId's doc comment). Owner/Manager only, same gate as
    /// Create/Delete since this restructures the floor plan. Deliberately NOT for combining
    /// two already-occupied tables' bills — every real request for this feature turned out to
    /// mean "more seats for one party", not "merge two running orders into one invoice".</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("{id:int}/merge")]
    public async Task<IActionResult> Merge(int id, MergeTableRequest req)
    {
        if (id == req.TargetHostTableId) throw new ApiValidationException("Pick a different table to merge with.");

        var guest = await db.Tables.FindAsync(id);
        if (guest is null) return NotFound();
        var host = await db.Tables.FindAsync(req.TargetHostTableId);
        if (host is null) throw new ApiValidationException("Target table not found.");

        // Flat structure only — a guest can't itself be (or become) a host, and vice versa.
        if (guest.MergedIntoTableId is not null) throw new ApiConflictException($"Table {guest.Code} is already merged with another table.");
        if (host.MergedIntoTableId is not null) throw new ApiConflictException($"Table {host.Code} is itself merged into another table — merge with its host instead.");
        if (await db.Tables.AnyAsync(t => t.MergedIntoTableId == guest.Id)) throw new ApiConflictException($"Table {guest.Code} already has other tables merged into it.");

        var guestBusy = await db.Orders.AnyAsync(o => o.TableCode == guest.Code && !o.Cancelled && (!o.Paid || o.Status != OrderStatus.Served));
        if (guestBusy) throw new ApiConflictException($"Table {guest.Code} has an open order — only empty tables can be merged.");
        var hostBusy = await db.Orders.AnyAsync(o => o.TableCode == host.Code && !o.Cancelled && (!o.Paid || o.Status != OrderStatus.Served));
        if (hostBusy) throw new ApiConflictException($"Table {host.Code} has an open order — only empty tables can be merged.");

        guest.MergedIntoTableId = host.Id;
        await db.SaveChangesAsync();
        await audit.LogAsync(AuditAction.TableMerge, AuditResource.Table, host.Id.ToString(),
            $"Table {guest.Code} merged into {host.Code}.", AuditSeverity.Low);
        return NoContent();
    }

    /// <summary>Splits a merged-in table back out to standalone — the reverse of Merge.
    /// Seats on both rows were never mutated by Merge, so this needs no reconciliation.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("{id:int}/unmerge")]
    public async Task<IActionResult> Unmerge(int id)
    {
        var table = await db.Tables.FindAsync(id);
        if (table is null) return NotFound();
        if (table.MergedIntoTableId is null) throw new ApiValidationException($"Table {table.Code} isn't merged with anything.");

        var host = await db.Tables.FindAsync(table.MergedIntoTableId.Value);
        table.MergedIntoTableId = null;
        await db.SaveChangesAsync();
        await audit.LogAsync(AuditAction.TableMerge, AuditResource.Table, id.ToString(),
            $"Table {table.Code} split back out from {host?.Code ?? "its host"}.", AuditSeverity.Low);
        return NoContent();
    }
}
