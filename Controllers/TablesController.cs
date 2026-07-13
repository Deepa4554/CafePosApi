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
public class TablesController(CafePosDbContext db, QrTokenService qrTokens) : ControllerBase
{
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
            .Where(o => (!o.Paid || o.Status != OrderStatus.Served) && o.TableCode != null)
            .Select(o => new { o.Id, o.TableCode, o.Status, o.Total, o.GuestName, o.GuestPhone })
            .ToListAsync();

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
            };
        });
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

        var busy = await db.Orders.AnyAsync(o => o.TableCode == table.Code && (!o.Paid || o.Status != OrderStatus.Served));
        if (busy) throw new ApiConflictException($"Table {table.Code} has an open order.");

        db.Tables.Remove(table);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
