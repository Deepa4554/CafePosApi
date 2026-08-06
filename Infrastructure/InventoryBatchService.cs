using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// The single place InventoryBatch rows are created, FIFO-consumed, and reversed —
/// every existing call site that used to mutate InventoryItem.Current directly (restock,
/// waste, adjust, sale deduction, void reversal, GRN, stock-take finalize) routes through
/// here instead, so "which lot did this movement touch" is always recorded consistently.
/// </summary>
public static class InventoryBatchService
{
    /// <summary>Takes Postgres row-level write locks (<c>SELECT … FOR UPDATE</c>) on the given
    /// ingredients, so concurrent stock movements on the same ingredient run one after another
    /// instead of both reading the same starting balance and the loser's deduction being
    /// overwritten at save time (see CafePosDbContext's stock-scope comment for what that
    /// silently did to InventoryItem.Current).
    ///
    /// Call this BEFORE the InventoryItem rows are read into the change tracker — the read is
    /// then guaranteed to see the latest committed balance, and the per-ingredient
    /// lock-and-refresh inside ConsumeFifoAsync/CreateBatchAsync/SetStockToAsync/ReverseAsync
    /// steps aside for anything already locked here. Ids are locked in one statement in
    /// ascending order so two requests holding overlapping sets can't deadlock by grabbing the
    /// same rows in opposite orders.</summary>
    public static async Task LockIngredientsAsync(CafePosDbContext db, IEnumerable<int> inventoryItemIds)
    {
        var ids = inventoryItemIds.Where(id => id != 0 && !db.HoldsStockLock(id)).Distinct().Order().ToArray();
        if (ids.Length == 0) return;
        if (!await db.BeginStockScopeAsync()) return;

        await db.Database.ExecuteSqlRawAsync(
            @"SELECT ""Id"" FROM ""InventoryItems"" WHERE ""Id"" = ANY({0}) ORDER BY ""Id"" FOR UPDATE",
            new object[] { ids });
        db.MarkStockLocked(ids);
    }

    /// <summary>Serializes one ingredient the way <see cref="LockIngredientsAsync"/> does, then —
    /// because the caller loaded the row before any lock existed — re-reads the balance that lock
    /// now protects. Only Current/LowStockNotified are refreshed rather than reloading the whole
    /// entry, so a caller that edited other fields first (PurchaseOrdersController.Receive's
    /// weighted-average UnitCost, InventoryController's bulk import rewriting cost/thresholds)
    /// doesn't silently lose them.
    ///
    /// No-ops once this unit of work already holds the ingredient's lock: whatever it has
    /// deducted in memory since is then the authoritative figure — nobody else could have moved
    /// the row — and re-reading would undo it.</summary>
    private static async Task LockAndRefreshAsync(CafePosDbContext db, InventoryItem ingredient)
    {
        if (ingredient.Id == 0 || db.HoldsStockLock(ingredient.Id)) return;
        if (!await db.BeginStockScopeAsync()) return;

        await db.Database.ExecuteSqlRawAsync(
            @"SELECT ""Id"" FROM ""InventoryItems"" WHERE ""Id"" = {0} FOR UPDATE", ingredient.Id);
        db.MarkStockLocked([ingredient.Id]);

        // IgnoreQueryFilters for the same reason ConsumeFifoAsync's batch query uses it: this
        // runs inside anonymous guest flows where the ambient JWT-derived tenant filter resolves
        // to the DEFAULT tenant and would hide the real cafe's row.
        var fresh = await db.InventoryItems.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.Id == ingredient.Id)
            .Select(i => new { i.Current, i.LowStockNotified })
            .FirstOrDefaultAsync();
        if (fresh is null) return;
        ingredient.Current = fresh.Current;
        ingredient.LowStockNotified = fresh.LowStockNotified;
    }

    /// <summary>Consumes <paramref name="amount"/> from <paramref name="ingredient"/>'s
    /// batches in FIFO order (soonest ExpiryDate first, nulls last, then oldest ReceivedAt,
    /// then Id) — one InventoryTransaction row per batch touched, so a single sale/waste/
    /// adjustment that spans two lots produces two correctly batch-tagged ledger rows. If
    /// every batch is exhausted before <paramref name="amount"/> is satisfied, the remainder
    /// is taken from the last batch touched (or a fresh zero-cost batch if this ingredient
    /// has no batches at all yet), letting it go negative — matches the existing
    /// negative-stock-allowed policy (see InventoryController's original ApplyTransaction
    /// doc comment). Updates ingredient.Current by -amount total.</summary>
    public static async Task ConsumeFifoAsync(
        CafePosDbContext db, InventoryItem ingredient, double amount, InventoryTransactionType type,
        string? referenceId, int? orderItemId, string? reason, WasteReason? wasteReasonCode,
        int? userId, string userName)
    {
        if (amount <= 0) return;

        // Before ingredient.Current is read below: everything from here to SaveChanges now runs
        // with this ingredient's row locked, so a concurrent deduction of the same ingredient
        // waits its turn and starts from the balance this one leaves behind.
        await LockAndRefreshAsync(db, ingredient);

        // Captured before any mutation below, so the low-stock check at the bottom only
        // fires on the crossing itself (above → at-or-below), not on every subsequent
        // deduction while it stays low — that's what LowStockNotified is for.
        var wasAboveReorder = ingredient.Current > ingredient.ReorderLevel;

        // IgnoreQueryFilters + the ingredient's own TenantId, NOT the ambient JWT-derived
        // filter: this runs inside anonymous guest flows too (QR self-ordering fires via
        // OrderBuildingService.ConsumeInventoryAsync), where the ambient filter resolves to
        // the DEFAULT tenant — leaving the real cafe's batches invisible, so every guest
        // sale skipped FIFO and piled up phantom negative batches instead.
        var batches = await db.InventoryBatches
            .IgnoreQueryFilters()
            .Where(b => b.TenantId == ingredient.TenantId && b.InventoryItemId == ingredient.Id && b.Quantity > 0)
            .OrderBy(b => b.ExpiryDate ?? DateOnly.MaxValue)
            .ThenBy(b => b.ReceivedAt)
            .ThenBy(b => b.Id)
            .ToListAsync();

        void WriteRow(InventoryBatch batch, double take)
        {
            var previous = ingredient.Current;
            batch.Quantity -= take;
            ingredient.Current -= take;
            db.InventoryTransactions.Add(new InventoryTransaction
            {
                TenantId = ingredient.TenantId,
                InventoryItemId = ingredient.Id,
                Batch = batch,
                Type = type,
                PreviousStock = previous,
                ChangedQuantity = -take,
                RemainingStock = ingredient.Current,
                Reason = reason,
                WasteReasonCode = wasteReasonCode,
                ReferenceId = referenceId,
                OrderItemId = orderItemId,
                UserId = userId,
                UserName = userName,
            });
        }

        // Planned first, written after: the idempotency index below is (OrderItemId,
        // InventoryItemId, Batch) — one row per DISTINCT batch this deduction actually
        // touches. Collecting draws per batch (instead of calling WriteRow the moment each
        // is decided) means the negative-stock fallback below, which can land back on the
        // SAME batch a plain FIFO walk already drew from, merges into that batch's one row
        // instead of writing a second row for it and self-colliding with the index.
        var draws = new List<(InventoryBatch Batch, double Take)>();
        var remaining = amount;
        foreach (var batch in batches)
        {
            if (remaining <= 0) break;
            var take = Math.Min(remaining, batch.Quantity);
            draws.Add((batch, take));
            remaining -= take;
        }

        if (remaining > 0)
        {
            // Ran out of stock to draw from — this ingredient either has no batches at all
            // (never purchased through this system) or every existing batch is already at
            // zero. Either way, the deduction still happens (never blocked), landing on the
            // last-touched batch or a brand-new zero-quantity one that goes negative.
            if (draws.Count > 0)
            {
                var last = draws[^1];
                draws[^1] = (last.Batch, last.Take + remaining);
            }
            else
            {
                var batch = new InventoryBatch
                {
                    TenantId = ingredient.TenantId,
                    InventoryItemId = ingredient.Id,
                    Quantity = 0,
                    UnitCost = ingredient.UnitCost,
                };
                db.InventoryBatches.Add(batch);
                draws.Add((batch, remaining));
            }
        }

        foreach (var (batch, take) in draws)
            WriteRow(batch, take);

        // Fires once per crossing, regardless of what kind of deduction caused it (sale,
        // waste, adjustment) — an Owner cares that stock ran low, not why. Gated centrally
        // by Settings.InventoryAlertsEnabled in CafePosDbContext.SaveChangesAsync, so this
        // can create the row unconditionally.
        if (wasAboveReorder && ingredient.Current <= ingredient.ReorderLevel && !ingredient.LowStockNotified)
        {
            ingredient.LowStockNotified = true;
            db.Notifications.Add(new AppNotification
            {
                TenantId = ingredient.TenantId,
                Title = "Low stock",
                Body = $"{ingredient.Name} is down to {ingredient.Current:0.##}{ingredient.Unit} (reorder at {ingredient.ReorderLevel:0.##}{ingredient.Unit}).",
                Category = NotificationCategory.Inventory,
                Channel = NotificationChannel.InApp,
                ActionUrl = "/inventory",
                TargetRolesCsv = NotificationAudience.Management,
            });
        }
    }

    /// <summary>Creates a new batch (purchase, or a positive stock-take/manual-adjustment
    /// correction) and writes one Purchase/ManualAdjustment ledger row. Updates
    /// ingredient.Current by +quantity. Async purely so it can take the ingredient's row lock
    /// before reading the balance it adds to — a restock racing a sale used to lose whichever
    /// of the two saved first.</summary>
    public static async Task<InventoryBatch> CreateBatchAsync(
        CafePosDbContext db, InventoryItem ingredient, double quantity, decimal unitCost,
        DateOnly? expiryDate, InventoryTransactionType type, string? referenceId,
        int? userId, string userName)
    {
        await LockAndRefreshAsync(db, ingredient);

        var batch = new InventoryBatch
        {
            TenantId = ingredient.TenantId,
            InventoryItemId = ingredient.Id,
            Quantity = quantity,
            UnitCost = unitCost,
            ExpiryDate = expiryDate,
            SourceReferenceId = referenceId,
        };
        db.InventoryBatches.Add(batch);

        var previous = ingredient.Current;
        ingredient.Current += quantity;
        // Restocked back above the reorder line — let the next crossing (if stock drops
        // low again later) raise a fresh alert instead of staying silent forever.
        if (ingredient.LowStockNotified && ingredient.Current > ingredient.ReorderLevel)
            ingredient.LowStockNotified = false;
        db.InventoryTransactions.Add(new InventoryTransaction
        {
            TenantId = ingredient.TenantId,
            InventoryItemId = ingredient.Id,
            Batch = batch,
            Type = type,
            PreviousStock = previous,
            ChangedQuantity = quantity,
            RemainingStock = ingredient.Current,
            ReferenceId = referenceId,
            UserId = userId,
            UserName = userName,
        });
        return batch;
    }

    /// <summary>Sets stock to an exact figure — a physical count correction, or a bulk
    /// import stating what's actually on the shelf. Unlike CreateBatchAsync/ConsumeFifoAsync
    /// (which move stock BY an amount), this moves it TO one, writing a single consolidated
    /// ManualAdjustment ledger row for the difference rather than one row per batch: the
    /// user counted once, so the ledger should read as one correction. Batches are squared
    /// up to match — FIFO-drained when the count came in lower, one new batch at the item's
    /// current cost when it came in higher. No-ops when the figure already matches.</summary>
    public static async Task SetStockToAsync(
        CafePosDbContext db, InventoryItem item, double newQuantity, string? reason,
        int? userId, string userName)
    {
        // Before the delta is computed: the count is absolute, but the ledger row and the FIFO
        // drain below are both sized from Current, so it has to be the locked, latest balance.
        await LockAndRefreshAsync(db, item);

        var delta = newQuantity - item.Current;
        if (delta == 0) return;

        var wasAboveReorder = item.Current > item.ReorderLevel;
        var previous = item.Current;
        item.Current = newQuantity;

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

            // Counted lower than every lot on record put together — the shortfall lands on
            // the last lot, letting it go negative (same negative-stock-allowed policy as
            // ConsumeFifoAsync).
            if (remaining > 0 && batches.Count > 0)
                batches[^1].Quantity -= remaining;
        }
        else
        {
            db.InventoryBatches.Add(new InventoryBatch
            {
                TenantId = item.TenantId,
                InventoryItemId = item.Id,
                Quantity = delta,
                UnitCost = item.UnitCost,
            });
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
            UserId = userId,
            UserName = userName,
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
                TargetRolesCsv = NotificationAudience.Management,
            });
        }
        // Counted back above the reorder line — re-arm so a later drop raises a fresh alert,
        // matching what CreateBatchAsync and InventoryController.Update already do.
        else if (item.LowStockNotified && item.Current > item.ReorderLevel)
        {
            item.LowStockNotified = false;
        }
    }

    /// <summary>Precisely reverses one earlier ledger row (void-before-prep) — credits the
    /// SAME batch back via <paramref name="original"/>'s InventoryBatchId, not a floating
    /// new batch. Falls back to a fresh no-expiry batch if the original batch is somehow
    /// gone (batches are never hard-deleted in normal operation, so this is just a safety
    /// net, not an expected path).
    ///
    /// <paramref name="amountBackOverride"/> credits back only PART of the original row — what a
    /// quantity correction needs (a line cut from 5 to 3 gives back two fifths of its deduction,
    /// see OrdersController.UpdateItemQty). The ledger stays append-only: the original Sale row is
    /// never rewritten, so callers that may reverse the same row more than once must work out the
    /// still-outstanding amount themselves by netting off earlier Returns (see
    /// OrdersController.ReverseItemStockAsync, the only place that does). Omit it — the void/cancel
    /// paths do — to credit the whole row back exactly as before. A non-positive amount is a no-op
    /// rather than a stock-eating negative "return".</summary>
    public static async Task ReverseAsync(
        CafePosDbContext db, InventoryTransaction original, InventoryItem ingredient, string reason,
        int? userId, string userName, double? amountBackOverride = null)
    {
        if (amountBackOverride is <= 0) return;

        await LockAndRefreshAsync(db, ingredient);

        // Same ambient-filter bypass as ConsumeFifoAsync above — Find/queries here must
        // resolve the batch by the ingredient's tenant, not the request's JWT tenant.
        var batch = original.InventoryBatchId is int batchId
            ? await db.InventoryBatches.IgnoreQueryFilters()
                .FirstOrDefaultAsync(b => b.Id == batchId && b.TenantId == ingredient.TenantId)
            : null;
        batch ??= new InventoryBatch
        {
            TenantId = ingredient.TenantId,
            InventoryItemId = ingredient.Id,
            Quantity = 0,
            UnitCost = ingredient.UnitCost,
        };
        if (batch.Id == 0) db.InventoryBatches.Add(batch);

        // ChangedQuantity was negative on the original deduction, so negating it gives the full
        // credit; an override caps that at whatever slice of the line is actually being pulled back.
        var amountBack = amountBackOverride ?? -original.ChangedQuantity;
        var previous = ingredient.Current;
        batch.Quantity += amountBack;
        ingredient.Current += amountBack;
        if (ingredient.LowStockNotified && ingredient.Current > ingredient.ReorderLevel)
            ingredient.LowStockNotified = false;

        db.InventoryTransactions.Add(new InventoryTransaction
        {
            TenantId = ingredient.TenantId,
            InventoryItemId = ingredient.Id,
            Batch = batch,
            Type = InventoryTransactionType.Return,
            PreviousStock = previous,
            ChangedQuantity = amountBack,
            RemainingStock = ingredient.Current,
            ReferenceId = original.ReferenceId,
            OrderItemId = original.OrderItemId,
            Reason = reason,
            UserId = userId,
            UserName = userName,
        });
    }
}
