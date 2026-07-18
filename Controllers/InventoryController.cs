using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

[ApiController]
[Route("api/inventory")]
[Authorize(Policy = Policies.CanReadInventory)]
[Authorize(Policy = Policies.RequirePlus)]
public class InventoryController(CafePosDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<InventoryItemDto>> List([FromQuery] int? branchId = null)
    {
        var query = db.InventoryItems.AsQueryable();
        // Same convention as OrdersController.List: no branch selected shows
        // everything, a branch selected shows only that branch's stock (items
        // added before branch-scoping existed have BranchId null and drop out).
        if (branchId is int bid) query = query.Where(i => i.BranchId == bid);
        return (await query.OrderBy(i => i.Name).ToListAsync()).Select(InventoryItemDto.From);
    }

    [HttpGet("low-stock")]
    public async Task<IEnumerable<InventoryItemDto>> LowStock() =>
        (await db.InventoryItems.Where(i => i.Current <= i.ReorderLevel).OrderBy(i => i.Name).ToListAsync())
            .Select(InventoryItemDto.From);

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost]
    public async Task<ActionResult<InventoryItemDto>> Create(CreateInventoryItemRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ApiValidationException("Name is required.");
        if (req.Max <= 0)
            throw new ApiValidationException("Max stock must be greater than zero.");

        var item = new InventoryItem
        {
            BranchId = req.BranchId,
            Name = req.Name.Trim(),
            Category = string.IsNullOrWhiteSpace(req.Category) ? "General" : req.Category.Trim(),
            Max = req.Max,
            Unit = req.Unit?.Trim() ?? "",
            UnitCost = req.UnitCost ?? 0,
            MinStock = req.MinStock ?? 0,
            ReorderLevel = req.ReorderLevel ?? Math.Round(req.Max * 0.25, 2),
            LastRestockAt = DateTime.UtcNow,
        };
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync(); // assigns item.Id, needed as the new batch's FK below

        // Initial stock becomes the item's first batch — everything traces back to a lot
        // from here on, same as a restock/GRN.
        InventoryBatchService.CreateBatch(db, item, req.Max, item.UnitCost, req.ExpiryDate,
            InventoryTransactionType.Purchase, referenceId: null, CurrentUserId(), CurrentUserName());
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(List), new { id = item.Id }, InventoryItemDto.From(item));
    }

    /// <summary>Quick single-item restock — the button already on each inventory card.
    /// Adds the given quantity as a new batch (not a forced top-up to Max), optionally
    /// dated with an expiry.</summary>
    [HttpPost("{id:int}/restock")]
    public async Task<ActionResult<InventoryItemDto>> Restock(int id, RestockRequest req)
    {
        var item = await db.InventoryItems.FindAsync(id);
        if (item is null) return NotFound();
        if (req.Quantity <= 0)
            throw new ApiValidationException("Restock quantity must be greater than zero.");

        var cost = req.UnitCost ?? item.UnitCost;
        InventoryBatchService.CreateBatch(db, item, req.Quantity, cost, req.ExpiryDate,
            InventoryTransactionType.Purchase, referenceId: null, CurrentUserId(), CurrentUserName());
        if (req.UnitCost is decimal) item.UnitCost = cost;
        item.LastRestockAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return InventoryItemDto.From(item);
    }

    [HttpPost("{id:int}/waste")]
    public async Task<ActionResult<InventoryItemDto>> LogWaste(int id, WasteRequest req)
    {
        var item = await db.InventoryItems.FindAsync(id);
        if (item is null) return NotFound();
        if (req.Quantity <= 0)
            throw new ApiValidationException("Waste quantity must be greater than zero.");

        var label = req.Reason.ToString();
        var reasonText = string.IsNullOrWhiteSpace(req.Note) ? label : $"{label} — {req.Note.Trim()}";
        // FIFO consumption — wasting the soonest-expiring lot first matches reality (you
        // use up / throw out what's about to go off, not a randomly-picked lot).
        await InventoryBatchService.ConsumeFifoAsync(db, item, req.Quantity, InventoryTransactionType.Waste,
            referenceId: null, orderItemId: null, reasonText, req.Reason, CurrentUserId(), CurrentUserName());

        await db.SaveChangesAsync();
        return InventoryItemDto.From(item);
    }

    /// <summary>Manual correction after a physical stock count — sets Current to an exact
    /// value. A shortfall FIFO-consumes existing batches; a surplus lands in a new
    /// no-expiry "found stock" batch (a physical recount has no known lot/expiry).</summary>
    [HttpPost("{id:int}/adjust")]
    public async Task<ActionResult<InventoryItemDto>> Adjust(int id, AdjustStockRequest req)
    {
        var item = await db.InventoryItems.FindAsync(id);
        if (item is null) return NotFound();
        if (req.NewQuantity < 0)
            throw new ApiValidationException("Stock cannot be adjusted below zero.");

        var delta = req.NewQuantity - item.Current;
        var reason = req.Reason?.Trim();
        if (delta < 0)
            await InventoryBatchService.ConsumeFifoAsync(db, item, -delta, InventoryTransactionType.ManualAdjustment,
                referenceId: null, orderItemId: null, reason, wasteReasonCode: null, CurrentUserId(), CurrentUserName());
        else if (delta > 0)
            InventoryBatchService.CreateBatch(db, item, delta, item.UnitCost, expiryDate: null,
                InventoryTransactionType.ManualAdjustment, referenceId: null, CurrentUserId(), CurrentUserName());

        await db.SaveChangesAsync();
        return InventoryItemDto.From(item);
    }

    [HttpGet("transactions")]
    public async Task<IEnumerable<InventoryTransactionDto>> AllTransactions([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(1, page);

        var txns = await db.InventoryTransactions.OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return await ToDtos(txns);
    }

    [HttpGet("{id:int}/transactions")]
    public async Task<ActionResult<IEnumerable<InventoryTransactionDto>>> ItemTransactions(int id)
    {
        var exists = await db.InventoryItems.AnyAsync(i => i.Id == id);
        if (!exists) return NotFound();

        var txns = await db.InventoryTransactions.Where(t => t.InventoryItemId == id)
            .OrderByDescending(t => t.CreatedAt).ToListAsync();
        return Ok(await ToDtos(txns));
    }

    /// <summary>Lot breakdown for one ingredient — FIFO order (soonest-expiring first),
    /// depleted batches (Quantity &lt;= 0) excluded.</summary>
    [HttpGet("{id:int}/batches")]
    public async Task<ActionResult<IEnumerable<InventoryBatchDto>>> Batches(int id)
    {
        var item = await db.InventoryItems.FindAsync(id);
        if (item is null) return NotFound();

        var batches = await db.InventoryBatches
            .Where(b => b.InventoryItemId == id && b.Quantity > 0)
            .OrderBy(b => b.ExpiryDate ?? DateOnly.MaxValue).ThenBy(b => b.ReceivedAt).ThenBy(b => b.Id)
            .ToListAsync();
        return Ok(batches.Select(b => ToBatchDto(b, item.Name, item.Unit)));
    }

    /// <summary>Every batch (any ingredient) expiring within `days` — includes already-expired
    /// lots — sorted soonest-first. This is the doc's "kaunsa lot pehle expire hoga" report.
    /// Not cost-sensitive, so stays under the controller's broader CanReadInventory policy —
    /// a waiter/chef should be able to see "this is expiring, use it" without extra permission.</summary>
    [HttpGet("expiring")]
    public async Task<IEnumerable<InventoryBatchDto>> Expiring([FromQuery] int days = 7)
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(Math.Max(0, days));
        var batches = await db.InventoryBatches
            .Where(b => b.Quantity > 0 && b.ExpiryDate != null && b.ExpiryDate <= cutoff)
            .OrderBy(b => b.ExpiryDate).ThenBy(b => b.ReceivedAt)
            .ToListAsync();

        var itemIds = batches.Select(b => b.InventoryItemId).Distinct().ToList();
        var items = await db.InventoryItems.Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        return batches.Select(b => ToBatchDto(b, items.TryGetValue(b.InventoryItemId, out var i) ? i.Name : "Unknown",
            items.TryGetValue(b.InventoryItemId, out var i2) ? i2.Unit : ""));
    }

    private static InventoryBatchDto ToBatchDto(InventoryBatch b, string itemName, string unit)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        int? daysUntilExpiry = b.ExpiryDate?.DayNumber - today.DayNumber;
        return new InventoryBatchDto(b.Id, b.InventoryItemId, itemName, unit, b.Quantity, b.UnitCost,
            b.ExpiryDate, b.ReceivedAt, daysUntilExpiry, daysUntilExpiry is < 0);
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var item = await db.InventoryItems.FindAsync(id);
        if (item is null) return NotFound();

        db.InventoryItems.Remove(item);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<List<InventoryTransactionDto>> ToDtos(List<InventoryTransaction> txns)
    {
        var ids = txns.Select(t => t.InventoryItemId).Distinct().ToList();
        var names = await db.InventoryItems.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Name);
        return txns.Select(t => new InventoryTransactionDto(
            t.Id, t.InventoryItemId, names.TryGetValue(t.InventoryItemId, out var n) ? n : "Unknown",
            t.Type.ToString(), t.PreviousStock, t.ChangedQuantity, t.RemainingStock, t.Reason, t.WasteReasonCode?.ToString(), t.ReferenceId, t.UserName, t.CreatedAt)).ToList();
    }

    private int? CurrentUserId()
    {
        var claim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }

    private string CurrentUserName() => User.Identity?.Name ?? "Cafe Staff";
}
