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
    public async Task<IEnumerable<InventoryItemDto>> List([FromQuery] int? branchId = null, [FromQuery] bool includeInactive = false)
    {
        var query = db.InventoryItems.AsQueryable();
        if (!includeInactive) query = query.Where(i => i.IsActive);
        // Same convention as OrdersController.List: no branch selected shows
        // everything, a branch selected shows only that branch's stock (items
        // added before branch-scoping existed have BranchId null and drop out).
        if (branchId is int bid) query = query.Where(i => i.BranchId == bid);
        return (await query.OrderBy(i => i.Name).ToListAsync()).Select(InventoryItemDto.From);
    }

    [HttpGet("low-stock")]
    public async Task<IEnumerable<InventoryItemDto>> LowStock() =>
        (await db.InventoryItems.Where(i => i.IsActive && i.Current <= i.ReorderLevel).OrderBy(i => i.Name).ToListAsync())
            .Select(InventoryItemDto.From);

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost]
    public async Task<ActionResult<InventoryItemDto>> Create(CreateInventoryItemRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ApiValidationException("Name is required.");
        if (req.Max <= 0)
            throw new ApiValidationException("Max stock must be greater than zero.");
        // Per-branch check: the same ingredient legitimately exists in each branch's stock.
        // Inactive items don't block the name — deactivate-then-recreate is the supported
        // way to start an ingredient over (see the reactivate endpoint).
        var nameLower = req.Name.Trim().ToLower();
        if (await db.InventoryItems.AnyAsync(i => i.IsActive && i.BranchId == req.BranchId && i.Name.ToLower() == nameLower))
            throw new ApiValidationException("An inventory item with this name already exists.");

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

    /// <summary>Edits the item's own details (name, category, unit, thresholds, cost).
    /// Current stock is untouched here on purpose — it only moves via Restock/Waste/Adjust
    /// so every quantity change leaves a ledger entry behind.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPut("{id:int}")]
    public async Task<ActionResult<InventoryItemDto>> Update(int id, UpdateInventoryItemRequest req)
    {
        var item = await db.InventoryItems.FindAsync(id);
        if (item is null) return NotFound();

        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ApiValidationException("Name is required.");
        if (req.Max <= 0)
            throw new ApiValidationException("Max stock must be greater than zero.");

        // Same per-branch uniqueness rule as Create, minus this item itself so saving an
        // unchanged name isn't rejected as a clash with its own row.
        var nameLower = req.Name.Trim().ToLower();
        if (await db.InventoryItems.AnyAsync(i => i.Id != id && i.IsActive && i.BranchId == item.BranchId && i.Name.ToLower() == nameLower))
            throw new ApiValidationException("An inventory item with this name already exists.");

        item.Name = req.Name.Trim();
        item.Category = string.IsNullOrWhiteSpace(req.Category) ? "General" : req.Category.Trim();
        item.Max = req.Max;
        item.Unit = req.Unit?.Trim() ?? "";
        item.UnitCost = req.UnitCost ?? item.UnitCost;
        item.MinStock = req.MinStock ?? 0;
        item.ReorderLevel = req.ReorderLevel ?? Math.Round(req.Max * 0.25, 2);

        // Raising ReorderLevel can put an already-notified item back under threshold, and
        // lowering it can lift one clear. Re-arm the alert whenever the new level says the
        // item is no longer low, matching what a restock past the level does.
        if (item.Current > item.ReorderLevel) item.LowStockNotified = false;

        await db.SaveChangesAsync();
        return InventoryItemDto.From(item);
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
    /// value. Creates a single consolidated ledger entry for the adjustment.</summary>
    [HttpPost("{id:int}/adjust")]
    public async Task<ActionResult<InventoryItemDto>> Adjust(int id, AdjustStockRequest req)
    {
        var item = await db.InventoryItems.FindAsync(id);
        if (item is null) return NotFound();
        if (req.NewQuantity < 0)
            throw new ApiValidationException("Stock cannot be adjusted below zero.");

        var delta = req.NewQuantity - item.Current;
        if (delta == 0) return InventoryItemDto.From(item);

        var reason = req.Reason?.Trim();
        var wasAboveReorder = item.Current > item.ReorderLevel;
        var previous = item.Current;
        item.Current = req.NewQuantity;

        if (delta < 0)
        {
            var batches = await db.InventoryBatches
                .Where(b => b.InventoryItemId == item.Id && b.Quantity > 0)
                .OrderBy(b => b.ExpiryDate ?? DateOnly.MaxValue)
                .ThenBy(b => b.ReceivedAt)
                .ThenBy(b => b.Id)
                .ToListAsync();

            var remaining = -delta;
            foreach (var batch in batches)
            {
                if (remaining <= 0) break;
                var take = Math.Min(remaining, batch.Quantity);
                batch.Quantity -= take;
                remaining -= take;
            }

            if (remaining > 0 && batches.Count > 0)
                batches.Last().Quantity -= remaining;
        }
        else
        {
            var batch = new InventoryBatch
            {
                TenantId = item.TenantId,
                InventoryItemId = item.Id,
                Quantity = delta,
                UnitCost = item.UnitCost,
            };
            db.InventoryBatches.Add(batch);
        }

        db.InventoryTransactions.Add(new InventoryTransaction
        {
            TenantId = item.TenantId,
            InventoryItemId = item.Id,
            Type = InventoryTransactionType.ManualAdjustment,
            PreviousStock = previous,
            ChangedQuantity = delta,
            RemainingStock = item.Current,
            Reason = reason,
            UserId = CurrentUserId(),
            UserName = CurrentUserName(),
        });

        if (wasAboveReorder && item.Current <= item.ReorderLevel && !item.LowStockNotified)
        {
            item.LowStockNotified = true;
            db.Notifications.Add(new AppNotification
            {
                TenantId = item.TenantId,
                Title = "Low stock",
                Body = $"{item.Name} is down to {item.Current:0.##}{item.Unit} (reorder at {item.ReorderLevel:0.##}{item.Unit}).",
                Category = NotificationCategory.Inventory,
                Channel = NotificationChannel.InApp,
                ActionUrl = "/inventory",
            });
        }

        await db.SaveChangesAsync();
        return InventoryItemDto.From(item);
    }

    [HttpGet("transactions")]
    public async Task<TransactionsPagedResult> AllTransactions(
        [FromQuery] string? types = null,
        [FromQuery] DateTime? dateFrom = null,
        [FromQuery] DateTime? dateTo = null,
        [FromQuery] string? search = null,
        [FromQuery] int? branchId = null,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string sortBy = "date",
        [FromQuery] string sortOrder = "desc")
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        pageNumber = Math.Max(1, pageNumber);

        var query = from t in db.InventoryTransactions
                    join i in db.InventoryItems on t.InventoryItemId equals i.Id
                    select new { Transaction = t, Item = i };

        // Filter by transaction types
        if (!string.IsNullOrWhiteSpace(types))
        {
            var typeList = types.Split(',').Select(t => t.Trim()).ToList();
            query = query.Where(x => typeList.Contains(x.Transaction.Type.ToString()));
        }

        // Filter by date range
        if (dateFrom.HasValue)
            query = query.Where(x => x.Transaction.CreatedAt >= dateFrom.Value);
        if (dateTo.HasValue)
            query = query.Where(x => x.Transaction.CreatedAt <= dateTo.Value);

        // Filter by search term (item name or user)
        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(x =>
                x.Item.Name.ToLower().Contains(searchLower) ||
                x.Transaction.UserName.ToLower().Contains(searchLower));
        }

        // Filter by branch
        if (branchId.HasValue)
            query = query.Where(x => x.Item.BranchId == branchId.Value);

        // Apply sorting
        query = (sortBy.ToLower(), sortOrder.ToLower()) switch
        {
            ("item", "asc") => query.OrderBy(x => x.Item.Name),
            ("item", _) => query.OrderByDescending(x => x.Item.Name),
            ("user", "asc") => query.OrderBy(x => x.Transaction.UserName),
            ("user", _) => query.OrderByDescending(x => x.Transaction.UserName),
            (_, "asc") => query.OrderBy(x => x.Transaction.CreatedAt),
            _ => query.OrderByDescending(x => x.Transaction.CreatedAt),
        };

        var totalItems = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

        var results = await query
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var txns = results.Select(x => x.Transaction).ToList();
        var dtos = await ToDtos(txns);
        return new TransactionsPagedResult(dtos, totalItems, totalPages, pageNumber, pageSize);
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

    /// <summary>Deactivate rather than delete — a hard delete would leave any Recipe still
    /// pointing at this ingredient silently deducting nothing (no MissingRecipeAlert either,
    /// since the recipe itself still "exists") and orphan this item's own ledger/batch
    /// history. List()/LowStock() hide it by default; existing recipes, transactions, and
    /// batches keep resolving it by id exactly as before.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var item = await db.InventoryItems.FindAsync(id);
        if (item is null) return NotFound();

        item.IsActive = false;
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Undoes Delete — the deactivated item goes back to showing up in List()/
    /// LowStock() and can be restocked/sold against normally again.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("{id:int}/reactivate")]
    public async Task<ActionResult<InventoryItemDto>> Reactivate(int id)
    {
        var item = await db.InventoryItems.FindAsync(id);
        if (item is null) return NotFound();

        // Create only checks active items, so the name can have been taken over by a fresh
        // item while this one sat deactivated. Restoring blindly would put two active items
        // with the same name in the branch — the one case Create/Update can't catch.
        var nameLower = item.Name.Trim().ToLower();
        if (await db.InventoryItems.AnyAsync(i => i.Id != id && i.IsActive && i.BranchId == item.BranchId &&
            i.Name.ToLower() == nameLower))
            throw new ApiValidationException(
                $"An active item named \"{item.Name}\" already exists. Rename that one first, then restore this.");

        item.IsActive = true;
        await db.SaveChangesAsync();
        return InventoryItemDto.From(item);
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
