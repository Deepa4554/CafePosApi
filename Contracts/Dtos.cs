using CafePOS.Api.Domain;

namespace CafePOS.Api.Contracts;

// ---------- Orders ----------

public record CreateOrderItemDto(int MenuItemId, int Qty, string? Modifier);

public record CreateOrderRequest(
    string OrderType, // DINE_IN / TAKEAWAY / DELIVERY
    string? TableCode,
    string? GuestName,
    List<CreateOrderItemDto> Items,
    decimal DiscountPct = 0,
    string? CouponCode = null,
    int? BranchId = null,
    string? GuestPhone = null,
    // Who actually took/served this order — omit to default to the logged-in user's
    // own StaffMember record (self-service waiter); explicit when a Cashier/Manager/
    // Owner rings up an order on behalf of a different waiter from a shared counter POS.
    int? ServedByStaffId = null);

public record RefundOrderRequest(decimal? Amount, string? Reason);

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
public record CreatePublicOrderRequest(string? GuestName, List<CreateOrderItemDto> Items);

public record OrderItemDto(string Name, int Qty, decimal Price, string? Modifier);

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
    decimal Tax,
    decimal Total,
    string Status,
    bool Paid,
    bool Refunded,
    decimal? RefundedAmount,
    DateTime CreatedAt,
    int? BranchId,
    string? CreatedByName,
    string? ServedByName)
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
        o.Items.Select(i => new OrderItemDto(i.Name, i.Qty, i.Price, i.Modifier)).ToList(),
        o.Subtotal,
        o.DiscountPct,
        o.DiscountAmount,
        o.Tax,
        o.Total,
        o.Status.ToString().ToUpperInvariant(),
        o.Paid,
        o.Refunded,
        o.RefundedAmount,
        o.CreatedAt,
        o.BranchId,
        o.CreatedByName,
        o.ServedByName);
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
