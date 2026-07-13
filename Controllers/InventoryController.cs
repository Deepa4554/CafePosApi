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
[Authorize(Policy = Policies.NotWaiter)]
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
            Current = req.Max,
            Max = req.Max,
            Unit = req.Unit?.Trim() ?? "",
            UnitCost = req.UnitCost ?? 0,
            MinStock = req.MinStock ?? 0,
            ReorderLevel = req.ReorderLevel ?? Math.Round(req.Max * 0.25, 2),
            LastRestockAt = DateTime.UtcNow,
        };
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(List), new { id = item.Id }, InventoryItemDto.From(item));
    }

    /// <summary>Quick single-item restock — the button already on each inventory card.
    /// Adds the given quantity (not a forced top-up to Max) and logs a Purchase ledger row.</summary>
    [HttpPost("{id:int}/restock")]
    public async Task<ActionResult<InventoryItemDto>> Restock(int id, RestockRequest req)
    {
        var item = await db.InventoryItems.FindAsync(id);
        if (item is null) return NotFound();
        if (req.Quantity <= 0)
            throw new ApiValidationException("Restock quantity must be greater than zero.");

        ApplyTransaction(item, InventoryTransactionType.Purchase, req.Quantity, reason: null, referenceId: null);
        if (req.UnitCost is decimal cost) item.UnitCost = cost;
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
        if (string.IsNullOrWhiteSpace(req.Reason))
            throw new ApiValidationException("A reason is required to log waste.");

        ApplyTransaction(item, InventoryTransactionType.Waste, -req.Quantity, req.Reason.Trim(), referenceId: null);

        await db.SaveChangesAsync();
        return InventoryItemDto.From(item);
    }

    /// <summary>Manual correction after a physical stock count — sets Current to an exact
    /// value and logs the signed delta that produced it.</summary>
    [HttpPost("{id:int}/adjust")]
    public async Task<ActionResult<InventoryItemDto>> Adjust(int id, AdjustStockRequest req)
    {
        var item = await db.InventoryItems.FindAsync(id);
        if (item is null) return NotFound();
        if (req.NewQuantity < 0)
            throw new ApiValidationException("Stock cannot be adjusted below zero.");

        var delta = req.NewQuantity - item.Current;
        ApplyTransaction(item, InventoryTransactionType.ManualAdjustment, delta, req.Reason?.Trim(), referenceId: null);

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

    /// <summary>Applies a signed stock change to <paramref name="item"/> and records the
    /// matching ledger row. Stock is allowed to go negative — never blocked here.</summary>
    private void ApplyTransaction(InventoryItem item, InventoryTransactionType type, double changedQuantity, string? reason, string? referenceId)
    {
        var previous = item.Current;
        item.Current += changedQuantity;
        db.InventoryTransactions.Add(new InventoryTransaction
        {
            InventoryItemId = item.Id,
            Type = type,
            PreviousStock = previous,
            ChangedQuantity = changedQuantity,
            RemainingStock = item.Current,
            Reason = reason,
            ReferenceId = referenceId,
            UserId = CurrentUserId(),
            UserName = CurrentUserName(),
        });
    }

    private async Task<List<InventoryTransactionDto>> ToDtos(List<InventoryTransaction> txns)
    {
        var ids = txns.Select(t => t.InventoryItemId).Distinct().ToList();
        var names = await db.InventoryItems.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Name);
        return txns.Select(t => new InventoryTransactionDto(
            t.Id, t.InventoryItemId, names.TryGetValue(t.InventoryItemId, out var n) ? n : "Unknown",
            t.Type.ToString(), t.PreviousStock, t.ChangedQuantity, t.RemainingStock, t.Reason, t.ReferenceId, t.UserName, t.CreatedAt)).ToList();
    }

    private int? CurrentUserId()
    {
        var claim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }

    private string CurrentUserName() => User.Identity?.Name ?? "Cafe Staff";
}
