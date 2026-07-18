namespace CafePOS.Api.Domain;

// ---------- Recipes (Bill of Materials) ----------

public class Recipe : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>One recipe per Prepared menu item — unique per tenant.</summary>
    public int MenuItemId { get; set; }
    public List<RecipeItem> Items { get; set; } = [];
}

public class RecipeItem : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int RecipeId { get; set; }
    public int InventoryItemId { get; set; }
    public double Quantity { get; set; }
    /// <summary>gm/kg/ml/litre/pcs — may differ from the ingredient's stored Unit; converted
    /// at deduction time by UnitConverter.</summary>
    public required string Unit { get; set; }
}

// ---------- Inventory Ledger ----------

public enum InventoryTransactionType { Purchase, Sale, ManualAdjustment, Waste, Expired, Return, Transfer }

/// <summary>Structured waste sub-reason — reconciles the old free-text convention
/// (Expired/Broken/KitchenWaste/Other) with a fuller, reportable set.</summary>
public enum WasteReason { Spoiled, Expired, Burnt, Spilled, TrialTasting, StaffMeal, Complimentary, Broken, Other }

public class InventoryTransaction : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int InventoryItemId { get; set; }
    public InventoryTransactionType Type { get; set; }
    public double PreviousStock { get; set; }
    /// <summary>Signed: negative for deductions, positive for additions.</summary>
    public double ChangedQuantity { get; set; }
    public double RemainingStock { get; set; }
    /// <summary>Free text, or the Waste sub-reason label + optional note.</summary>
    public string? Reason { get; set; }
    /// <summary>OrderId, PurchaseOrder.Id, etc. — string so it can hold any reference.</summary>
    public string? ReferenceId { get; set; }
    /// <summary>Set only for Type == Sale (fire-time deduction) and Type == Return (void
    /// reversal) rows — the OrderItem line this movement is tied to. Backs the idempotency
    /// guard (see CafePosDbContext's unique index) and RemoveItem's reversal lookup. Null
    /// for Purchase/Waste/ManualAdjustment/Expired/Transfer rows.</summary>
    public int? OrderItemId { get; set; }
    /// <summary>Structured sub-reason for Type == Waste rows only; null otherwise.</summary>
    public WasteReason? WasteReasonCode { get; set; }
    public int? UserId { get; set; }
    public string UserName { get; set; } = "System";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ---------- Stock Take ----------

public enum StockTakeStatus { Draft, Finalized }

/// <summary>Physical count session. One-way finalize (Draft rows freely editable,
/// Finalized rows read-only history).</summary>
public class StockTake : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int? BranchId { get; set; }
    public StockTakeStatus Status { get; set; } = StockTakeStatus.Draft;
    public string? Note { get; set; }
    public int CreatedByUserId { get; set; }
    public string CreatedByName { get; set; } = "System";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinalizedAt { get; set; }
    public int? FinalizedByUserId { get; set; }
    public string? FinalizedByName { get; set; }
    public List<StockTakeLine> Lines { get; set; } = [];
}

public class StockTakeLine : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int StockTakeId { get; set; }
    public int InventoryItemId { get; set; }
    /// <summary>InventoryItem.Current snapshotted at StockTake creation time.</summary>
    public double SystemQty { get; set; }
    /// <summary>Entered by staff during the count. Null until counted — finalize skips
    /// still-null lines (partial counts allowed).</summary>
    public double? CountedQty { get; set; }
    /// <summary>CountedQty - SystemQty, stored at finalize time (not re-derived later,
    /// since InventoryItem.Current may have moved on since). Null until finalized.</summary>
    public double? Variance { get; set; }
}

// ---------- Missing-Recipe Alerts ----------

/// <summary>One row per (TenantId, MenuItemId), upserted whenever a Prepared item with no
/// Recipe is sold — replaces the previous log-only behaviour with something queryable.</summary>
public class MissingRecipeAlert : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int MenuItemId { get; set; }
    public int OccurrenceCount { get; set; } = 1;
    public DateTime FirstOccurredAt { get; set; } = DateTime.UtcNow;
    public DateTime LastOccurredAt { get; set; } = DateTime.UtcNow;
    public bool Dismissed { get; set; }
}

// ---------- Purchase Orders ----------

public class PurchaseOrder : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string? SupplierName { get; set; }
    public string? Note { get; set; }
    public int CreatedByUserId { get; set; }
    public string CreatedByName { get; set; } = "System";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<PurchaseItem> Items { get; set; } = [];
}

public class PurchaseItem : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int PurchaseOrderId { get; set; }
    public int InventoryItemId { get; set; }
    public double Quantity { get; set; }
    public required string Unit { get; set; }
    public decimal UnitCost { get; set; }
}
