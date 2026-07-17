using CafePOS.Api.Domain;

namespace CafePOS.Api.Contracts;

// ---------- Orders ----------

public record CreateOrderItemDto(int MenuItemId, int Qty, string? Modifier);

public record CreateOrderRequest(
    string OrderType, // DINE_IN / TAKEAWAY / DELIVERY
    string? TableCode,
    string? GuestName,
    List<CreateOrderItemDto> Items,
    // Order-time manual discount only. Coupons and gift cards are NO LONGER applied here —
    // they're billing-time actions on a served order (see bill-coupon / bill-giftcard).
    decimal DiscountPct = 0,
    int? BranchId = null,
    string? GuestPhone = null,
    // Who actually took/served this order — omit to default to the logged-in user's
    // own StaffMember record (self-service waiter); explicit when a Cashier/Manager/
    // Owner rings up an order on behalf of a different waiter from a shared counter POS.
    int? ServedByStaffId = null);

public record RefundOrderRequest(decimal? Amount, string? Reason);

// ---------- Order lifecycle (add item / fire / billing-time discounts / payment) ----------

public record AddOrderItemRequest(int MenuItemId, int Qty, string? Modifier);

/// <summary>Manager-only markdown applied at the billing stage (Served). Supply exactly
/// one of Pct (percentage of subtotal) or Amount (flat).</summary>
public record BillDiscountRequest(decimal? Pct, decimal? Amount);

public record BillCouponRequest(string Code);

public record BillGiftCardRequest(string Code);

/// <summary>How the settled bill was paid — Cash / Card / UPI / Multiple. Optional; the
/// legacy pay call with no body still works (payment method just stays unrecorded).</summary>
public record PayRequest(string? PaymentMethod);

/// <summary>
/// Real math on real order history, not AI — see OrdersController.RushForecast. HasEnoughData
/// false means fewer than 3 distinct days of paid-order history exist yet, the honest
/// answer being "too early to tell" rather than a guess dressed up as a prediction.
/// </summary>
public record RushForecastDto(
    bool HasEnoughData,
    bool RushExpected,
    string? NextDaypartLabel,
    double? NextDaypartAvgOrders);

/// <summary>Customer self-ordering from the QR table page — always dine-in, no
/// discounts/coupons. No TableCode here on purpose: the table comes from the
/// encrypted QrToken in the route, never from client-supplied input (a public/
/// anonymous caller could otherwise claim any table it likes).</summary>
public record CreatePublicOrderRequest(string? GuestName, string? GuestPhone, List<CreateOrderItemDto> Items);

public record OrderItemDto(int Id, string Name, int Qty, decimal Price, string? Modifier, int FireBatch);

/// <summary>One fire round's own kitchen status — see Order.FireBatches. KDS flattens an
/// order's non-Served batches into separate ticket cards from this list, instead of relying
/// on the single rollup Status below. KotNumber is a tenant-wide sequential ticket id (same
/// "#1000+id" convention as Order.Number) — the KOT-wise KDS view sorts/labels by this
/// instead of by table, matching how a KOT chit works in a physical kitchen.</summary>
public record FireBatchDto(int BatchNumber, string Status, DateTime FiredAt, string KotNumber);

public record OrderDto(
    int Id,
    string Number,
    string Title,
    string OrderType,
    string? TableCode,
    string? GuestName,
    string? GuestPhone,
    int? CustomerId,
    List<OrderItemDto> Items,
    decimal Subtotal,
    decimal DiscountPct,
    decimal DiscountAmount,
    decimal BillDiscountAmount,
    decimal CouponDiscountAmount,
    decimal Tax,
    decimal Total,
    string Status,
    bool Paid,
    bool Refunded,
    decimal? RefundedAmount,
    DateTime CreatedAt,
    int? BranchId,
    string? CreatedByName,
    string? ServedByName,
    string? CouponCode,
    string? GiftCardCode,
    decimal GiftCardAmountApplied,
    string? PaymentMethod,
    int CurrentFireBatch,
    List<FireBatchDto> FireBatches)
{
    public static OrderDto From(Order o) => new(
        o.Id,
        $"#{1000 + o.Id}",
        o.Title,
        o.OrderType,
        o.TableCode,
        o.GuestName,
        o.GuestPhone,
        o.CustomerId,
        o.Items.Select(i => new OrderItemDto(i.Id, i.Name, i.Qty, i.Price, i.Modifier, i.FireBatch)).ToList(),
        o.Subtotal,
        o.DiscountPct,
        o.DiscountAmount,
        o.BillDiscountAmount,
        o.CouponDiscountAmount,
        o.Tax,
        o.Total,
        o.Status.ToString().ToUpperInvariant(),
        o.Paid,
        o.Refunded,
        o.RefundedAmount,
        o.CreatedAt,
        o.BranchId,
        o.CreatedByName,
        o.ServedByName,
        o.CouponCode,
        o.GiftCardCode,
        o.GiftCardAmountApplied,
        o.PaymentMethod,
        o.CurrentFireBatch,
        o.FireBatches.OrderBy(b => b.BatchNumber)
            .Select(b => new FireBatchDto(b.BatchNumber, b.Status.ToString().ToUpperInvariant(), b.FiredAt, $"#{1000 + b.Id}"))
            .ToList());
}

public record SetStatusRequest(string Status);

// ---------- Menu ----------

public record CreateMenuItemRequest(string Name, string Category, decimal Price, string? Icon, string? Subtitle, string? Image, string? Description, ProductType? ProductType = null, int? LinkedInventoryItemId = null);

public record UpdateMenuItemRequest(string? Name, string? Category, decimal? Price, bool? Available, string? Subtitle, string? Image, string? Description, bool? Popular, ProductType? ProductType = null, int? LinkedInventoryItemId = null);

public record BulkImportResultDto(int CreatedCount, int SkippedCount);

public record MenuItemImageDto(int Id, string DataUri, int SortOrder)
{
    public static MenuItemImageDto From(MenuItemImage i) => new(i.Id, i.DataUri, i.SortOrder);
}

public record AddMenuItemImageRequest(string DataUri);

/// <summary>MenuItem plus how many units actually sold in the ranking window — powers
/// the Menu screen's "Best Sellers" row. UnitsSold is 0 for items backfilled from the
/// Popular flag when there isn't enough real order history yet.</summary>
public record BestSellerDto(int Id, string Name, string Category, decimal Price, string Icon, string Image, string Subtitle, bool Available, bool Popular, int UnitsSold);

// ---------- Tables ----------

public record CreateTableRequest(string Zone, int Seats);

// ---------- Inventory ----------

public record CreateInventoryItemRequest(string Name, string Category, double Max, string? Unit, decimal? UnitCost, double? MinStock = null, double? ReorderLevel = null, int? BranchId = null);

/// <summary>InventoryItem plus a server-computed LowStock flag (Current &lt;= ReorderLevel)
/// — replaces the frontend's old hardcoded current/max &lt;= 0.25 ratio guess.</summary>
public record InventoryItemDto(
    int Id, string Name, string Category, string Icon, double Current, double Max, string Unit,
    double DailyUsage, DateTime? LastRestockAt, decimal UnitCost, double MinStock, double ReorderLevel, bool LowStock, int? BranchId)
{
    public static InventoryItemDto From(InventoryItem i) => new(
        i.Id, i.Name, i.Category, i.Icon, i.Current, i.Max, i.Unit, i.DailyUsage, i.LastRestockAt,
        i.UnitCost, i.MinStock, i.ReorderLevel, i.Current <= i.ReorderLevel, i.BranchId);
}

public record RestockRequest(double Quantity, decimal? UnitCost);

public record WasteRequest(double Quantity, string Reason);

public record AdjustStockRequest(double NewQuantity, string Reason);

public record InventoryTransactionDto(
    int Id, int InventoryItemId, string InventoryItemName, string Type, double PreviousStock,
    double ChangedQuantity, double RemainingStock, string? Reason, string? ReferenceId, string UserName, DateTime CreatedAt);

// ---------- Purchase Orders ----------

public record PurchaseItemRequest(int InventoryItemId, double Quantity, string Unit, decimal UnitCost);

public record CreatePurchaseOrderRequest(string? SupplierName, string? Note, List<PurchaseItemRequest> Items);

public record PurchaseItemDto(int InventoryItemId, string InventoryItemName, double Quantity, string Unit, decimal UnitCost);

public record PurchaseOrderDto(int Id, string? SupplierName, string? Note, string CreatedByName, DateTime CreatedAt, List<PurchaseItemDto> Items);

// ---------- Recipes ----------

public record RecipeItemRequest(int InventoryItemId, double Quantity, string Unit);

public record UpsertRecipeRequest(List<RecipeItemRequest> Items);

public record RecipeItemDto(int InventoryItemId, string InventoryItemName, string InventoryItemUnit, double Quantity, string Unit);

public record RecipeDto(int MenuItemId, List<RecipeItemDto> Items);

// ---------- Settings ----------

public record UpdateSettingsRequest(
    decimal? TaxRatePct,
    string? Currency,
    string? Region,
    string? BusinessName,
    string? ReceiptHeader,
    string? ReceiptFooter,
    string? LogoUrl,
    string? PrimaryColor,
    string? QrStyle,
    string? ThemeMode,
    bool? TwoFactorEnabled,
    bool? TerminalPasscodeRequired,
    bool? InventoryAlertsEnabled,
    bool? ShiftReportsEnabled,
    string? Phone = null,
    string? Address = null,
    string? StoreHoursJson = null);
