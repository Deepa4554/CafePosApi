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
    /// <summary>Free text, or the Waste sub-reason (Expired/Broken/KitchenWaste/Other).</summary>
    public string? Reason { get; set; }
    /// <summary>OrderId, PurchaseOrder.Id, etc. — string so it can hold any reference.</summary>
    public string? ReferenceId { get; set; }
    public int? UserId { get; set; }
    public string UserName { get; set; } = "System";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
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
