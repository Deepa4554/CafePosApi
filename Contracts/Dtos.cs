using CafePOS.Api.Domain;

namespace CafePOS.Api.Contracts;

// ---------- Orders ----------

/// <summary>VariantId picks a Half/Full/... price instead of the item's base MenuItem.Price;
/// ModifierOptionIds are the selected toppings/add-ons, each adding its own price delta.
/// Both are validated (and priced) server-side — see OrderBuildingService.ResolveLinePricingAsync.
///
/// OpenPrice is the rate the biller typed for an MRP item (MenuItem.IsOpenPrice) — required
/// for one, ignored for everything else, so an ordinary item can never be re-priced from the
/// wire.</summary>
public record CreateOrderItemDto(int MenuItemId, int Qty, string? Modifier, int? VariantId = null, List<int>? ModifierOptionIds = null,
    decimal? OpenPrice = null);

public record CreateOrderRequest(
    string OrderType, // DINE_IN / TAKEAWAY / DELIVERY
    string? TableCode,
    string? GuestName,
    List<CreateOrderItemDto> Items,
    // Order-time manual discount only. Coupons and gift cards are NO LONGER applied here —
    // they're billing-time actions on a served order (see bill-coupon / bill-giftcard).
    decimal DiscountPct = 0,
    // A flat rupee discount, as an alternative to DiscountPct — sent when the biller typed
    // "₹50 off" rather than a percentage. Stored as-is so it stays exactly ₹50 however the cart
    // changes afterwards; the POS used to convert it to a percentage, which then drifted (₹50 on
    // a ₹400 bill became ₹62.50 once another item was added). When both are sent, the flat amount
    // wins. See OrderBuildingService.BuildOrderAsync.
    decimal DiscountAmount = 0,
    int? BranchId = null,
    string? GuestPhone = null,
    // Who actually took/served this order — omit to default to the logged-in user's
    // own StaffMember record (self-service waiter); explicit when a Cashier/Manager/
    // Owner rings up an order on behalf of a different waiter from a shared counter POS.
    int? ServedByStaffId = null,
    // Saved onto the guest's Customer record (see OrderBuildingService.FindOrCreateCustomerAsync)
    // rather than the order itself — useful for delivery, and remembered for next visit.
    string? GuestAddress = null);

/// <summary>Fills in (or corrects) the guest's details on an order that already exists —
/// the "cashier hits Send-on-WhatsApp and only then realises no number was taken" case, which
/// is why Order.GuestPhone is documented as capturable after placement. Deliberately allowed
/// on a paid order too: sending the bill is the main reason to add a number at all, and that
/// happens after settling. Only non-null fields are applied, so the phone can be set without
/// disturbing a name that's already there. Setting a phone also re-links the order's CRM
/// customer — see OrdersController.UpdateGuest.</summary>
public record UpdateOrderGuestRequest(string? GuestName = null, string? GuestPhone = null);

public record RefundOrderRequest(decimal? Amount, string? Reason);

public record CancelOrderRequest(string? Reason);

public record ShiftTableRequest(string NewTableCode);

// ---------- Order lifecycle (add item / fire / billing-time discounts / payment) ----------

/// <summary>OpenPrice carries the biller's typed rate for an MRP item — see CreateOrderItemDto.</summary>
public record AddOrderItemRequest(int MenuItemId, int Qty, string? Modifier, int? VariantId = null, List<int>? ModifierOptionIds = null,
    decimal? OpenPrice = null);

/// <summary>Corrects an existing line's quantity — <paramref name="Qty"/> is the line's FINAL
/// quantity, not a delta. Must be ≥ 1; removing a line entirely is still DELETE .../items/{itemId},
/// which carries its own "order must keep at least one item" rule. Reason is required only when the
/// change pulls back units that are already Preparing/Ready or recorded as served (same wastage
/// rule as RemoveItem) — see OrdersController.UpdateItemQty.
///
/// <paramref name="ReasonCode"/> is the picked one of the fixed reasons; <paramref name="Reason"/>
/// is the free-text note beside it, and is what's required when the code is Other.
/// <paramref name="Unprepared"/> is the staff member's assertion that the units being pulled back
/// were never actually made, and is the only thing that puts their stock back. It's honoured ONLY
/// for units the line had recorded as SERVED — at every other stage the server knows the answer
/// itself and ignores this.</summary>
public record UpdateOrderItemQtyRequest(int Qty, string? Reason = null,
    VoidReasonCode ReasonCode = VoidReasonCode.Other, bool Unprepared = false);

/// <summary>Overrides one line's per-unit rate on THIS order only — <paramref name="Price"/> is the
/// new effective rate, replacing whatever the catalog priced the line at, and the menu is left
/// untouched. Must be > 0. Reason is required only when the rate goes DOWN (the direction that
/// costs the cafe money) — see OrdersController.UpdateItemPrice.</summary>
public record UpdateOrderItemPriceRequest(decimal Price, string? Reason = null);

/// <summary>Manager-only markdown applied at the billing stage (Served). Supply exactly
/// one of Pct (percentage of subtotal) or Amount (flat).</summary>
public record BillDiscountRequest(decimal? Pct, decimal? Amount);

public record BillCouponRequest(string Code);

public record BillGiftCardRequest(string Code);

/// <summary>Billing-time Service Charge / Packing Charge / Delivery Charge / Tip / Round Off,
/// applied together as one adjustment sheet — see OrdersController.ApplyBillCharges. Every
/// field is optional; only the ones supplied (non-null) are changed, so a cashier can set just
/// one (e.g. TipAmount) without re-sending the others. Send 0 to explicitly clear a charge.
/// ServiceCharge accepts either a percentage of Subtotal OR a flat amount — not both.</summary>
public record BillChargesRequest(
    decimal? ServiceChargePct,
    decimal? ServiceChargeAmount,
    decimal? PackingChargeAmount,
    decimal? DeliveryChargeAmount,
    decimal? TipAmount,
    decimal? RoundOffAmount);

/// <summary>Redeems Points of the order's linked customer as a bill-time discount (1 point =
/// ₹1, matching the earn rate in OrderBuildingService.RecordVisit). See OrdersController.
/// ApplyBillLoyalty — capped at both the customer's available balance and what's left owed.</summary>
public record BillLoyaltyRequest(int Points);

/// <summary>One tender in a split payment — e.g. part Cash, part Card. "Due" is a tender
/// here too, but a special one: it settles the bill without any money changing hands and
/// parks the amount on the customer's khata instead (see OrdersController.Pay).</summary>
public record PaymentSplitRequest(string Method, decimal Amount);

/// <summary>How the settled bill was paid — Cash / Card / UPI / Multiple. Optional; the
/// legacy pay call with no body still works (payment method just stays unrecorded). Supply
/// Splits instead of PaymentMethod to settle across more than one tender. By default the
/// splits must add up to the order's remaining balance (see OrdersController.Pay);
/// AllowPartial relaxes that so a cashier can deliberately collect less than the full amount
/// and leave the rest owing — the order becomes PartiallyPaid instead of Paid, and Pay can be
/// called again later with the remainder. PaymentMethod is ignored when Splits is given, and
/// the order's PaymentMethod summary becomes "Multiple" once more than one tender has been
/// recorded across all Pay calls. KeepOpen is the opposite kind of exception: the payment
/// fully covers (or exceeds) the balance, but the order should NOT close — e.g. a Pay First
/// order that's still expected to have more items added. The order stays Paid=false/
/// PartiallyPaid=true even at 100% covered; OrdersController.Close finalizes it later once
/// nothing more will be added.
///
/// GuestName/GuestPhone are only read when a "Due" (udhaar) tender is in play, where they're
/// compulsory: the credit has to land on an identifiable customer's khata, and the number is
/// what that khata is looked up by. Both are stamped onto the order as a side effect, so a
/// bill rung up as a walk-in ends up naming whoever actually took the credit. Leave them null
/// when the order already carries a real name and number — they're an override for the ones
/// the cashier types into the payment picker at settle time, not a repeat of what's on file.
/// Due is deliberately incompatible with AllowPartial and KeepOpen (both leave the order open,
/// which would put the same rupees on the order AND on the khata).
///
/// UnfiredItems answers "what about the lines that never went to the kitchen?" and takes exactly
/// one value, "keep" — an explicit "yes, bill them anyway". It is REQUIRED whenever such a line
/// exists and the call would close the bill; without it the settle is refused (see
/// OrdersController.EnsureUnfiredItemsResolved, which explains why the answers that change the
/// total are separate calls made before settling rather than values here). Null on every bill
/// that has no unfired line, which is almost all of them.</summary>
public record PayRequest(
    string? PaymentMethod,
    List<PaymentSplitRequest>? Splits = null,
    bool AllowPartial = false,
    bool KeepOpen = false,
    string? GuestName = null,
    string? GuestPhone = null,
    string? UnfiredItems = null);

/// <summary>Body for OrdersController.Close — see PayRequest.UnfiredItems, which this carries for
/// exactly the same reason: Close is the other call that flips a bill to Paid, so it needs the
/// same answer about lines the kitchen never saw. Optional as a whole; a Close on an order with
/// no unfired line needs no body at all.</summary>
public record CloseOrderRequest(string? UnfiredItems = null);

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
/// <summary>Price is the per-unit snapshot — this selection adds Price * Qty to its line.</summary>
public record SelectedModifierDto(int ModifierOptionId, string Name, decimal Price, int Qty)
{
    public static SelectedModifierDto From(OrderItemModifier m) => new(m.ModifierOptionId, m.Name, m.Price, m.Qty);
}

public record OrderItemDto(int Id, int MenuItemId, string Name, int Qty, decimal Price, string? Modifier, int FireBatch, string Status,
    int NewQty, int ReadQty, int PreparingQty, int ReadyQty, int ServedQty, bool Voided, DateTime? VoidedAt,
    int? VariantId, string? VariantName, List<SelectedModifierDto> SelectedModifiers, string StationName,
    decimal? TaxRatePct, decimal TaxableAmount, decimal TaxAmount, string? VegNonVegType = null, string? Subtitle = null);

/// <summary>One row of the bill's tax summary — the taxable value and tax charged at a single
/// rate. A GST invoice has to break tax down per slab rather than print one combined figure,
/// so an order mixing a 5% item with a 12% one shows two rows here.</summary>
public record OrderTaxLineDto(decimal RatePct, decimal TaxableAmount, decimal TaxAmount)
{
    /// <summary>Groups an order's live lines by their effective rate. `fallbackRatePct` stands
    /// in for lines with no snapshot (placed before tax groups, or an item with no group and no
    /// tenant default) — the same rate RecomputeTotals billed them at.</summary>
    public static List<OrderTaxLineDto> From(Order o, decimal fallbackRatePct) =>
        o.Items
            .Where(i => !i.Voided)
            .GroupBy(i => i.TaxRatePct ?? fallbackRatePct)
            .Where(g => g.Sum(i => i.TaxAmount) != 0 || g.Key != 0)
            .OrderBy(g => g.Key)
            .Select(g => new OrderTaxLineDto(g.Key, g.Sum(i => i.TaxableAmount), g.Sum(i => i.TaxAmount)))
            .ToList();
}

/// <summary>One fire round's own kitchen status — see Order.FireBatches. KDS flattens an
/// order's non-Served batches into separate ticket cards from this list, instead of relying
/// on the single rollup Status below. KotNumber is a tenant-wide sequential ticket id (same
/// "#1000+id" convention as Order.Number) — the KOT-wise KDS view sorts/labels by this
/// instead of by table, matching how a KOT chit works in a physical kitchen.</summary>
public record FireBatchDto(int BatchNumber, string Status, DateTime FiredAt, string KotNumber);

/// <summary>One settled tender — see Order.Payments.</summary>
public record OrderPaymentDto(string Method, decimal Amount)
{
    public static OrderPaymentDto From(OrderPayment p) => new(p.Method, p.Amount);
}

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
    decimal OfferDiscountAmount,
    string? AppliedOfferTitle,
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
    List<FireBatchDto> FireBatches,
    List<OrderPaymentDto> Payments,
    decimal LoyaltyDiscountAmount,
    int LoyaltyPointsRedeemed,
    decimal ServiceChargeAmount,
    decimal PackingChargeAmount,
    decimal DeliveryChargeAmount,
    decimal TipAmount,
    decimal RoundOffAmount,
    // Sum of every OrderPayment recorded so far — non-zero before Paid flips true only when
    // a partial payment has been collected (see OrdersController.Pay). Deliberately counts the
    // "Due" tender too, so BalanceDue below stays 0 on a settled credit bill: from the ORDER's
    // point of view that bill is closed and there is nothing further to collect against it.
    // What's still owed lives on the customer's khata instead — see DueAmount.
    decimal AmountPaid,
    decimal BalanceDue,
    // How much of AmountPaid was credit rather than money in the till (the "Due" tender). Zero
    // on an ordinary bill. Non-zero means this much moved onto the customer's khatabook at
    // settle time and is collected there, not here.
    decimal DueAmount,
    // True once at least one tender has been collected but the bill isn't fully settled yet.
    // Never true at the same time as Paid.
    bool PartiallyPaid,
    // The linked customer's redeemable point balance — null unless the caller (currently only
    // Get-by-id) loaded the Customer navigation; the Points screen's own AvailablePoints
    // lookup is the source of truth, this is just a checkout-time preview.
    int? CustomerAvailablePoints)
{
    public static OrderDto From(Order o)
    {
        var amountPaid = o.Payments.Sum(p => p.Amount);
        return new(
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
            i.VariantId, i.VariantName, i.SelectedModifiers.Select(SelectedModifierDto.From).ToList(), i.StationName,
            i.TaxRatePct, i.TaxableAmount, i.TaxAmount, i.VegNonVegType?.ToString(), i.Subtitle)).ToList(),
        o.Subtotal,
        o.DiscountPct,
        o.DiscountAmount,
        o.BillDiscountAmount,
        o.CouponDiscountAmount,
        o.OfferDiscountAmount,
        o.AppliedOfferTitle,
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
            .ToList(),
        o.Payments.Select(OrderPaymentDto.From).ToList(),
        o.LoyaltyDiscountAmount,
        o.LoyaltyPointsRedeemed,
        o.ServiceChargeAmount,
        o.PackingChargeAmount,
        o.DeliveryChargeAmount,
        o.TipAmount,
        o.RoundOffAmount,
        amountPaid,
        Math.Max(0, o.Total - amountPaid),
        o.Payments.Where(p => p.Method == "Due").Sum(p => p.Amount),
        !o.Paid && amountPaid > 0,
        o.Customer?.AvailablePoints);
    }
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
    string? VegNonVegType = null,
    int? TaxGroupId = null,
    /// <summary>MRP item — the biller types the rate at billing time. See MenuItem.IsOpenPrice.</summary>
    bool? IsOpenPrice = null);

/// <summary>ImageDataUri is a "data:image/...;base64,..." string — same shape the client's
/// own image picker already produces for every other photo upload in the app.</summary>
public record ImportMenuPhotoRequest(string ImageDataUri);

/// <summary>Raw text already OCR'd client-side (Tesseract.js) — see MenuController.CategorizeText.</summary>
public record CategorizeMenuTextRequest(string OcrText);

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
    string? VegNonVegType = null,
    /// <summary>Which tax slab to bill this item at. Pass 0 to clear it back to the
    /// tenant default (null on a PATCH means "leave unchanged", so it can't clear).</summary>
    int? TaxGroupId = null,
    /// <summary>MRP item — the biller types the rate at billing time. See MenuItem.IsOpenPrice.</summary>
    bool? IsOpenPrice = null);

public record BulkImportResultDto(int CreatedCount, int SkippedCount);

// ---------- Tax Groups ----------

public record TaxGroupDto(int Id, string Name, decimal RatePct, bool IsDefault)
{
    public static TaxGroupDto From(TaxGroup t) => new(t.Id, t.Name, t.RatePct, t.IsDefault);
}

public record CreateTaxGroupRequest(string Name, decimal RatePct, bool IsDefault = false);

public record UpdateTaxGroupRequest(string? Name, decimal? RatePct, bool? IsDefault);

// ---------- Kitchen Stations ----------

public record StationDto(int Id, string Name, string Icon, int SortOrder, bool Active)
{
    public static StationDto From(Station s) => new(s.Id, s.Name, s.Icon, s.SortOrder, s.Active);
}

public record CreateStationRequest(string Name, string? Icon = null);

public record UpdateStationRequest(string? Name, string? Icon, int? SortOrder, bool? Active);

// ---------- Menu Categories (default-station lookup) ----------

public record CategoryDto(string Name, int? DefaultStationId, string? DefaultStationName, int ItemCount, int SortOrder);

public record SetCategoryDefaultStationRequest(int? StationId);

public record ApplyCategoryStationRequest(int StationId);

public record CreateCategoryRequest(string Name);

public record RenameCategoryRequest(string NewName);

/// <summary>The categories in the order they should appear, front first. Any category left
/// out keeps whatever position it already had.</summary>
public record ReorderCategoriesRequest(List<string> Names);

/// <summary>What a rename/delete actually touched. Both operations move rows around behind
/// one tap, so the client reports the counts back rather than claiming a silent success —
/// MergedInto is set when a rename landed on a name that already existed.</summary>
public record CategoryMutationResultDto(
    string Name,
    int MovedItemCount,
    int UpdatedOfferCount,
    bool MergedInto);

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

/// <summary>Code is the table's display name on the floor plan. Optional — left null/blank
/// the server auto-numbers the next "T{n}", which is what the app sent before naming was
/// offered, so older builds keep working unchanged.</summary>
public record CreateTableRequest(string Zone, int Seats, string? Code = null);

public record MergeTableRequest(int TargetHostTableId);

// ---------- Inventory ----------

public record CreateInventoryItemRequest(string Name, string Category, double Max, string? Unit, decimal? UnitCost, double? MinStock = null, double? ReorderLevel = null, int? BranchId = null, DateOnly? ExpiryDate = null);

/// <summary>Edits an item's own details only. Current stock is deliberately absent — that
/// moves through Restock/Waste/Adjust so every change keeps a ledger entry. BranchId is
/// absent too: the item's batches and transactions are already booked against its branch.</summary>
public record UpdateInventoryItemRequest(string Name, string Category, double Max, string? Unit, decimal? UnitCost, double? MinStock = null, double? ReorderLevel = null);

/// <summary>One row of a bulk stock/rate sheet — see InventoryController.BulkImport.
/// CurrentStock is a physical count (it sets the figure, it doesn't add to it) and UnitCost
/// is the rate per <see cref="Unit"/>; both are converted when the row's unit differs from
/// the item's stored one.</summary>
public record InventoryImportRowRequest(
    string Name,
    string Unit,
    double CurrentStock,
    decimal UnitCost,
    string? Category = null,
    double? MaxStock = null,
    double? ReorderLevel = null);

public record InventoryImportRowError(int RowNumber, string Name, string Reason);

public record InventoryImportResultDto(
    int ItemsCreated,
    int ItemsUpdated,
    int RowsWithErrors,
    List<InventoryImportRowError> Errors);

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

public record TransactionsPagedResult(
    List<InventoryTransactionDto> Items, int TotalItems, int TotalPages, int PageNumber, int PageSize);

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

/// <summary>UnitCost is the expected/quoted price, if known — optional, since the real
/// price isn't confirmed until Receive.</summary>
public record PurchaseItemRequest(int InventoryItemId, double Quantity, string Unit, decimal? UnitCost = null);

/// <summary>Supply VendorId to link a real vendor (preferred); SupplierName is the legacy
/// free-text fallback, still accepted when no vendor master entry exists yet.</summary>
public record CreatePurchaseOrderRequest(int? VendorId, string? SupplierName, string? Note, List<PurchaseItemRequest> Items);

public record ReceivePurchaseItemRequest(int PurchaseItemId, double ReceivedQuantity, decimal UnitCost, DateOnly? ExpiryDate);

/// <summary>Every line on the order must be covered — partial receipt isn't supported yet.</summary>
public record ReceivePurchaseOrderRequest(List<ReceivePurchaseItemRequest> Items);

public record PurchaseItemDto(int PurchaseItemId, int InventoryItemId, string InventoryItemName, double Quantity, string Unit, decimal? UnitCost, DateOnly? ExpiryDate, double? ReceivedQuantity);

public record PurchaseOrderDto(int Id, int? VendorId, string? SupplierName, string? VendorPhone, string? Note, string Status, string CreatedByName, DateTime CreatedAt, DateTime? ReceivedAt, string? ReceivedByName, List<PurchaseItemDto> Items);

// ---------- Recipes ----------

public record RecipeItemRequest(int InventoryItemId, double Quantity, string Unit);

public record UpsertRecipeRequest(List<RecipeItemRequest> Items);

public record RecipeItemDto(int InventoryItemId, string InventoryItemName, string InventoryItemUnit, double Quantity, string Unit);

public record RecipeDto(int MenuItemId, List<RecipeItemDto> Items);

/// <summary>Cost of one portion of a recipe against its menu price — see
/// RecipesController.GetCost / ReportsController.FoodCost.</summary>
public record RecipeItemCostDto(int InventoryItemId, string Name, double Quantity, string Unit, decimal LineCost);

public record RecipeCostDto(int MenuItemId, string MenuItemName, decimal IngredientCost, decimal MenuPrice, decimal FoodCostPct, List<RecipeItemCostDto> Items);

// ---------- Recipe + Inventory bulk import ----------

/// <summary>One row of a bulk CSV/Excel import — the recipe (bill of materials) only:
/// which menu item uses which ingredient, and how much per serving. Deliberately carries
/// no stock quantity or cost: those stay owned by the Inventory screen (where stock only
/// moves via Restock/Waste/Adjust so every change keeps a ledger entry), and per-dish
/// costing then falls out of recipe quantity × the ingredient's own UnitCost — see
/// RecipesController.GetCost. Repeated appearances of the same ingredient across several
/// menu items' rows are expected (e.g. "Butter" in ten recipes).</summary>
public record RecipeImportRowRequest(
    string MenuItemName,
    string IngredientName,
    double Quantity,
    string Unit);

public record RecipeImportRowError(int RowNumber, string MenuItemName, string IngredientName, string Reason);

public record RecipeImportResultDto(
    int MenuItemsUpdated,
    int IngredientsCreated,
    int RowsWithErrors,
    List<RecipeImportRowError> Errors);

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
    string? BusinessType,
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
    bool? MorningShiftEnabled = null,
    bool? EveningShiftEnabled = null,
    bool? NightShiftEnabled = null,
    bool? GeneralShiftEnabled = null,
    bool? ReceiptShowAddress = null,
    bool? ReceiptShowWaiterName = null,
    bool? ReceiptShowGuestPhone = null,
    bool? ReceiptShowItemNotes = null,
    bool? ReceiptShowFooter = null,
    string? GstNumber = null,
    bool? OrderPlacedAlertsEnabled = null,
    bool? OrderPendingConfirmationAlertsEnabled = null,
    bool? OrderReadyAlertsEnabled = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    decimal? ServiceChargeDefaultPct = null,
    bool? ServiceChargeAutoApplyDineIn = null,
    bool? ServiceChargeAutoApplyTakeaway = null,
    bool? ServiceChargeAutoApplyDelivery = null,
    bool? ServiceChargeAutoApplyToken = null,
    bool? ServiceChargeClearDefault = null,
    decimal? PackingChargeDefaultAmount = null,
    bool? PackingChargeAutoApplyDineIn = null,
    bool? PackingChargeAutoApplyTakeaway = null,
    bool? PackingChargeAutoApplyDelivery = null,
    bool? PackingChargeAutoApplyToken = null,
    bool? PackingChargeClearDefault = null,
    decimal? DeliveryChargeDefaultAmount = null,
    bool? DeliveryChargeAutoApplyDineIn = null,
    bool? DeliveryChargeAutoApplyTakeaway = null,
    bool? DeliveryChargeAutoApplyDelivery = null,
    bool? DeliveryChargeAutoApplyToken = null,
    bool? DeliveryChargeClearDefault = null,
    bool? ApprovalAlertsEnabled = null,
    /// <summary>The cafe's UPI address for bill QR codes (see CafeSettings.UpiVpa). Send an
    /// empty string to clear it — unlike the toggles above, "" is a meaningful value here
    /// (stop showing the UPI QR) rather than "leave unchanged", which is what null means.</summary>
    string? UpiVpa = null);

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
