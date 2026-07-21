using CafePOS.Api.Domain;

namespace CafePOS.Api.Contracts;

// ---------- Orders ----------

/// <summary>VariantId picks a Half/Full/... price instead of the item's base MenuItem.Price;
/// ModifierOptionIds are the selected toppings/add-ons, each adding its own price delta.
/// Both are validated (and priced) server-side — see OrderBuildingService.ResolveLinePricingAsync.</summary>
public record CreateOrderItemDto(int MenuItemId, int Qty, string? Modifier, int? VariantId = null, List<int>? ModifierOptionIds = null);

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

public record CancelOrderRequest(string? Reason);

// ---------- Order lifecycle (add item / fire / billing-time discounts / payment) ----------

public record AddOrderItemRequest(int MenuItemId, int Qty, string? Modifier, int? VariantId = null, List<int>? ModifierOptionIds = null);

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

/// <summary>Status is the derived overall stage; the New/Read/Preparing/Ready/Served Qty
/// fields are the real per-unit distribution (partial-quantity production) — they sum to
/// Qty. KDS renders per-stage counts and picks the card's column from the lowest non-empty.</summary>
public record SelectedModifierDto(int ModifierOptionId, string Name, decimal Price)
{
    public static SelectedModifierDto From(OrderItemModifier m) => new(m.ModifierOptionId, m.Name, m.Price);
}

public record OrderItemDto(int Id, int MenuItemId, string Name, int Qty, decimal Price, string? Modifier, int FireBatch, string Status,
    int NewQty, int ReadQty, int PreparingQty, int ReadyQty, int ServedQty, bool Voided, DateTime? VoidedAt,
    int? VariantId, string? VariantName, List<SelectedModifierDto> SelectedModifiers, string StationName);

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
    int? TokenNumber,
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
    bool Cancelled,
    DateTime? CancelledAt,
    string? CancelReason,
    DateTime CreatedAt,
    int? BranchId,
    string? CreatedByName,
    string? ServedByName,
    string? CouponCode,
    string? GiftCardCode,
    decimal GiftCardAmountApplied,
    string? PaymentMethod,
    int CurrentFireBatch,
    bool PendingStaffConfirmation,
    List<FireBatchDto> FireBatches)
{
    public static OrderDto From(Order o) => new(
        o.Id,
        $"#{1000 + o.Id}",
        o.Title,
        o.OrderType,
        o.TableCode,
        o.TokenNumber,
        o.GuestName,
        o.GuestPhone,
        o.CustomerId,
        o.Items.Select(i => new OrderItemDto(i.Id, i.MenuItemId, i.Name, i.Qty, i.Price, i.Modifier, i.FireBatch, i.Status.ToString().ToUpperInvariant(),
            i.NewQty, i.ReadQty, i.PreparingQty, i.ReadyQty, i.ServedQty, i.Voided, i.VoidedAt,
            i.VariantId, i.VariantName, i.SelectedModifiers.Select(SelectedModifierDto.From).ToList(), i.StationName)).ToList(),
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
        o.Cancelled,
        o.CancelledAt,
        o.CancelReason,
        o.CreatedAt,
        o.BranchId,
        o.CreatedByName,
        o.ServedByName,
        o.CouponCode,
        o.GiftCardCode,
        o.GiftCardAmountApplied,
        o.PaymentMethod,
        o.CurrentFireBatch,
        o.PendingStaffConfirmation,
        o.FireBatches.OrderBy(b => b.BatchNumber)
            .Select(b => new FireBatchDto(b.BatchNumber, b.Status.ToString().ToUpperInvariant(), b.FiredAt, $"#{1000 + b.Id}"))
            .ToList());
}

public record SetStatusRequest(string Status);

/// <summary>Advance some units of one line one stage forward. FromStage null = the line's
/// current least-progressed stage; Qty null = every unit at that stage.</summary>
public record AdvanceUnitsRequest(string? FromStage, int? Qty);

/// <summary>Production View bulk action. Allocations non-empty = advance exactly those
/// line/quantities; otherwise FIFO-allocate Qty across the dish's fired lines (oldest KOT
/// first).</summary>
public record BulkAdvanceRequest(int MenuItemId, string FromStage, int? Qty, List<BulkAdvanceAllocation>? Allocations);
public record BulkAdvanceAllocation(int OrderId, int ItemId, int Qty);

// ---------- Guest sessions (QR ordering — see docs/qr-ordering-session-plan) ----------

/// <summary>GuestName/GuestPhone are only used (and GuestPhone required, 10 digits — same
/// rule OrdersController.CreatePublic already enforces) the very first time a cart item is
/// added for a session, since that's what creates the underlying Order/Customer record;
/// every later call ignores them.</summary>
public record AddCartItemRequest(int MenuItemId, int Qty, string? Modifier, string? GuestName = null, string? GuestPhone = null,
    int? VariantId = null, List<int>? ModifierOptionIds = null);

/// <summary>Everything the guest page needs to render: the session's own status, the
/// cart (unfired items — FireBatch == 0 on the underlying order, see OrderBuildingService)
/// and, once the first item has been fired, the live order/KOT status. Order is null until
/// the very first cart-add creates the underlying (still-unfired) Order row.</summary>
public record GuestSessionStateDto(string Status, string TableCode, OrderDto? Order)
{
    public static GuestSessionStateDto From(GuestSession session, string tableCode, Order? order) =>
        new(session.Status.ToString().ToUpperInvariant(), tableCode, order is null ? null : OrderDto.From(order));
}

/// <summary>Result of POST session/scan — Case drives which screen the guest page shows
/// (doc Section 3's 5-CASE decision tree). State is populated for READY/BILL_LOCKED;
/// null for JOIN (another device already owns an active session on this table — the guest
/// page should prompt for POST session/join) and STAFF_ASSIST (an unpaid order sits on
/// this table with no live session — no new session is auto-created).</summary>
public record ScanResultDto(string Case, GuestSessionStateDto? State);

public record RevokeSessionRequest(string? Reason);

// ---------- Menu ----------

public record CreateMenuItemRequest(
    string Name,
    string Category,
    decimal Price,
    string? Icon,
    string? Subtitle,
    string? Image,
    string? Description,
    ProductType? ProductType = null,
    int? LinkedInventoryItemId = null,
    string? ShortCode = null,
    int? StationId = null,
    string? ItemType = null,
    string? VegNonVegType = null);

public record UpdateMenuItemRequest(
    string? Name,
    string? Category,
    decimal? Price,
    bool? Available,
    string? Subtitle,
    string? Image,
    string? Description,
    bool? Popular,
    ProductType? ProductType = null,
    int? LinkedInventoryItemId = null,
    string? ShortCode = null,
    int? StationId = null,
    string? ItemType = null,
    string? VegNonVegType = null);

public record BulkImportResultDto(int CreatedCount, int SkippedCount);

// ---------- Kitchen Stations ----------

public record StationDto(int Id, string Name, string Icon, int SortOrder, bool Active)
{
    public static StationDto From(Station s) => new(s.Id, s.Name, s.Icon, s.SortOrder, s.Active);
}

public record CreateStationRequest(string Name, string? Icon = null);

public record UpdateStationRequest(string? Name, string? Icon, int? SortOrder, bool? Active);

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

public record CreateInventoryItemRequest(string Name, string Category, double Max, string? Unit, decimal? UnitCost, double? MinStock = null, double? ReorderLevel = null, int? BranchId = null, DateOnly? ExpiryDate = null);

/// <summary>InventoryItem plus a server-computed LowStock flag (Current &lt;= ReorderLevel)
/// — replaces the frontend's old hardcoded current/max &lt;= 0.25 ratio guess.</summary>
public record InventoryItemDto(
    int Id, string Name, string Category, string Icon, double Current, double Max, string Unit,
    double DailyUsage, DateTime? LastRestockAt, decimal UnitCost, double MinStock, double ReorderLevel, bool LowStock, int? BranchId, bool IsActive)
{
    public static InventoryItemDto From(InventoryItem i) => new(
        i.Id, i.Name, i.Category, i.Icon, i.Current, i.Max, i.Unit, i.DailyUsage, i.LastRestockAt,
        i.UnitCost, i.MinStock, i.ReorderLevel, i.Current <= i.ReorderLevel, i.BranchId, i.IsActive);
}

public record RestockRequest(double Quantity, decimal? UnitCost, DateOnly? ExpiryDate = null);

public record WasteRequest(double Quantity, WasteReason Reason, string? Note = null);

public record AdjustStockRequest(double NewQuantity, string Reason);

public record InventoryTransactionDto(
    int Id, int InventoryItemId, string InventoryItemName, string Type, double PreviousStock,
    double ChangedQuantity, double RemainingStock, string? Reason, string? WasteReasonCode, string? ReferenceId, string UserName, DateTime CreatedAt);

// ---------- Inventory Batches (FIFO + expiry) ----------

/// <summary>One physical lot — see InventoryBatchService. DaysUntilExpiry is negative once
/// past ExpiryDate (IsExpired true); null for a batch with no ExpiryDate at all.</summary>
public record InventoryBatchDto(int Id, int InventoryItemId, string InventoryItemName, string Unit,
    double Quantity, decimal UnitCost, DateOnly? ExpiryDate, DateTime ReceivedAt, int? DaysUntilExpiry, bool IsExpired);

// ---------- Vendors ----------

public record VendorDto(int Id, string Name, string? Phone, string? Email, string? Gstin, string? Address, string? PaymentTerms, string? Notes, bool IsActive, DateTime CreatedAt);

public record CreateVendorRequest(string Name, string? Phone, string? Email, string? Gstin, string? Address, string? PaymentTerms, string? Notes);

public record UpdateVendorRequest(string Name, string? Phone, string? Email, string? Gstin, string? Address, string? PaymentTerms, string? Notes, bool IsActive);

// ---------- Purchase Orders ----------

public record PurchaseItemRequest(int InventoryItemId, double Quantity, string Unit, decimal UnitCost, DateOnly? ExpiryDate = null);

/// <summary>Supply VendorId to link a real vendor (preferred); SupplierName is the legacy
/// free-text fallback, still accepted when no vendor master entry exists yet.</summary>
public record CreatePurchaseOrderRequest(int? VendorId, string? SupplierName, string? Note, List<PurchaseItemRequest> Items);

public record PurchaseItemDto(int InventoryItemId, string InventoryItemName, double Quantity, string Unit, decimal UnitCost, DateOnly? ExpiryDate);

public record PurchaseOrderDto(int Id, int? VendorId, string? SupplierName, string? VendorPhone, string? Note, string CreatedByName, DateTime CreatedAt, List<PurchaseItemDto> Items);

// ---------- Recipes ----------

public record RecipeItemRequest(int InventoryItemId, double Quantity, string Unit);

public record UpsertRecipeRequest(List<RecipeItemRequest> Items);

public record RecipeItemDto(int InventoryItemId, string InventoryItemName, string InventoryItemUnit, double Quantity, string Unit);

public record RecipeDto(int MenuItemId, List<RecipeItemDto> Items);

/// <summary>Cost of one portion of a recipe against its menu price — see
/// RecipesController.GetCost / ReportsController.FoodCost.</summary>
public record RecipeItemCostDto(int InventoryItemId, string Name, double Quantity, string Unit, decimal LineCost);

public record RecipeCostDto(int MenuItemId, string MenuItemName, decimal IngredientCost, decimal MenuPrice, decimal FoodCostPct, List<RecipeItemCostDto> Items);

// ---------- Stock Take ----------

public record CreateStockTakeRequest(int? BranchId, string? Note);

public record RecordCountRequest(double CountedQty);

public record StockTakeLineDto(int Id, int InventoryItemId, string InventoryItemName, string Unit, double SystemQty, double? CountedQty, double? Variance);

public record StockTakeDto(int Id, string Status, int? BranchId, string? Note, string CreatedByName, DateTime CreatedAt,
    DateTime? FinalizedAt, string? FinalizedByName, List<StockTakeLineDto> Lines);

// ---------- Reports ----------

public record VarianceReportLineDto(int InventoryItemId, string Name, string Unit,
    double TheoreticalConsumption, double PurchasedQty, double WastageQty,
    double? LatestStockTakeVariance, DateTime? LatestStockTakeAt);

public record MissingRecipeAlertDto(int Id, int MenuItemId, string MenuItemName, int OccurrenceCount, DateTime FirstOccurredAt, DateTime LastOccurredAt);

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
    string? StoreHoursJson = null,
    bool? RequireStaffOrderConfirmation = null,
    string? KdsStageMode = null,
    bool? DineInEnabled = null,
    bool? TakeawayEnabled = null,
    bool? DeliveryEnabled = null,
    bool? QsrEnabled = null,
    bool? CashEnabled = null,
    bool? ReceiptShowAddress = null,
    bool? ReceiptShowWaiterName = null,
    bool? ReceiptShowGuestPhone = null,
    bool? ReceiptShowItemNotes = null,
    bool? ReceiptShowFooter = null,
    string? GstNumber = null);

// ---------- Order Note Suggestions ----------

public record OrderNoteSuggestionDto(int Id, string Text, int UsageCount)
{
    public static OrderNoteSuggestionDto From(OrderNoteSuggestion s) => new(s.Id, s.Text, s.UsageCount);
}

public record UpsertOrderNoteSuggestionRequest(string Text);

// ---------- Menu Features ----------

public record CreateVariantRequest(string Name, decimal Price, bool IsDefault = false);

public record UpdateVariantRequest(string? Name, decimal? Price, bool? IsAvailable = null, bool? IsDefault = null);

public record VariantDto(int Id, int MenuItemId, string Name, decimal Price, int SortOrder, bool IsAvailable, bool IsDefault)
{
    public static VariantDto From(Variant v) => new(v.Id, v.MenuItemId, v.Name, v.Price, v.SortOrder, v.IsAvailable, v.IsDefault);
}

// ---------- Modifiers (Spice, Add-ons, etc.) ----------

public record CreateModifierOptionRequest(string Name, decimal Price = 0);

public record UpdateModifierOptionRequest(string? Name, decimal? Price);

public record ModifierOptionDto(int Id, int ModifierId, string Name, decimal Price, int SortOrder)
{
    public static ModifierOptionDto From(ModifierOption o) => new(o.Id, o.ModifierId, o.Name, o.Price, o.SortOrder);
}

public record CreateModifierRequest(string Name, string Type = "MultiSelect", bool IsRequired = false);

public record UpdateModifierRequest(string? Name, string? Type, bool? IsRequired);

public record ModifierDto(int Id, int MenuItemId, string Name, string Type, bool IsRequired, int SortOrder, List<ModifierOptionDto> Options)
{
    public static ModifierDto From(Modifier m) => new(m.Id, m.MenuItemId, m.Name, m.Type, m.IsRequired, m.SortOrder,
        m.Options.OrderBy(o => o.SortOrder).Select(ModifierOptionDto.From).ToList());
}
