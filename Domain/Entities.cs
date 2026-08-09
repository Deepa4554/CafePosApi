using System.ComponentModel.DataAnnotations.Schema;

namespace CafePOS.Api.Domain;

public enum OrderStatus
{
    New,
    Read,    // TEMP: Keep for migration, will be removed after data is migrated
    Preparing,
    Ready,
    Served,
}

/// <summary>Prepared items consume ingredients via a Recipe (BOM) on sale; Independent
/// items decrease their own linked InventoryItem stock directly (e.g. bottled water).</summary>
public enum ProductType { Prepared, Independent }

public enum ItemType { Recipe, Retail, Service, Combo }

public enum VegNonVegType { Veg, NonVeg, Jain, Eggetarian }

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
    /// <summary>Staff pinned this item to the front of the POS grid — the handful of things a
    /// till rings up all day, kept out of the alphabetical scroll. Distinct from
    /// <see cref="Popular"/>, which only paints a badge and never moves anything, and from the
    /// best-sellers list, which is derived from real sales rather than chosen.
    ///
    /// Cafe-wide, not per-user (this is tenant-scoped like every other MenuItem field), and
    /// deliberately NOT honoured by the customer-facing QR menu: MenuController.List serves both,
    /// so the ordering is applied by the POS grid itself rather than by the query.</summary>
    public bool Pinned { get; set; }
    public ProductType ProductType { get; set; } = ProductType.Prepared;
    /// <summary>Only set when ProductType == Independent — the InventoryItem this menu
    /// item sells directly from.</summary>
    public int? LinkedInventoryItemId { get; set; }
    public string? ShortCode { get; set; } // e.g., "CAPP" for Cappuccino, max 5 chars, unique per tenant
    /// <summary>Which kitchen station preps this item — e.g. Main Kitchen, Bar, Dessert.
    /// Drives KDS station filtering and per-station KOT print routing. FK to Station
    /// (a managed per-tenant list, see StationsController), not free text.</summary>
    public int StationId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Station? Station { get; set; }
    /// <summary>Convenience read-only projection of Station.Name for API responses — flat
    /// like every other MenuItem field, instead of a nested object. Only resolves past the
    /// "Kitchen" fallback when the caller loaded this item with .Include(m => m.Station).</summary>
    [NotMapped]
    public string StationName => Station?.Name ?? "Kitchen";
    public ItemType ItemType { get; set; } = ItemType.Recipe;
    public VegNonVegType? VegNonVegType { get; set; } // null if not applicable
    /// <summary>Which tax slab this item is billed at. Null falls back to the tenant's
    /// default TaxGroup, then CafeSettings.TaxRatePct — see <see cref="TaxGroup"/>.</summary>
    public int? TaxGroupId { get; set; }

    public List<Variant> Variants { get; set; } = [];
    public List<Modifier> Modifiers { get; set; } = [];
}

/// <summary>A kitchen prep station (e.g. "Main Kitchen", "Bar", "Dessert") — a managed,
/// per-tenant list so MenuItem.StationId can't drift into typo'd duplicates the way a
/// free-text field would. Deactivate (Active=false) rather than delete once referenced
/// by a MenuItem, same convention as InventoryItem.IsActive.</summary>
public class Station : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public required string Name { get; set; }
    public string Icon { get; set; } = "chef-hat";
    public int SortOrder { get; set; } = 0;
    public bool Active { get; set; } = true;
}

/// <summary>A per-tenant default station for a MenuItem.Category value — Category itself
/// stays a free-text string on MenuItem (see below), this is just a lookup so a cafe can
/// set "everything in Beverages defaults to the Bar station" once instead of tagging every
/// item individually. Rows are created lazily (only once an Owner sets a default for that
/// category name, see CategoriesController) — a category with items but no default set
/// simply has no row here yet.</summary>
public class MenuCategory : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public required string Name { get; set; }
    public int? DefaultStationId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Station? DefaultStation { get; set; }
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

/// <summary>Half/Full plate or size variants — same dish, different portion/size, different price.</summary>
public class Variant : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int MenuItemId { get; set; }
    public required string Name { get; set; } // "Half", "Full", "Small", "Medium", "Large", etc.
    public decimal Price { get; set; } // absolute price, not delta
    public bool IsAvailable { get; set; } = true; // variant-level 86 toggle
    public bool IsDefault { get; set; } = false; // single tap on order screen selects this
    public int SortOrder { get; set; } = 0;
}

/// <summary>Modifiers are optional add-ons or customizations — spice level, extra toppings, etc.
/// Think of it as a group (e.g., "Spice Level" or "Add-ons") containing multiple options.</summary>
public class Modifier : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int MenuItemId { get; set; }
    public required string Name { get; set; } // e.g., "Spice Level", "Add-ons"
    public string Type { get; set; } = "MultiSelect"; // Radio, MultiSelect, Quantity
    public bool IsRequired { get; set; } = false;
    public int SortOrder { get; set; } = 0;

    public List<ModifierOption> Options { get; set; } = [];
}

/// <summary>Individual option within a modifier — e.g., "Mild", "Medium", "Spicy" for spice level.</summary>
public class ModifierOption : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int ModifierId { get; set; }
    public required string Name { get; set; } // e.g., "Mild", "Extra Cheese"
    public decimal Price { get; set; } = 0; // price adjustment, can be 0 or negative
    public int SortOrder { get; set; } = 0;
}


/// <summary>A free-text order-item note (e.g. "No onion", "Extra spicy") this cafe's staff
/// has typed before — surfaced back as a tap-to-apply suggestion chip next time, so a
/// recurring instruction only needs to be typed once. See OrderNoteSuggestionsController.</summary>
public class OrderNoteSuggestion : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public required string Text { get; set; }
    public int UsageCount { get; set; } = 1;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
}

public class CafeTable : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>Display code shown on the floor plan, e.g. "T1".</summary>
    public required string Code { get; set; }
    public required string Zone { get; set; }
    public int Seats { get; set; }
    /// <summary>Set only on a "guest" table temporarily folded into another ("host") table's
    /// seating for a party too big for one table — e.g. T5.MergedIntoTableId = T3.Id. Null
    /// on every standalone table, and on a host itself (structure is flat: a host can have
    /// many guests, but a guest can never itself be a host — see TablesController.Merge).
    /// Merging is only ever offered between two currently-EMPTY tables (TablesController.List
    /// derives occupancy from live Orders, never from this field) — a big party's actual
    /// order still just goes on the host's own Code once seated, nothing else changes about
    /// how orders work. TablesController.List hides any table with this set from the normal
    /// grid and folds its Seats into the host's reported total; Unmerge clears it back to null,
    /// fully reversible since Seats itself is never mutated on either row.</summary>
    public int? MergedIntoTableId { get; set; }
}

/// <summary>One row per (tenant, calendar day) — LastNumber is incremented atomically via an
/// UPSERT (see OrderBuildingService.NextTokenNumberAsync) to hand out the next QSR token
/// number for that day without a race under concurrent order creation. A new day gets a new
/// row starting at 1 — tokens never carry over.</summary>
public class TokenCounter : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public DateOnly Date { get; set; }
    public int LastNumber { get; set; }
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
    /// <summary>Counter/QSR ticket number — stamped only for OrderType == "QSR", scoped to
    /// TokenDate via TokenCounter (see OrderBuildingService.NextTokenNumberAsync). Null for
    /// every other order type.</summary>
    public int? TokenNumber { get; set; }
    /// <summary>The calendar day TokenNumber was issued on (cafe-local date) — lets the Token
    /// Dashboard scope "today's active tokens" without the number itself ever needing to be
    /// globally unique across days.</summary>
    public DateOnly? TokenDate { get; set; }
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

    /// <summary>Where a DELIVERY order is going, as the customer typed it — flat/floor/landmark
    /// included, because that part is for the human rider, not the map. Null for every other
    /// order type. Latitude/Longitude come from the customer's own device (the QR page asks the
    /// browser for its location) and are what a courier API actually routes on; the two are
    /// captured together but neither substitutes for the other — a rooftop-accurate pin still
    /// doesn't say "3rd floor, blue gate".</summary>
    public string? DeliveryAddress { get; set; }
    public decimal? DeliveryLatitude { get; set; }
    public decimal? DeliveryLongitude { get; set; }

    /// <summary>Set once a third-party rider has been booked for this order (see
    /// DeliveryController / BorzoClient). CourierProvider names which service, so a cafe that
    /// switches later can still read its old orders. All null until someone presses Book rider —
    /// booking is never automatic, since every booking costs the cafe money.</summary>
    public string? CourierProvider { get; set; }
    public string? CourierOrderId { get; set; }
    /// <summary>Provider's own status string, refreshed by their callback — e.g. Borzo's
    /// new/available/active/completed/canceled. Kept as free text rather than an enum: it's
    /// the provider's vocabulary, and inventing a mapping would only lose detail.</summary>
    public string? CourierStatus { get; set; }
    /// <summary>Per-drop link the customer can watch the rider on — safe to send over WhatsApp,
    /// it exposes nothing but this one delivery.</summary>
    public string? CourierTrackingUrl { get; set; }
    /// <summary>What the courier charged the cafe. Distinct from DeliveryChargeAmount, which is
    /// what the *customer* was billed — the two only match when BorzoPassFeeToCustomer is on,
    /// and the gap between them is exactly what the cafe is absorbing.</summary>
    public decimal? CourierFeeAmount { get; set; }
    public string? CourierRiderName { get; set; }
    public string? CourierRiderPhone { get; set; }
    public DateTime? CourierBookedAt { get; set; }

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
    /// <summary>Loyalty points redeemed as a bill-time discount (1 point = ₹1, matching the
    /// earn rate in OrderBuildingService.RecordVisit) — paired with LoyaltyPointsRedeemed,
    /// summed into the same discount pool as Coupon/GiftCard by RecomputeTotals.</summary>
    public decimal LoyaltyDiscountAmount { get; set; }
    public int LoyaltyPointsRedeemed { get; set; }
    /// <summary>Billing-time charges — added on top of tax, not themselves taxed (kept simple
    /// rather than re-running per-line GST on a flat add-on). ServiceCharge/Packing/Delivery/
    /// Tip are always ≥ 0; RoundOff can be either sign (negative rounds the total down).</summary>
    public decimal ServiceChargeAmount { get; set; }
    public decimal PackingChargeAmount { get; set; }
    public decimal DeliveryChargeAmount { get; set; }
    public decimal TipAmount { get; set; }
    public decimal RoundOffAmount { get; set; }
    /// <summary>How the bill was settled — Cash / Card / UPI / Multiple. Set when the order
    /// is marked paid; null until then.</summary>
    public string? PaymentMethod { get; set; }
    /// <summary>Optimistic-concurrency token over this order's money state, bumped by exactly
    /// one on every Pay / Close / Refund (see OrdersController.SavePaymentStateAsync). Two
    /// staff devices settling the same table in the same instant both read Paid == false and
    /// used to both record a full payment row — the bill went down as paid twice and the
    /// cash-drawer/revenue reports stopped matching. Now both carry the same original value
    /// into their UPDATE, so the second one matches zero rows and comes back as a 409 with
    /// its payment rows rolled back rather than a silent duplicate settle.
    ///
    /// Deliberately scoped to the money paths only: AddItem/Fire/status changes never touch
    /// it, so two waiters editing the same order concurrently behave exactly as they did
    /// before. The one new conflict is an edit that races a settle — which is a conflict
    /// worth surfacing, since the bill it was editing is now closed.</summary>
    public int PaymentVersion { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.New;
    /// <summary>Increments by 1 each time at least one previously-unfired item is fired to
    /// the kitchen (see OrdersController.Fire). An item's FireBatch == this value means it
    /// was part of the most recent fire round (drives the "NEW" badge on KDS).</summary>
    public int CurrentFireBatch { get; set; }
    /// <summary>Set when a guest QR order's very first fire is submitted while
    /// CafeSettings.RequireStaffOrderConfirmation is on — items sit unfired (FireBatch == 0,
    /// invisible on KDS, same as before Place Order was ever tapped) until a staff member
    /// hits Confirm (OrdersController.ConfirmGuestOrder), which fires them for real. Never
    /// set for staff-created POS orders — only the guest self-order path checks this.</summary>
    public bool PendingStaffConfirmation { get; set; }
    public bool Paid { get; set; }
    public bool Refunded { get; set; }
    public decimal? RefundedAmount { get; set; }
    public string? RefundReason { get; set; }
    public DateTime? RefundedAt { get; set; }
    /// <summary>Whole-order cancel (see OrdersController.Cancel) — a distinct terminal
    /// state from Refunded, which only applies to an already-Paid order. Cancelling voids
    /// every not-yet-served line via the same reversal rules as a single-item void.</summary>
    public bool Cancelled { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
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
    /// <summary>One row per fire round (see OrderItem.FireBatch) — each round tracks its own
    /// kitchen progress independently, so a re-fire on an order already Preparing/Ready/Served
    /// shows as a fresh, separate ticket without disturbing the earlier round's progress.
    /// Order.Status is a computed rollup of these (see OrdersController.RecomputeOrderStatus),
    /// not set directly, except by the legacy manual-override SetStatus endpoint.</summary>
    public List<OrderFireBatch> FireBatches { get; set; } = [];
    /// <summary>One row per tender used to settle this bill — a single-method payment still
    /// gets exactly one row here, a split payment gets one row per method. Order.PaymentMethod
    /// stays a quick-glance summary ("Cash" or "Multiple"); this is the real breakdown, set
    /// once at Pay time and never touched again (see OrdersController.Pay).</summary>
    public List<OrderPayment> Payments { get; set; } = [];
}

/// <summary>A single tender applied toward settling a bill — see Order.Payments. Recorded
/// once, at Pay time; never edited or added to afterward (a correction means a refund + a
/// fresh Pay call, not mutating history).</summary>
public class OrderPayment : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int OrderId { get; set; }
    public required string Method { get; set; } // Cash / Card / UPI
    public decimal Amount { get; set; }
    /// <summary>This tender's 0-based slot in its order's payment ledger, assigned from
    /// however many rows already exist (a 3-way split in one Pay call takes slots n, n+1,
    /// n+2). Unique per order at the DB level — see CafePosDbContext — which is the backstop
    /// underneath Order.PaymentVersion's concurrency check: two racing Pay calls read the
    /// same ledger and compute the same next slot, so even if the token check were ever
    /// bypassed Postgres still refuses the duplicate tender outright.</summary>
    public int LedgerIndex { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class OrderItem : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int OrderId { get; set; }
    public int MenuItemId { get; set; }
    public required string Name { get; set; }
    public int Qty { get; set; }
    /// <summary>Effective per-unit price at the moment this line was added — already includes
    /// the selected Variant's price (instead of MenuItem.Price) and every SelectedModifiers
    /// delta, so every existing Subtotal/Tax/Total computation (Sum of Price*Qty) needs no
    /// changes. See OrderBuildingService.ResolveLinePricingAsync, the one place that computes it.</summary>
    public decimal Price { get; set; }
    /// <summary>Tax slab this line is billed at, snapshotted from the item's TaxGroup when the
    /// line was created (see <see cref="TaxGroup"/> for how it resolves). Null on lines placed
    /// before tax groups existed, and on any line whose item had no group and no default —
    /// RecomputeTotals bills those at CafeSettings.TaxRatePct, exactly as it always did.</summary>
    public decimal? TaxRatePct { get; set; }
    /// <summary>This line's share of the order's taxable value — gross (Price * Qty) minus its
    /// proportional slice of every order-level discount. Stored rather than derived because the
    /// bill has to show taxable value per rate, and the discount split can't be recovered from
    /// the line alone. Written by RecomputeTotals.</summary>
    public decimal TaxableAmount { get; set; }
    /// <summary>Tax charged on this line = TaxableAmount * rate. Order.Tax is the sum of these.
    /// Written by RecomputeTotals.</summary>
    public decimal TaxAmount { get; set; }
    public string? Modifier { get; set; }
    /// <summary>Which Variant (Half/Full/...) was picked, if any — null means the item's own
    /// base MenuItem.Price was used. VariantName is a snapshot (survives the variant being
    /// renamed/deleted later), same convention as Name snapshotting MenuItem.Name.</summary>
    public int? VariantId { get; set; }
    public string? VariantName { get; set; }
    /// <summary>Which kitchen station this line was prepped at, snapshotted from
    /// MenuItem.Station.Name at the moment the line was created — same snapshot convention
    /// as Name/VariantName, so a later station rename doesn't retroactively change an
    /// already-fired/printed KOT. Drives KDS station filtering and per-station KOT print
    /// routing (see OrderBuildingService.ResolveLinePricingAsync).</summary>
    public string StationName { get; set; } = "Kitchen";
    /// <summary>Veg/non-veg mark, snapshotted from MenuItem.VegNonVegType when the line was
    /// created — same convention as Name/StationName above, so re-tagging an item later can
    /// never change what an already-printed KOT said the kitchen was cooking. Null both for
    /// untagged items and for lines placed before this column existed; every renderer treats
    /// null as "no mark" rather than assuming veg.</summary>
    public VegNonVegType? VegNonVegType { get; set; }
    /// <summary>Toppings/add-ons picked for this line — each row snapshots the ModifierOption's
    /// name/price at order time (same reasoning as VariantName/Price above).</summary>
    public List<OrderItemModifier> SelectedModifiers { get; set; } = [];
    /// <summary>Which "fire round" this item was sent to the kitchen in. 0 = not yet fired
    /// (still freely editable/removable, invisible on KDS). >0 = the Order.CurrentFireBatch
    /// value at the moment it was fired — matches an OrderFireBatch.BatchNumber.</summary>
    public int FireBatch { get; set; }
    /// <summary>How many of this line's <see cref="Qty"/> units have reached each kitchen
    /// stage. The per-UNIT distribution is the real source of truth — a "Chowmein ×6" line
    /// can have 3 units Preparing and 3 still New (partial-quantity production). Units only
    /// move forward. NewQty is derived: Qty - (ReadQty + PreparingQty + ReadyQty + ServedQty).
    /// These are non-cumulative counts of units CURRENTLY at that stage; they always sum
    /// (with NewQty) to Qty.</summary>
    public int ReadQty { get; set; }
    public int PreparingQty { get; set; }
    public int ReadyQty { get; set; }
    public int ServedQty { get; set; }
    /// <summary>Derived overall stage for this line — the least-progressed stage that still
    /// has ≥1 unit (or Served once every unit is served). Maintained by RecomputeItemStatus
    /// whenever the unit counts change; the KOT/order status rollups read this. Never set
    /// directly.</summary>
    public OrderStatus Status { get; set; } = OrderStatus.New;
    /// <summary>Set by OrdersController.VoidItemAsync — either a same-KOT single-item void
    /// (RemoveItem, once fired) or part of a whole-order Cancel. A flag rather than a hard
    /// delete so KOT/fire-batch history and reporting survive. Voided lines are excluded
    /// from Subtotal/Total recomputation and from batch-status rollups.</summary>
    public bool Voided { get; set; }
    public DateTime? VoidedAt { get; set; }
    public string? VoidReason { get; set; }

    /// <summary>Units still at New (not yet acknowledged) — derived, not stored.</summary>
    [NotMapped]
    public int NewQty => Qty - (ReadQty + PreparingQty + ReadyQty + ServedQty);
}

/// <summary>One selected topping/add-on on an order line — snapshot of a ModifierOption at
/// order time (see OrderItem.SelectedModifiers). ModifierOptionId is a loose reference (not
/// a navigation) so the option can later be edited/deleted from the catalog without touching
/// order history, same convention as RecipeItem.InventoryItemId.</summary>
public class OrderItemModifier : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int OrderItemId { get; set; }
    public int ModifierOptionId { get; set; }
    public required string Name { get; set; }
    /// <summary>Snapshot of the option's price for ONE unit — the line contributes
    /// Price * Qty, so this stays comparable across rows regardless of Qty.</summary>
    public decimal Price { get; set; }
    /// <summary>How many of this option were picked (e.g. 2x Extra Cheese). Only a
    /// "Quantity"-type Modifier group can exceed 1; Radio/MultiSelect groups are capped
    /// at 1 by OrderBuildingService.ResolveLinePricingAsync.</summary>
    public int Qty { get; set; } = 1;
}

/// <summary>A single fire round (KOT) — see Order.FireBatches. Created when
/// OrderBuildingService.FireUnfiredItemsAsync fires a new batch. Its Id doubles as the KOT
/// number (see FireBatchDto), and FiredAt drives the KDS timer.</summary>
public class OrderFireBatch : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int OrderId { get; set; }
    /// <summary>Matches the OrderItem.FireBatch value of the items fired in this round —
    /// and the Order.CurrentFireBatch value at the moment this round was fired.</summary>
    public int BatchNumber { get; set; }
    /// <summary>Rollup of this batch's items' statuses (least-progressed active item, or
    /// Served once all are) — computed by RecomputeBatchStatus, never set directly. Drives
    /// which KDS status-tab the ticket appears under.</summary>
    public OrderStatus Status { get; set; } = OrderStatus.New;
    public DateTime FiredAt { get; set; } = DateTime.UtcNow;
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
    /// <summary>Set once a low-stock AppNotification has fired for this item (Current
    /// crossed at-or-below ReorderLevel) so repeat sales while it's still low don't spam a
    /// new alert on every order — see InventoryBatchService.ConsumeFifoAsync/CreateBatch.
    /// Cleared automatically once restocked back above ReorderLevel.</summary>
    public bool LowStockNotified { get; set; }
    /// <summary>Deactivated (IsActive=false) rather than deleted once referenced by a Recipe,
    /// InventoryTransaction, or InventoryBatch — hard-deleting would leave those pointing at a
    /// dead FK (a recipe silently stops deducting that ingredient with no alert, and the
    /// item's own ledger history becomes unreachable). Same convention as Vendor/Reward.
    /// List()/LowStock() hide it by default via includeInactive.</summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>A named tax rate (e.g. "GST 5%", "GST 12%") that menu items are assigned to,
/// so a cafe can bill different slabs on one order instead of one flat rate for everything.
///
/// A line's rate resolves in this order: the item's own group → the group flagged
/// <see cref="IsDefault"/> → <see cref="CafeSettings.TaxRatePct"/> (the pre-tax-group
/// behaviour, kept so a cafe that never creates a group bills exactly as it did before).
/// The resolved rate is SNAPSHOTTED onto OrderItem.TaxRatePct at order time — editing or
/// deleting a group later never re-prices an already-placed bill.</summary>
public class TaxGroup : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>Label as it appears on the bill's tax breakdown, e.g. "GST 5%".</summary>
    public required string Name { get; set; }
    public decimal RatePct { get; set; }
    /// <summary>Applied to any menu item with no group of its own. At most one per tenant —
    /// TaxGroupsController clears the flag on the others when a new default is set.</summary>
    public bool IsDefault { get; set; }
}

public class CafeSettings : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    /// <summary>Fallback rate for items with no TaxGroup and when no default group exists —
    /// see <see cref="TaxGroup"/> for the full resolution order.</summary>
    public decimal TaxRatePct { get; set; } = 0;

    // Language & Region
    public string Currency { get; set; } = "INR (₹)";
    public string Region { get; set; } = "Asia/Kolkata";

    /// <summary>Which onboarding style card the Owner picked (see OnboardingTypeScreen) —
    /// "coffee" (QSR)/"bakery"/"restaurant"/"lounge". Stores the stable id, not the display
    /// label, since the label text can change independently (e.g. "Coffee Shop" -> "QSR")
    /// without needing to touch stored data. Not read by anything yet — captured now so a
    /// future feature (starter-menu suggestions, receipt wording, analytics segmentation,
    /// ...) has it available without a fresh onboarding-time prompt.</summary>
    public string? BusinessType { get; set; }

    // Receipt Customization / Branding
    public string BusinessName { get; set; } = "PrabandhOS";
    public string ReceiptHeader { get; set; } = "Welcome to PrabandhOS\n123 Coffee Street";
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

    // Notification Preferences — each gates one real AppNotification category end-to-end
    // (see NotificationPreferences.NamedGates): turning one off means that category's
    // AppNotification is silently dropped before it's ever saved or pushed, not just hidden
    // client-side. Every category NOT listed with its own bool here is still gate-able — see
    // NotificationCategoryOverridesJson below and NotificationPreferences' own doc comment.
    public bool InventoryAlertsEnabled { get; set; } = true;
    public bool ShiftReportsEnabled { get; set; } = true;
    /// <summary>Kitchen "new order"/"items fired" alert — see OrderBuildingService.FireUnfiredItemsAsync.</summary>
    public bool OrderPlacedAlertsEnabled { get; set; } = true;
    /// <summary>Only fires anything while RequireStaffOrderConfirmation is also on — see
    /// OrderBuildingService.MarkPendingConfirmation.</summary>
    public bool OrderPendingConfirmationAlertsEnabled { get; set; } = true;
    /// <summary>"Order ready to serve" alert — see OrderBuildingService.RecomputeBatchStatus.</summary>
    public bool OrderReadyAlertsEnabled { get; set; } = true;
    /// <summary>"Approval needed" / granted / rejected notices — see ApprovalsController. Unlike
    /// the alerts above these are role-scoped rather than tenant-wide (only whoever may actually
    /// resolve the request is told), so turning this off silences an approval queue nobody else
    /// was seeing anyway.</summary>
    public bool ApprovalAlertsEnabled { get; set; } = true;
    /// <summary>Gates the WhatsApp order-tracking module's automatic status-update/bill-PDF
    /// sends for this tenant — see CafePosDbContext.SaveChangesAsync's WhatsApp event hook.
    /// Independent of whether a WhatsAppSession is actually Connected (that's a separate,
    /// harder gate); this is just the same "Owner can turn a notification category off"
    /// pattern as the alerts above, applied to the WhatsApp channel.</summary>
    public bool WhatsAppOrderUpdatesEnabled { get; set; } = true;
    /// <summary>Per-tenant on/off overrides for every NotificationCategory that has NO
    /// dedicated bool above — a JSON dict of category name -> enabled, written/read only
    /// through NotificationPreferences (see its doc comment for why this exists instead of
    /// one more named column per category). Null/empty = every generic category still at its
    /// default (enabled). Never read or written directly outside NotificationPreferences.</summary>
    public string? NotificationCategoryOverridesJson { get; set; }

    // QR Ordering
    /// <summary>When on, a guest QR session's first fire (Place Order) doesn't reach the
    /// kitchen until a staff member confirms it (see Order.PendingStaffConfirmation) — stops
    /// prank/misdialed-table orders from hitting KDS. Later re-orders on an already-confirmed
    /// session skip this and fire immediately. Defaults on: the safer choice for a cafe that
    /// hasn't consciously decided it wants instant-fire self-ordering.</summary>
    public bool RequireStaffOrderConfirmation { get; set; } = true;

    public bool HasCompletedOnboarding { get; set; }

    /// <summary>"TWO_STAGE" (New → Ready, Preparing skipped) or "THREE_STAGE" (New →
    /// Preparing → Ready, the default) — lets an Owner pick how many taps the kitchen
    /// needs per item/KOT on the KDS screen. Purely a UI/tap-behavior setting; the
    /// underlying per-unit stage tracking (NewQty/PreparingQty/ReadyQty/ServedQty) is
    /// unchanged either way.</summary>
    public string KdsStageMode { get; set; } = "THREE_STAGE";

    // Which order types the POS offers — an Owner turns off whichever this cafe doesn't
    // do (e.g. a counter-only QSR place hides Dine In/Delivery entirely). At least one
    // must stay enabled; enforced in SettingsController, not here.
    public bool DineInEnabled { get; set; } = true;
    public bool TakeawayEnabled { get; set; } = true;
    public bool DeliveryEnabled { get; set; } = true;
    public bool QsrEnabled { get; set; } = true;
    public bool CashEnabled { get; set; } = true;

    // Auto Charges — lets an Owner define Service/Packing/Delivery charge once instead of
    // a biller re-entering it on every bill. Each charge has a default value (null = the
    // charge is off entirely, matching how ServiceChargeDefaultPct/PackingChargeDefaultAmount/
    // DeliveryChargeDefaultAmount already read as "no default configured") plus which order
    // types it auto-applies to on order creation (OrderBuildingService.BuildOrderAsync) —
    // same on/off-per-order-type shape as DineInEnabled/TakeawayEnabled/DeliveryEnabled above,
    // just scoped to one charge instead of the whole order type. A biller can still remove or
    // change an auto-applied charge per bill via OrderBillActions' existing removable tiles —
    // this only sets the starting value, it doesn't lock it.
    public decimal? ServiceChargeDefaultPct { get; set; }
    public bool ServiceChargeAutoApplyDineIn { get; set; } = true;
    public bool ServiceChargeAutoApplyTakeaway { get; set; }
    public bool ServiceChargeAutoApplyDelivery { get; set; }
    public bool ServiceChargeAutoApplyToken { get; set; }

    public decimal? PackingChargeDefaultAmount { get; set; }
    public bool PackingChargeAutoApplyDineIn { get; set; }
    public bool PackingChargeAutoApplyTakeaway { get; set; } = true;
    public bool PackingChargeAutoApplyDelivery { get; set; } = true;
    public bool PackingChargeAutoApplyToken { get; set; }

    public decimal? DeliveryChargeDefaultAmount { get; set; }
    public bool DeliveryChargeAutoApplyDineIn { get; set; }
    public bool DeliveryChargeAutoApplyTakeaway { get; set; }
    public bool DeliveryChargeAutoApplyDelivery { get; set; } = true;
    public bool DeliveryChargeAutoApplyToken { get; set; }

    // Borzo courier integration — books a real rider for DELIVERY orders (see BorzoClient).
    // Per-cafe rather than per-deployment: each tenant has its own Borzo account, its own
    // balance, and its own pickup point.
    public bool BorzoEnabled { get; set; }
    /// <summary>
    /// The cafe's own X-DV-Auth-Token. Anyone holding this can book rides on the cafe's account
    /// and spend its balance, so it must never leave the server.
    ///
    /// [JsonIgnore] is load-bearing, not tidiness: SettingsController.Get is [AllowAnonymous]
    /// (the app needs branding before login, and the QR menu is customer-facing) and returns
    /// this whole entity, so any property added here is world-readable by default. This is the
    /// first actual secret on CafeSettings — every other JsonIgnore in this file is just a
    /// navigation property. DeliveryController reads it straight off the entity server-side;
    /// BorzoSettingsDto reports only whether one is saved.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? BorzoAuthToken { get; set; }
    /// <summary>Points at Borzo's sandbox instead of production. Defaults on so a cafe that
    /// pastes a token before it has meant to go live can't accidentally summon a real rider;
    /// sandbox prices are NOT real prices, which is the reason the UI has to say which one
    /// it's on.</summary>
    public bool BorzoUseTestEnvironment { get; set; } = true;
    /// <summary>Whether what the courier charges is added to the customer's bill. Off means the
    /// cafe absorbs it — the order still books, the customer just never sees the fee.</summary>
    public bool BorzoPassFeeToCustomer { get; set; } = true;
    /// <summary>Where the rider collects from. Falls back to Address above when blank, but the
    /// coordinates have no fallback — without them nothing can be booked, which is why the
    /// settings screen makes the cafe pin its location once.</summary>
    public string? PickupAddress { get; set; }
    public decimal? PickupLatitude { get; set; }
    public decimal? PickupLongitude { get; set; }

    // Receipt Builder — which optional sections print on the customer bill (see
    // receiptFormat.ts buildReceiptLines, the one shared line-model every print
    // transport/screen renders from). Business name, items, and totals always print —
    // there's no real receipt without them, so those aren't toggleable.
    public bool ReceiptShowAddress { get; set; } = true;
    public bool ReceiptShowWaiterName { get; set; } = true;
    public bool ReceiptShowGuestPhone { get; set; } = true;
    public bool ReceiptShowItemNotes { get; set; } = true;
    public bool ReceiptShowFooter { get; set; } = true;
    /// <summary>GST/tax registration number line — blank means the cafe hasn't set one,
    /// same as ReceiptShowGstNumber being off either way (no number to show).</summary>
    public string? GstNumber { get; set; }

    /// <summary>The cafe's own UPI payment address ("cafe@okaxis") — what the pay-by-UPI QR
    /// on a bill is addressed to. Null/blank means the cafe hasn't set one up, and every
    /// UPI QR surface (printed bill, the bill screen's Pay by UPI button) simply doesn't
    /// appear — see the frontend's buildUpiPaymentUri.
    ///
    /// Deliberately safe to serve from this controller's [AllowAnonymous] GET: a VPA is a
    /// receive-only address meant to be shown to whoever is paying (it goes on the printed
    /// bill and on a QR sticker at the counter), not a credential. Knowing it lets someone
    /// send money to this cafe, nothing else.</summary>
    public string? UpiVpa { get; set; }

    // Attendance tunables — used by AttendanceController to derive Late/HalfDay/
    // Overtime status from raw punch times.
    public int LateGraceMinutes { get; set; } = 10;
    public int HalfDayThresholdHours { get; set; } = 4;
    /// <summary>Used only when a day has no Shift scheduled, so overtime/half-day can
    /// still be computed against something.</summary>
    public int StandardShiftHours { get; set; } = 8;

    /// <summary>The cafe's own registered coordinates, captured once by an Owner/Manager
    /// tapping "Use Current Location" in Cafe Profile while standing on-site. Null until
    /// set — AttendanceController skips the punch geofence check entirely when this is
    /// null rather than blocking every punch before an Owner has configured it.</summary>
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }

    /// <summary>
    /// The owning Tenant's Slug, populated by SettingsController (not a real column —
    /// see [NotMapped]). Lets the app build tenant-aware QR ordering links
    /// (/order/{slug}/{tableCode}) without a separate endpoint.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string? TenantSlug { get; set; }
}
