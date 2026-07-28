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

        await InventoryBatchService.SetStockToAsync(db, item, req.NewQuantity, req.Reason?.Trim(),
            CurrentUserId(), CurrentUserName());

        await db.SaveChangesAsync();
        return InventoryItemDto.From(item);
    }

    /// <summary>Bulk stock/rate import from a CSV/Excel sheet, parsed client-side (see
    /// csvInventoryImport.ts) and posted as JSON — the counterpart to the recipe import:
    /// that file says what a dish consumes, this one says what's on the shelf and what it
    /// costs, and per-dish food cost falls out of the two together.
    ///
    /// CurrentStock is treated as a physical count — it SETS the figure (via
    /// InventoryBatchService.SetStockToAsync, so the difference still lands in the ledger),
    /// it doesn't add to what's already there. Re-uploading the same file is therefore
    /// idempotent rather than doubling stock. A row whose unit differs from the item's
    /// stored unit is converted (500/kg on a gm-based item becomes 0.5/gm); one that can't
    /// convert is reported instead of guessed.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("bulk-import")]
    public async Task<ActionResult<InventoryImportResultDto>> BulkImport(
        List<InventoryImportRowRequest> rows, [FromQuery] int? branchId = null)
    {
        if (rows is null || rows.Count == 0)
            throw new ApiValidationException("No rows to import.");
        if (rows.Count > 2000)
            throw new ApiValidationException("Cannot import more than 2000 rows at once.");

        var errors = new List<InventoryImportRowError>();
        // +2: row 1 is the header, and file rows are 1-based for a person reading them in Excel.
        var numbered = rows.Select((r, i) => (Row: r, RowNumber: i + 2)).ToList();

        var existingByName = (await db.InventoryItems.Where(i => i.IsActive).ToListAsync())
            .GroupBy(i => i.Name.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.ToList());

        var created = 0;
        var updated = 0;

        foreach (var group in numbered
            .Where(x => !string.IsNullOrWhiteSpace(x.Row.Name))
            .GroupBy(x => x.Row.Name.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var row = first.Row;
            var name = row.Name.Trim();

            // The same ingredient listed twice in one sheet is a data-entry slip, not two
            // stocktakes — say so rather than silently letting the last row win.
            if (group.Count() > 1)
            {
                foreach (var x in group.Skip(1))
                    errors.Add(new InventoryImportRowError(x.RowNumber, name, $"'{name}' appears more than once in this file — combine it into a single row. Only the first row was applied."));
            }

            if (string.IsNullOrWhiteSpace(row.Unit))
            {
                errors.Add(new InventoryImportRowError(first.RowNumber, name, "Unit is required (gm, kg, ml, litre, pcs)."));
                continue;
            }
            if (row.CurrentStock < 0)
            {
                errors.Add(new InventoryImportRowError(first.RowNumber, name, "Current stock cannot be negative."));
                continue;
            }
            if (row.UnitCost < 0)
            {
                errors.Add(new InventoryImportRowError(first.RowNumber, name, "Unit cost cannot be negative."));
                continue;
            }

            var fileUnit = row.Unit.Trim();

            if (!existingByName.TryGetValue(name.ToLowerInvariant(), out var matches))
            {
                var max = row.MaxStock is double m && m > 0 ? m : row.CurrentStock;
                db.InventoryItems.Add(new InventoryItem
                {
                    BranchId = branchId,
                    Name = name,
                    Category = string.IsNullOrWhiteSpace(row.Category) ? "General" : row.Category.Trim(),
                    Unit = fileUnit,
                    Current = row.CurrentStock,
                    Max = max,
                    UnitCost = row.UnitCost,
                    MinStock = 0,
                    ReorderLevel = row.ReorderLevel ?? Math.Round(max * 0.25, 2),
                    LastRestockAt = DateTime.UtcNow,
                });
                created++;
                continue;
            }

            if (matches.Count > 1)
            {
                errors.Add(new InventoryImportRowError(first.RowNumber, name, "Multiple inventory items share this name — rename one so it's unique, then re-import."));
                continue;
            }

            var item = matches[0];

            // How many of the item's stored units one file unit is worth: 1 for an exact
            // match (covers custom units like "packet" that the converter doesn't model),
            // 1000 for kg→gm, and so on. Quantities multiply by it; a per-unit RATE divides.
            double factor;
            if (string.Equals(fileUnit, item.Unit, StringComparison.OrdinalIgnoreCase))
            {
                factor = 1;
            }
            else if (UnitConverter.AreCompatible(fileUnit, item.Unit))
            {
                factor = UnitConverter.Convert(1, fileUnit, item.Unit);
            }
            else
            {
                errors.Add(new InventoryImportRowError(first.RowNumber, name, $"'{fileUnit}' can't convert to this item's stored unit ('{item.Unit}') — fix the sheet, or change the item's unit on the Inventory screen first."));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(row.Category)) item.Category = row.Category.Trim();
            item.UnitCost = factor == 1 ? row.UnitCost : Math.Round(row.UnitCost / (decimal)factor, 4);
            if (row.MaxStock is double maxStock && maxStock > 0) item.Max = maxStock * factor;
            if (row.ReorderLevel is double reorder) item.ReorderLevel = reorder * factor;

            // Cost is assigned above on purpose: a positive correction opens its batch at the
            // rate this same sheet just declared, not the stale one.
            await InventoryBatchService.SetStockToAsync(db, item, row.CurrentStock * factor,
                "Bulk inventory import", CurrentUserId(), CurrentUserName());
            item.LastRestockAt = DateTime.UtcNow;
            updated++;
        }

        await db.SaveChangesAsync();
        return new InventoryImportResultDto(created, updated, errors.Count, errors);
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
