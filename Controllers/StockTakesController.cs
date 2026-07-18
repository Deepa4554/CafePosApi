using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>Physical stock counts — the doc's "variance moment". A Draft session snapshots
/// each ingredient's system balance, staff record what they actually counted, and
/// Finalize (one-way) writes ManualAdjustment ledger rows for whatever's different —
/// after finalize, system stock IS the physical count.</summary>
[ApiController]
[Route("api/stock-takes")]
[Authorize(Policy = Policies.OwnerOrManager)]
[Authorize(Policy = Policies.RequirePlus)]
public class StockTakesController(CafePosDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<StockTakeDto>> List()
    {
        var stockTakes = await db.StockTakes.Include(s => s.Lines).OrderByDescending(s => s.CreatedAt).ToListAsync();
        return await ToDtos(stockTakes);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<StockTakeDto>> Get(int id)
    {
        var stockTake = await db.StockTakes.Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == id);
        if (stockTake is null) return NotFound();
        return (await ToDtos([stockTake]))[0];
    }

    [HttpPost]
    public async Task<ActionResult<StockTakeDto>> Create(CreateStockTakeRequest req)
    {
        var query = db.InventoryItems.AsQueryable();
        if (req.BranchId is int bid) query = query.Where(i => i.BranchId == bid);
        var items = await query.OrderBy(i => i.Name).ToListAsync();
        if (items.Count == 0)
            throw new ApiValidationException("No inventory items to count.");

        var stockTake = new StockTake
        {
            BranchId = req.BranchId,
            Note = req.Note?.Trim(),
            CreatedByUserId = CurrentUserId() ?? 0,
            CreatedByName = CurrentUserName(),
            Lines = items.Select(i => new StockTakeLine { InventoryItemId = i.Id, SystemQty = i.Current }).ToList(),
        };
        db.StockTakes.Add(stockTake);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = stockTake.Id }, (await ToDtos([stockTake]))[0]);
    }

    /// <summary>Records what staff actually counted for one line. Partial counts allowed —
    /// Finalize only acts on lines that ended up with a non-null CountedQty.</summary>
    [HttpPatch("{id:int}/lines/{lineId:int}")]
    public async Task<ActionResult<StockTakeDto>> RecordCount(int id, int lineId, RecordCountRequest req)
    {
        var stockTake = await db.StockTakes.Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == id);
        if (stockTake is null) return NotFound();
        if (stockTake.Status != StockTakeStatus.Draft) throw new ApiConflictException("This stock take has already been finalized.");
        if (req.CountedQty < 0) throw new ApiValidationException("Counted quantity cannot be negative.");

        var line = stockTake.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null) return NotFound();

        line.CountedQty = req.CountedQty;
        await db.SaveChangesAsync();
        return (await ToDtos([stockTake]))[0];
    }

    /// <summary>One-way. For every counted line whose CountedQty differs from the
    /// ingredient's CURRENT live balance (which may have moved since the count snapshot via
    /// intervening sales/purchases — the physical count wins regardless), sets Current to the
    /// counted value and writes a ManualAdjustment ledger row for the delta. Variance is
    /// stored against the original SystemQty snapshot, for reporting.</summary>
    [HttpPost("{id:int}/finalize")]
    public async Task<ActionResult<StockTakeDto>> Finalize(int id)
    {
        var stockTake = await db.StockTakes.Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == id);
        if (stockTake is null) return NotFound();
        if (stockTake.Status != StockTakeStatus.Draft) throw new ApiConflictException("This stock take has already been finalized.");

        var counted = stockTake.Lines.Where(l => l.CountedQty is not null).ToList();
        if (counted.Count == 0) throw new ApiValidationException("Count at least one item before finalizing.");

        var inventoryIds = counted.Select(l => l.InventoryItemId).ToList();
        var inventory = await db.InventoryItems.Where(i => inventoryIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);

        foreach (var line in counted)
        {
            line.Variance = line.CountedQty!.Value - line.SystemQty;
            if (!inventory.TryGetValue(line.InventoryItemId, out var ingredient)) continue;

            var liveBalance = ingredient.Current;
            if (Math.Abs(liveBalance - line.CountedQty.Value) < 0.0001) continue; // already matches, no adjustment needed

            var delta = line.CountedQty.Value - liveBalance;
            var referenceId = stockTake.Id.ToString();
            var reason = $"Stock take #{stockTake.Id}";
            if (delta < 0)
                await InventoryBatchService.ConsumeFifoAsync(db, ingredient, -delta, InventoryTransactionType.ManualAdjustment,
                    referenceId, orderItemId: null, reason, wasteReasonCode: null, CurrentUserId(), CurrentUserName());
            else
                InventoryBatchService.CreateBatch(db, ingredient, delta, ingredient.UnitCost, expiryDate: null,
                    InventoryTransactionType.ManualAdjustment, referenceId, CurrentUserId(), CurrentUserName());
        }

        stockTake.Status = StockTakeStatus.Finalized;
        stockTake.FinalizedAt = DateTime.UtcNow;
        stockTake.FinalizedByUserId = CurrentUserId();
        stockTake.FinalizedByName = CurrentUserName();

        await db.SaveChangesAsync();
        return (await ToDtos([stockTake]))[0];
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var stockTake = await db.StockTakes.FindAsync(id);
        if (stockTake is null) return NotFound();
        if (stockTake.Status != StockTakeStatus.Draft) throw new ApiConflictException("A finalized stock take can't be deleted.");

        db.StockTakes.Remove(stockTake);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<List<StockTakeDto>> ToDtos(List<StockTake> stockTakes)
    {
        var ids = stockTakes.SelectMany(s => s.Lines).Select(l => l.InventoryItemId).Distinct().ToList();
        var items = await db.InventoryItems.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        return stockTakes.Select(s => new StockTakeDto(
            s.Id, s.Status.ToString().ToUpperInvariant(), s.BranchId, s.Note, s.CreatedByName, s.CreatedAt,
            s.FinalizedAt, s.FinalizedByName,
            s.Lines.Select(l => new StockTakeLineDto(
                l.Id, l.InventoryItemId,
                items.TryGetValue(l.InventoryItemId, out var i) ? i.Name : "Unknown",
                items.TryGetValue(l.InventoryItemId, out var i2) ? i2.Unit : "",
                l.SystemQty, l.CountedQty, l.Variance)).ToList()
        )).ToList();
    }

    private int? CurrentUserId()
    {
        var claim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }

    private string CurrentUserName() => User.Identity?.Name ?? "Cafe Staff";
}
