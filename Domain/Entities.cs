namespace CafePOS.Api.Domain;

public enum OrderStatus
{
    New,
    Preparing,
    Ready,
    Served,
}

/// <summary>Prepared items consume ingredients via a Recipe (BOM) on sale; Independent
/// items decrease their own linked InventoryItem stock directly (e.g. bottled water).</summary>
public enum ProductType { Prepared, Independent }

public class MenuItem : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public required string Name { get; set; }
    public required string Category { get; set; }
    public decimal Price { get; set; }
    public string Icon { get; set; } = "silverware-fork-knife";
    public string Subtitle { get; set; } = "";
    public bool Available { get; set; } = true;
    public string Image { get; set; } = "";
    public string? Description { get; set; }
    public bool Popular { get; set; }
    public ProductType ProductType { get; set; } = ProductType.Prepared;
    /// <summary>Only set when ProductType == Independent — the InventoryItem this menu
    /// item sells directly from.</summary>
    public int? LinkedInventoryItemId { get; set; }
}

/// <summary>Extra photos for a menu item beyond its single cover Image (e.g. plating shot,
/// ingredients close-up) — a separate table rather than a JSON array column so each photo
/// (a multi-MB base64 data URI, same storage approach as every other image in this app)
/// stays its own row instead of bloating one ever-growing text column.</summary>
public class MenuItemImage : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int MenuItemId { get; set; }
    public required string DataUri { get; set; }
    public int SortOrder { get; set; }
}

public class CafeTable : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>Display code shown on the floor plan, e.g. "T1".</summary>
    public required string Code { get; set; }
    public required string Zone { get; set; }
    public int Seats { get; set; }
}

public class Order : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>Which branch this order was placed at — null for cafes that haven't
    /// set up branches, or orders placed before branch-scoping existed. Filtered by
    /// whichever branch is active in the POS/Orders UI (see BranchesController).</summary>
    public int? BranchId { get; set; }
    /// <summary>e.g. "Table #T3 – Priya" / "Takeaway – Walk-in".</summary>
    public required string Title { get; set; }
    public required string OrderType { get; set; } // DINE_IN / TAKEAWAY / DELIVERY
    public string? TableCode { get; set; }
    public string? GuestName { get; set; }
    /// <summary>10-digit mobile, captured so the bill can be sent (WhatsApp, SMS) after
    /// the order is placed — e.g. when it's marked paid, not necessarily at creation.</summary>
    public string? GuestPhone { get; set; }
    /// <summary>Linked once the guest name resolves to a CRM customer record.</summary>
    public int? CustomerId { get; set; }
    /// <summary>
    /// Set this (not CustomerId directly) when linking a brand-new Customer that
    /// hasn't been saved yet — EF Core resolves the real generated id through the
    /// tracked reference. Assigning the raw CustomerId int before SaveChanges
    /// would still be 0 on a relational provider (Postgres only assigns the
    /// identity value at insert time; only the InMemory provider assigns eagerly).
    /// </summary>
    public Customer? Customer { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountPct { get; set; }
    /// <summary>The order-time manual discount only (from DiscountPct, applied when the
    /// order is created in POS). Coupon and gift-card redemptions are NO LONGER folded in
    /// here — they're separate billing-time reductions (CouponDiscountAmount /
    /// GiftCardAmountApplied), applied only once the order reaches Served. RecomputeTotals
    /// (OrdersController) subtracts all four discount components from Subtotal.</summary>
    public decimal DiscountAmount { get; set; }
    /// <summary>Manager-only discount applied at the billing/payment stage (Status ==
    /// Served), kept distinct from the order-time DiscountAmount for audit clarity.</summary>
    public decimal BillDiscountAmount { get; set; }
    /// <summary>Discount from a coupon redeemed at billing time (Served stage). Paired with
    /// CouponCode below.</summary>
    public decimal CouponDiscountAmount { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    /// <summary>Set when a coupon was redeemed against this order at billing time — its
    /// value lives in CouponDiscountAmount above.</summary>
    public string? CouponCode { get; set; }
    /// <summary>Set when a gift card was redeemed against this order at billing time — its
    /// value is a separate pre-tax reduction (GiftCardAmountApplied), summed alongside the
    /// other discount components by RecomputeTotals, not folded into DiscountAmount.</summary>
    public string? GiftCardCode { get; set; }
    public decimal GiftCardAmountApplied { get; set; }
    /// <summary>How the bill was settled — Cash / Card / UPI / Multiple. Set when the order
    /// is marked paid; null until then.</summary>
    public string? PaymentMethod { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.New;
    /// <summary>Increments by 1 each time at least one previously-unfired item is fired to
    /// the kitchen (see OrdersController.Fire). An item's FireBatch == this value means it
    /// was part of the most recent fire round (drives the "NEW" badge on KDS).</summary>
    public int CurrentFireBatch { get; set; }
    public bool Paid { get; set; }
    public bool Refunded { get; set; }
    public decimal? RefundedAmount { get; set; }
    public string? RefundReason { get; set; }
    public DateTime? RefundedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Whoever was logged in when this order was rung up — the till operator,
    /// not necessarily the waiter who actually took it (a shared counter POS is often
    /// run by a Cashier/Manager while a different person serves the table). Null for
    /// guest self-orders placed via the QR/public flow. Kept for transaction/audit
    /// accountability; performance is measured via ServedByStaffId instead.</summary>
    public int? CreatedByUserId { get; set; }
    public string? CreatedByName { get; set; }
    /// <summary>The staff member who actually took/served this order — references
    /// StaffMember, not AppUser, since a waiter doesn't need an app login to be
    /// credited here (the till operator picks them from the roster). Defaults to the
    /// logged-in user's own StaffMember record when they're a self-service waiter;
    /// otherwise set explicitly by whoever's operating the POS. Drives per-staff
    /// orders/revenue/attendance in StaffController's performance endpoints.</summary>
    public int? ServedByStaffId { get; set; }
    public string? ServedByName { get; set; }
    public List<OrderItem> Items { get; set; } = [];
}

public class OrderItem : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int OrderId { get; set; }
    public int MenuItemId { get; set; }
    public required string Name { get; set; }
    public int Qty { get; set; }
    public decimal Price { get; set; }
    public string? Modifier { get; set; }
    /// <summary>Which "fire round" this item was sent to the kitchen in. 0 = not yet fired
    /// (still freely editable/removable, invisible on KDS). >0 = the Order.CurrentFireBatch
    /// value at the moment it was fired. Lets the kitchen receive only newly-added items on
    /// a re-fire instead of the whole order again.</summary>
    public int FireBatch { get; set; }
}

public class InventoryItem : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>Which branch stocks this ingredient — null for cafes without branches
    /// set up, or single shared stock across all locations.</summary>
    public int? BranchId { get; set; }
    public required string Name { get; set; }
    public required string Category { get; set; }
    public string Icon { get; set; } = "package-variant-closed";
    public double Current { get; set; }
    public double Max { get; set; }
    public string Unit { get; set; } = "";
    public double DailyUsage { get; set; } = 1;
    public DateTime? LastRestockAt { get; set; }
    /// <summary>Cost per unit — powers the Dashboard's inventory-value-in-currency figure.</summary>
    public decimal UnitCost { get; set; }
    /// <summary>Floor for planning — distinct from ReorderLevel, which is what actually
    /// triggers a low-stock alert.</summary>
    public double MinStock { get; set; }
    /// <summary>Threshold that triggers a low-stock alert: Current &lt;= ReorderLevel.</summary>
    public double ReorderLevel { get; set; }
}

public class CafeSettings : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    // Tax & GST Configuration
    public decimal TaxRatePct { get; set; } = 8;

    // Language & Region
    public string Currency { get; set; } = "INR (₹)";
    public string Region { get; set; } = "Asia/Kolkata";

    // Receipt Customization / Branding
    public string BusinessName { get; set; } = "CafePOS";
    public string ReceiptHeader { get; set; } = "Welcome to CafePOS\n123 Coffee Street";
    public string ReceiptFooter { get; set; } = "Thank you for your visit!";
    public string? LogoUrl { get; set; }

    // Owner / Cafe Profile
    public string? Phone { get; set; }
    public string? Address { get; set; }
    /// <summary>Serialized JSON array of {day, open, from, to} — kept as one JSON blob
    /// rather than a separate table since it's always read/written as a whole week at
    /// once from the Cafe Profile screen, never queried per-day.</summary>
    public string StoreHoursJson { get; set; } = "[]";
    public string PrimaryColor { get; set; } = "#6366F1";
    public string QrStyle { get; set; } = "standard"; // standard / dots / squares
    public string ThemeMode { get; set; } = "system"; // light / dark / system

    // Account Security
    public bool TwoFactorEnabled { get; set; }
    public bool TerminalPasscodeRequired { get; set; } = true;

    // Notification Preferences
    public bool InventoryAlertsEnabled { get; set; } = true;
    public bool ShiftReportsEnabled { get; set; } = true;

    public bool HasCompletedOnboarding { get; set; }

    /// <summary>
    /// The owning Tenant's Slug, populated by SettingsController (not a real column —
    /// see [NotMapped]). Lets the app build tenant-aware QR ordering links
    /// (/order/{slug}/{tableCode}) without a separate endpoint.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string? TenantSlug { get; set; }
}
