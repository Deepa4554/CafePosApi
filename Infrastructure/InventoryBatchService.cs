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

        // Captured before any mutation below, so the low-stock check at the bottom only
        // fires on the crossing itself (above → at-or-below), not on every subsequent
        // deduction while it stays low — that's what LowStockNotified is for.
        var wasAboveReorder = ingredient.Current > ingredient.ReorderLevel;

        var batches = await db.InventoryBatches
            .Where(b => b.InventoryItemId == ingredient.Id && b.Quantity > 0)
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

        var remaining = amount;
        InventoryBatch? lastTouched = null;
        foreach (var batch in batches)
        {
            if (remaining <= 0) break;
            var take = Math.Min(remaining, batch.Quantity);
            WriteRow(batch, take);
            remaining -= take;
            lastTouched = batch;
        }

        if (remaining > 0)
        {
            // Ran out of stock to draw from — this ingredient either has no batches at all
            // (never purchased through this system) or every existing batch is already at
            // zero. Either way, the deduction still happens (never blocked), landing on the
            // last-touched batch or a brand-new zero-quantity one that goes negative.
            var batch = lastTouched;
            if (batch is null)
            {
                batch = new InventoryBatch
                {
                    TenantId = ingredient.TenantId,
                    InventoryItemId = ingredient.Id,
                    Quantity = 0,
                    UnitCost = ingredient.UnitCost,
                };
                db.InventoryBatches.Add(batch);
            }
            WriteRow(batch, remaining);
        }

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
            });
        }
    }

    /// <summary>Creates a new batch (purchase, or a positive stock-take/manual-adjustment
    /// correction) and writes one Purchase/ManualAdjustment ledger row. Updates
    /// ingredient.Current by +quantity.</summary>
    public static InventoryBatch CreateBatch(
        CafePosDbContext db, InventoryItem ingredient, double quantity, decimal unitCost,
        DateOnly? expiryDate, InventoryTransactionType type, string? referenceId,
        int? userId, string userName)
    {
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

    /// <summary>Precisely reverses one earlier ledger row (void-before-prep) — credits the
    /// SAME batch back via <paramref name="original"/>'s InventoryBatchId, not a floating
    /// new batch. Falls back to a fresh no-expiry batch if the original batch is somehow
    /// gone (batches are never hard-deleted in normal operation, so this is just a safety
    /// net, not an expected path).</summary>
    public static async Task ReverseAsync(
        CafePosDbContext db, InventoryTransaction original, InventoryItem ingredient, string reason,
        int? userId, string userName)
    {
        var batch = original.InventoryBatchId is int batchId
            ? await db.InventoryBatches.FindAsync(batchId)
            : null;
        batch ??= new InventoryBatch
        {
            TenantId = ingredient.TenantId,
            InventoryItemId = ingredient.Id,
            Quantity = 0,
            UnitCost = ingredient.UnitCost,
        };
        if (batch.Id == 0) db.InventoryBatches.Add(batch);

        var amountBack = -original.ChangedQuantity; // ChangedQuantity was negative on the original deduction
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
