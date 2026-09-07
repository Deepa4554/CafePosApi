using System.Security.Claims;
using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Order pricing/creation/firing core — extracted out of OrdersController so it has a
/// third caller (GuestSessionController's cart/order endpoints) without duplicating
/// pricing, inventory, or CRM behaviour a second time. Originally this logic lived only
/// in OrdersController.Create/CreatePublic; the extraction is purely mechanical — same
/// behaviour, just callable from more than one controller.
/// </summary>
/// <summary>The handful of Modifier-group fields line pricing actually needs — option
/// ownership, the per-type selection cap, and required-group enforcement. A named type rather
/// than the anonymous one this used to project into, so a whole order's groups can be loaded
/// once and handed back per line (see LoadModifierGroupsAsync).</summary>
public record ModifierGroupInfo(int Id, string Name, string Type, bool IsRequired);

/// <summary>The tax slabs a line can bill at. A cafe has a handful of these in total, so the
/// whole set is loaded once per order and matched in memory — see LoadTaxGroupsAsync.</summary>
public record TaxGroupInfo(int Id, decimal RatePct, bool IsDefault);

public interface IOrderBuildingService
{
    Task<Order> BuildOrderAsync(
        CafePosDbContext db, string orderType, string? tableCode, string? guestName, List<CreateOrderItemDto> items,
        decimal discountPct, ClaimsPrincipal? user, int? explicitTenantId = null, int? branchId = null,
        string? guestPhone = null, int? servedByStaffId = null, string? guestAddress = null, decimal flatDiscountAmount = 0);

    /// <summary>Adds a new unfired cart line or overwrites an existing unfired line's qty for
    /// the same (menuItemId, modifier) pair — qty is the line's FINAL quantity, not a delta.
    /// qty == 0 removes the line. Never touches inventory: cart lines stay FireBatch == 0
    /// and stock is only consumed when they're actually fired (see FireUnfiredItemsAsync,
    /// the single deduction point). Recomputes order.Subtotal/Tax/Total. Does not save —
    /// the caller saves.</summary>
    Task<OrderItem?> AddOrUpdateCartItemAsync(CafePosDbContext db, Order order, int menuItemId, int qty, string? modifier, int explicitTenantId,
        int? variantId = null, List<int>? modifierOptionIds = null);

    /// <summary>Prices one order line: the selected Variant's price (or the item's own base
    /// price if none), plus every selected ModifierOption's price delta — both validated as
    /// actually belonging to this menu item. Also snapshots the item's kitchen station name
    /// (see OrderItem.StationName) — the caller must have loaded menuItem with
    /// .Include(m => m.Station) for this to resolve to anything but the "Kitchen" fallback.
    /// Also resolves the line's tax slab (item's TaxGroup → tenant default group → null,
    /// meaning "bill at CafeSettings.TaxRatePct") for RecomputeTotals to snapshot.
    ///
    /// <paramref name="openPrice"/> is the rate the biller typed for an MRP item (see
    /// MenuItem.IsOpenPrice) — required for such an item, and ignored for every other one so
    /// pricing stays server-authoritative. The returned PriceIncludesTax rides along with it:
    /// an MRP is a ceiling, so that line's tax is carved out of the rate rather than added on.
    ///
    /// Throws ApiValidationException on an unknown/unavailable variant, an option that
    /// belongs to a different item, or a missing/non-positive rate on an MRP item. Shared by
    /// BuildOrderAsync, AddOrUpdateCartItemAsync, and OrdersController.AddItem so the three
    /// order-item-creation paths can never compute this differently.</summary>
    /// <paramref name="defaultHsnCode"/> is the cafe's CafeSettings.DefaultHsnCode, used for an
    /// item with no HSN of its own. Passed in rather than looked up here so resolving it costs
    /// one query per ORDER instead of one per line, while the resolution itself stays in this
    /// single place. Callers that genuinely don't have settings to hand pass null, which just
    /// means the line snapshots the item's own code or nothing.
    Task<(decimal Price, string? VariantName, List<OrderItemModifier> Modifiers, string StationName, decimal? TaxRatePct, bool PriceIncludesTax, string? HsnCode)> ResolveLinePricingAsync(
        CafePosDbContext db, MenuItem menuItem, int? variantId, List<int>? modifierOptionIds, int? explicitTenantId,
        decimal? openPrice = null, string? defaultHsnCode = null, bool tenantPricesIncludeTax = false,
        IReadOnlyDictionary<int, List<ModifierGroupInfo>>? preloadedGroups = null,
        IReadOnlyList<TaxGroupInfo>? preloadedTaxGroups = null);

    /// <summary>Every modifier group of MANY menu items in one query, keyed by menu item — hand
    /// the whole dictionary to ResolveLinePricingAsync as <c>preloadedGroups</c> and it stops
    /// querying per line. Pricing a line needs its item's groups whether or not the line has any
    /// add-ons at all (required groups have to be enforced against an EMPTY selection too), so
    /// the old per-line query fired for every single line on every order — the one part of
    /// creating an order that still scaled with cart size.</summary>
    Task<Dictionary<int, List<ModifierGroupInfo>>> LoadModifierGroupsAsync(
        CafePosDbContext db, IEnumerable<int> menuItemIds, int? explicitTenantId);

    /// <summary>This cafe's whole tax-slab table in one query — hand it to
    /// ResolveLinePricingAsync as <c>preloadedTaxGroups</c>. Same story as the modifier groups
    /// beside it: resolving a line's slab needs the item's own group OR the tenant default, so
    /// the query ran for every line whether or not the item had a slab of its own.</summary>
    Task<List<TaxGroupInfo>> LoadTaxGroupsAsync(CafePosDbContext db, int? explicitTenantId);

    /// <summary>Assigns the next fire-batch number to every not-yet-fired item, creates that
    /// batch's own kitchen-ticket row, notifies the kitchen about just those items, and
    /// deducts their ingredients — this is THE single point inventory is consumed at (see
    /// doc Section 4.1: deduct when the kitchen actually starts cooking, not at order
    /// creation). Returns false if there was nothing new to fire. Recomputes Order.Status
    /// but does not save — the caller saves.</summary>
    Task<bool> FireUnfiredItemsAsync(CafePosDbContext db, Order order, int? explicitTenantId);

    /// <summary>Re-prices the tenant's active Offers against the order's current cart and stamps
    /// the result onto each line (OrderItem.OfferDiscountAmount) plus the order
    /// (OfferDiscountAmount / AppliedOfferTitle). Call it before RecomputeTotals at every point
    /// the cart changes — offers are a pure function of the lines, so anywhere else re-running it
    /// is a no-op, but a cart edit that skips it leaves a stale discount. Does not save.</summary>
    Task ApplyOffersAsync(CafePosDbContext db, Order order, int? explicitTenantId);

    /// <summary>Staff-Confirm Mode: flags the order as awaiting a staff member's OK instead of
    /// firing straight to the kitchen (see Order.PendingStaffConfirmation) and notifies the
    /// floor (not kitchen — see NotificationCategory.OrderPendingConfirmation). Items stay
    /// unfired (FireBatch == 0) exactly as they were before Place Order was tapped; the actual
    /// fire happens later via FireUnfiredItemsAsync once OrdersController.ConfirmGuestOrder
    /// runs. No-ops (returns without adding a second notification) if already pending. Does
    /// not save — the caller saves.</summary>
    void MarkPendingConfirmation(CafePosDbContext db, Order order, int? explicitTenantId);

    void RecomputeBatchStatus(CafePosDbContext db, Order order, int batchNumber);

    Task<decimal> GetTaxRatePctAsync(CafePosDbContext db, int tenantId);

    /// <summary>Deducts real ingredient stock for the given line items (see the private
    /// implementation for the Prepared-vs-Independent deduction rule) — exposed so
    /// OrdersController.AddItem (which keeps its own "always a new line" semantics,
    /// distinct from the guest cart's upsert-by-line behaviour) can still share the
    /// deduction logic instead of duplicating it.</summary>
    Task ConsumeInventoryAsync(CafePosDbContext db, Dictionary<int, MenuItem> menu, List<OrderItem> items, int orderId,
        int? explicitTenantId = null, string? reason = null, bool skipAlreadyDeductedCheck = false);

    /// <summary>Deducts the ingredients for <paramref name="extraQty"/> ADDITIONAL units of a line
    /// that has already been fired (and so already had its original units deducted) — what a
    /// quantity increase needs, see OrdersController.UpdateItemQty.
    ///
    /// Distinct from a plain re-run of <see cref="ConsumeInventoryAsync"/> in two ways, both
    /// deliberate: it must NOT be skipped by the already-deducted guard (this line having been
    /// deducted once is the normal case here, not a retry), and its ledger rows are tagged with
    /// <see cref="OrderBuildingService.TopUpDeductionReason"/> so they fall outside the fire-time
    /// idempotency index rather than colliding with the line's original draw (see
    /// CafePosDbContext). Does not save — the caller does.</summary>
    Task ConsumeInventoryForAddedUnitsAsync(CafePosDbContext db, OrderItem line, int extraQty, int orderId);

    /// <summary>Resolves the CRM customer for a guest — by phone first, then by name, creating
    /// one if neither matches. Exposed so OrdersController.UpdateGuest can re-link an order
    /// whose phone arrives after creation using exactly the same matching rules the order was
    /// built with, rather than a second, subtly-different lookup. Does not save.</summary>
    Task<Customer> FindOrCreateCustomerAsync(CafePosDbContext db, string guestName, string? guestPhone, int? explicitTenantId = null, string? guestAddress = null);
}

public class OrderBuildingService(ITaxRateCache taxRateCache, ITenantContext tenantContext, ILogger<OrderBuildingService> logger) : IOrderBuildingService
{
    /// <summary>Restricts a DbSet to one specific tenant, bypassing the ambient JWT-derived
    /// filter — used by anonymous/guest flows, which have no JWT and so must be told the
    /// tenant explicitly. Returns the DbSet unchanged (normal ambient-filtered behaviour)
    /// when explicitTenantId is null (the staff POS path).</summary>
    private static IQueryable<T> TenantScoped<T>(DbSet<T> set, int? explicitTenantId) where T : class, ITenantScoped =>
        explicitTenantId is int tid ? set.IgnoreQueryFilters().Where(e => e.TenantId == tid) : set;

    public async Task<decimal> GetTaxRatePctAsync(CafePosDbContext db, int tenantId) =>
        await taxRateCache.GetTaxRatePctAsync(tenantId, async () => (await db.Settings.IgnoreQueryFilters().FirstAsync(s => s.TenantId == tenantId)).TaxRatePct);

    // The whole build runs in one transaction so the dine-in table claim below can hold a
    // lock from the "is this table free?" check all the way through to the order actually
    // existing. Two waiters tapping New Order on the same table within milliseconds used to
    // both pass the check and both succeed — the second order became a ghost nobody billed.
    public Task<Order> BuildOrderAsync(
        CafePosDbContext db, string orderType, string? tableCode, string? guestName, List<CreateOrderItemDto> items,
        decimal discountPct, ClaimsPrincipal? user, int? explicitTenantId = null, int? branchId = null,
        string? guestPhone = null, int? servedByStaffId = null, string? guestAddress = null, decimal flatDiscountAmount = 0) =>
        DbConcurrency.InTransactionAsync(db, async () =>
    {
        if (items.Count == 0)
            throw new ApiValidationException("Order must contain at least one item.");

        if (orderType == "DINE_IN")
        {
            if (string.IsNullOrWhiteSpace(tableCode))
                throw new ApiValidationException("Dine-in orders need a tableCode.");

            var tableId = await TenantScoped(db.Tables, explicitTenantId)
                .Where(t => t.Code == tableCode)
                .Select(t => (int?)t.Id)
                .FirstOrDefaultAsync();
            if (tableId is null)
                throw new ApiValidationException($"Table {tableCode} does not exist.");

            // The table row itself is the claim token — whoever holds its lock owns the
            // right to open an order on it until they commit. The loser then re-runs the
            // busy check against the winner's committed order and gets the conflict, which
            // is what the guest-QR path has always done via GuestSession's unique index.
            await DbConcurrency.LockRowsAsync<CafeTable>(db, tableId.Value);

            var busy = await TenantScoped(db.Orders, explicitTenantId)
                .AnyAsync(o => o.TableCode == tableCode && !o.Cancelled && (!o.Paid || o.Status != OrderStatus.Served));
            if (busy)
                throw new ApiConflictException($"Table {tableCode} already has an open order.");
        }

        var menuIds = items.Select(i => i.MenuItemId).ToList();
        var menu = await TenantScoped(db.MenuItems, explicitTenantId).Include(m => m.Station)
            .Where(m => menuIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id);

        // Loaded before the line loop (rather than at its old spot beside ComputeDefaultCharges
        // below) because the loop now needs DefaultHsnCode — one fetch for the order instead of
        // one per line. Same row either way; nothing between here and there writes to it.
        var settings = await TenantScoped(db.Settings, explicitTenantId).FirstAsync();

        // Same "once per order, not once per line" treatment as the menu and settings lookups
        // above — see LoadModifierGroupsAsync.
        var modifierGroups = await LoadModifierGroupsAsync(db, menuIds, explicitTenantId);
        var taxGroupSlabs = await LoadTaxGroupsAsync(db, explicitTenantId);

        var orderItems = new List<OrderItem>();
        foreach (var line in items)
        {
            if (!menu.TryGetValue(line.MenuItemId, out var menuItem))
                throw new ApiValidationException($"Menu item {line.MenuItemId} not found.");
            if (!menuItem.Available)
                throw new ApiValidationException($"{menuItem.Name} is currently unavailable.");
            if (line.Qty <= 0)
                throw new ApiValidationException($"Invalid quantity for {menuItem.Name}.");

            var (linePrice, variantName, selections, stationName, lineTaxRatePct, linePriceIncludesTax, lineHsnCode) =
                await ResolveLinePricingAsync(db, menuItem, line.VariantId, line.ModifierOptionIds, explicitTenantId, line.OpenPrice, settings.DefaultHsnCode, settings.MenuPricesIncludeTax, modifierGroups, taxGroupSlabs);
            var orderItem = new OrderItem
            {
                MenuItemId = menuItem.Id,
                Name = menuItem.Name,
                Qty = line.Qty,
                Price = linePrice,
                Modifier = line.Modifier,
                VariantId = line.VariantId,
                VariantName = variantName,
                SelectedModifiers = selections,
                StationName = stationName,
                VegNonVegType = menuItem.VegNonVegType,
                Subtitle = string.IsNullOrWhiteSpace(menuItem.Subtitle) ? null : menuItem.Subtitle,
                TaxRatePct = lineTaxRatePct,
                PriceIncludesTax = linePriceIncludesTax,
                HsnCode = lineHsnCode,
            };
            // Anonymous guest requests have no JWT, so StampTenantIds would fall back to the
            // default tenant — and the ambient query filter would then hide these lines from
            // the cafe's own staff (orders showing "0 items", Confirm always failing).
            if (explicitTenantId is int lineTid) orderItem.TenantId = lineTid;
            orderItems.Add(orderItem);
        }

        var effectiveTenantId = explicitTenantId ?? tenantContext.TenantIdOrDefault;
        var taxRatePct = await GetTaxRatePctAsync(db, effectiveTenantId);
        var subtotal = orderItems.Sum(i => i.Price * i.Qty);
        // A flat rupee discount is stored exactly as typed (clamped to the bill) and wins over a
        // percentage — the whole point of the flat option is that "₹50 off" stays ₹50, so it must
        // NOT be turned back into a percentage. A percentage discount keeps its old behaviour:
        // pct is snapshotted and the amount derived from this subtotal once, at creation.
        decimal clampedDiscountPct;
        decimal discountAmount;
        if (flatDiscountAmount > 0)
        {
            clampedDiscountPct = 0;
            discountAmount = Math.Min(flatDiscountAmount, subtotal);
        }
        else
        {
            clampedDiscountPct = Math.Clamp(discountPct, 0, 100);
            discountAmount = Math.Round(subtotal * clampedDiscountPct / 100, 2);
        }

        var (defaultServiceCharge, defaultPackingCharge, defaultDeliveryCharge) = ComputeDefaultCharges(settings, orderType, subtotal);

        // QSR counter orders get a daily-resetting token instead of a table. The UPSERT is
        // atomic on its own; running inside this method's transaction additionally means a
        // build that fails later gives the number back instead of leaving a gap in the
        // day's tokens.
        int? tokenNumber = null;
        DateOnly? tokenDate = null;
        if (orderType == "QSR")
        {
            // The cafe's calendar day, not UTC's (see IstClock) — TokenDate is what scopes the
            // daily reset, and a UTC date rolls over at 05:30 IST. A counter open across that
            // moment used to carry yesterday's sequence until 5:30 AM and then restart at #1
            // mid-service, handing out the same token number twice in one morning.
            tokenDate = DateOnly.FromDateTime(IstClock.NowIst);
            tokenNumber = await NextTokenNumberAsync(db, effectiveTenantId, tokenDate.Value);
        }

        int? createdByUserId = null;
        string? createdByName = null;
        StaffMember? servedBy = null;
        if (explicitTenantId is null && user is not null)
        {
            var idClaim = user.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (idClaim is not null && int.TryParse(idClaim, out var currentUserId))
            {
                var currentUser = await db.Users.FindAsync(currentUserId);
                createdByUserId = currentUser?.Id;
                createdByName = currentUser?.Name;
            }

            servedBy = servedByStaffId is int sid
                ? await db.Staff.FirstOrDefaultAsync(s => s.Id == sid)
                : await db.Staff.FirstOrDefaultAsync(s => s.UserId == createdByUserId);
            if (servedByStaffId is int explicitSid && servedBy is null)
                throw new ApiValidationException("Selected waiter not found.");
        }

        var guest = string.IsNullOrWhiteSpace(guestName) ? null : guestName.Trim();
        var guestSuffix = guest is null ? "" : $" – {guest}";
        var typeLabel = orderType switch
        {
            "TAKEAWAY" => "Takeaway",
            "DELIVERY" => "Delivery",
            "QSR" => "Token",
            "CASH" => "Cash Sale",
            _ => "Dine In",
        };
        var title = orderType == "DINE_IN"
            ? $"Table #{tableCode}{guestSuffix}"
            : orderType == "QSR"
                ? $"Token #{tokenNumber}"
                : $"{typeLabel} – {guest ?? "Walk-in"}";

        var order = new Order
        {
            BranchId = branchId,
            Title = title,
            OrderType = orderType,
            TableCode = orderType == "DINE_IN" ? tableCode : null,
            TokenNumber = tokenNumber,
            TokenDate = tokenDate,
            GuestName = guest,
            GuestPhone = string.IsNullOrWhiteSpace(guestPhone) ? null : guestPhone.Trim(),
            Items = orderItems,
            Subtotal = subtotal,
            DiscountPct = clampedDiscountPct,
            DiscountAmount = discountAmount,
            CreatedByUserId = createdByUserId,
            CreatedByName = createdByName,
            ServedByStaffId = servedBy?.Id,
            ServedByName = servedBy?.Name,
            ServiceChargeAmount = defaultServiceCharge,
            PackingChargeAmount = defaultPackingCharge,
            DeliveryChargeAmount = defaultDeliveryCharge,
            // Snapshotted at creation, exactly like each line's own slab: the cafe turning the
            // setting on tomorrow must not add tax to a bill that is open on a table today, and
            // turning it off must not strip tax from one already settled. Null (the setting off)
            // is the pre-existing behaviour — charges added on top of tax, untaxed.
            ChargesTaxRatePct = settings.TaxChargesEnabled ? taxRatePct : null,
        };
        await ApplyOffersAsync(db, order, explicitTenantId);
        RecomputeTotals(order, taxRatePct);
        if (explicitTenantId is int tid2) order.TenantId = tid2;
        db.Orders.Add(order);

        // No phone means no reliable identity to attach — matching by name alone would land
        // this visit on a stranger who happens to share it (or, for an unnamed walk-in, on the
        // one shared "Walk-in Guest" bucket every other nameless order piles onto too). Leave
        // order.CustomerId null instead; a phone added later (UpdateGuest, or the khata path in
        // Pay) links it to a real Customer via AttachCustomerAsync, same as it always has.
        if (!string.IsNullOrWhiteSpace(guestPhone))
        {
            var customer = await FindOrCreateCustomerAsync(db, guest ?? "Walk-in Guest", guestPhone, explicitTenantId, guestAddress);
            order.Customer = customer;
            // IgnoreQueryFilters + explicit TenantId, mirroring GetTaxRatePctAsync above: a guest
            // order arrives with no tenant on the ambient context, so the filtered set is empty.
            var loyaltyEarnPct = (await db.Settings.IgnoreQueryFilters()
                .FirstAsync(s => s.TenantId == effectiveTenantId)).LoyaltyEarnPct;
            RecordVisit(customer, order.Total, loyaltyEarnPct);
            TrackFavorites(db, customer, orderItems, explicitTenantId);
        }

        // No inventory deduction here — orders are created "Open"/unfired (see doc Section
        // 4.1). Stock is only consumed once FireUnfiredItemsAsync actually sends items to
        // the kitchen, so a held or abandoned order never leaves a phantom deduction.

        // The cafe's own running bill number — what actually prints on the receipt, instead of
        // the shared Orders identity sequence that made a new cafe's first bill read "#1455".
        //
        // Taken here, as late as the build allows, and deliberately NOT up beside the QSR token:
        // the UPSERT row-locks this cafe's single counter row until the surrounding transaction
        // commits, so every concurrent order for that cafe queues behind whoever holds it.
        // Allocating at the top would stretch that queue across the offer engine, the customer
        // lookup and the staff lookups as well; here it spans only the save. (Unlike the
        // dine-in table claim above, this lock is per-CAFE, not per-table — nothing else in the
        // build serialises orders this broadly, which is why the window is worth keeping short.)
        order.BillNumber = await NextBillNumberAsync(db, effectiveTenantId);

        // Two saves because the audit entry needs the order's generated id; the surrounding
        // transaction (DbConcurrency.InTransactionAsync at the top of this method) is what
        // keeps them atomic, exactly as the hand-rolled one here used to.
        await db.SaveChangesAsync();
        if (discountAmount > 0)
        {
            // A flat discount audits by its rupee value; a percentage one still names the rate.
            var discountDetail = clampedDiscountPct > 0
                ? $"Order {order.Id} applied {clampedDiscountPct}% discount (−{discountAmount:C})."
                : $"Order {order.Id} applied a flat {discountAmount:C} discount.";
            var discountEntry = new AuditLogEntry
            {
                Action = AuditAction.Discount,
                Resource = AuditResource.Order,
                ResourceId = order.Id.ToString(),
                Details = discountDetail,
                Severity = AuditSeverity.Medium,
            };
            if (explicitTenantId is int auditTid) discountEntry.TenantId = auditTid;
            db.AuditLog.Add(discountEntry);
            await db.SaveChangesAsync();
        }

        return order;
    });

    /// <summary>Atomically hands out the next QSR token number for (tenantId, date) — a plain
    /// SELECT-then-UPDATE would race under concurrent order creation (two counter orders
    /// rung up at the same moment could get the same number), so this is a single UPSERT:
    /// insert a fresh counter row at 1, or bump the existing one, in one round-trip.</summary>
    private static async Task<int> NextTokenNumberAsync(CafePosDbContext db, int tenantId, DateOnly date)
    {
        // The UPSERT below is Postgres-only syntax (ON CONFLICT ... RETURNING), which
        // Database.SqlQuery rejects outright against a non-relational provider — e.g. the
        // in-memory database this API falls back to for local dev when no connection string
        // is configured (see Program.cs). Non-relational only ever runs single-process, so
        // the race the UPSERT guards against can't happen there — a plain read-modify-write
        // is safe.
        if (!db.Database.IsRelational())
        {
            var counter = await db.TokenCounters.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Date == date);
            if (counter is null)
            {
                counter = new TokenCounter { TenantId = tenantId, Date = date, LastNumber = 1 };
                db.TokenCounters.Add(counter);
            }
            else
            {
                counter.LastNumber += 1;
            }
            await db.SaveChangesAsync();
            return counter.LastNumber;
        }

        var result = await db.Database.SqlQuery<int>(
            $@"INSERT INTO ""TokenCounters"" (""TenantId"", ""Date"", ""LastNumber"")
               VALUES ({tenantId}, {date}, 1)
               ON CONFLICT (""TenantId"", ""Date"")
               DO UPDATE SET ""LastNumber"" = ""TokenCounters"".""LastNumber"" + 1
               RETURNING ""LastNumber""").ToListAsync();
        return result[0];
    }

    /// <summary>Atomically hands out the next bill number for a tenant, by exactly the same
    /// UPSERT trick as <see cref="NextTokenNumberAsync"/> — see that method for why a
    /// SELECT-then-UPDATE won't do and why the non-relational branch can skip it. The only
    /// difference is the key: one counter per tenant with no date, because bill numbers run
    /// continuously for the life of the cafe instead of restarting each morning.</summary>
    private static async Task<int> NextBillNumberAsync(CafePosDbContext db, int tenantId)
    {
        if (!db.Database.IsRelational())
        {
            // IgnoreQueryFilters: guest QR orders build under an explicit tenant that is not the
            // ambient one, and the tenant filter would otherwise hide that cafe's existing counter
            // row — every guest order would then try to insert a second row starting back at 1.
            var counter = await db.BillCounters.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId);
            if (counter is null)
            {
                counter = new BillCounter { TenantId = tenantId, LastNumber = 1 };
                db.BillCounters.Add(counter);
            }
            else
            {
                counter.LastNumber += 1;
            }
            await db.SaveChangesAsync();
            return counter.LastNumber;
        }

        var result = await db.Database.SqlQuery<int>(
            $@"INSERT INTO ""BillCounters"" (""TenantId"", ""LastNumber"")
               VALUES ({tenantId}, 1)
               ON CONFLICT (""TenantId"")
               DO UPDATE SET ""LastNumber"" = ""BillCounters"".""LastNumber"" + 1
               RETURNING ""LastNumber""").ToListAsync();
        return result[0];
    }

    public async Task<Dictionary<int, List<ModifierGroupInfo>>> LoadModifierGroupsAsync(
        CafePosDbContext db, IEnumerable<int> menuItemIds, int? explicitTenantId)
    {
        var ids = menuItemIds.Distinct().ToArray();
        if (ids.Length == 0) return [];

        var groups = await TenantScoped(db.Modifiers, explicitTenantId)
            .Where(m => ids.Contains(m.MenuItemId))
            .Select(m => new { m.MenuItemId, Info = new ModifierGroupInfo(m.Id, m.Name, m.Type, m.IsRequired) })
            .ToListAsync();

        // Every requested id gets an entry, including the (common) items with no groups at all —
        // otherwise ResolveLinePricingAsync couldn't tell "this item has none" from "nothing was
        // preloaded" and would fall back to querying for exactly the items that need it least.
        var byMenuItem = ids.ToDictionary(id => id, _ => new List<ModifierGroupInfo>());
        foreach (var g in groups) byMenuItem[g.MenuItemId].Add(g.Info);
        return byMenuItem;
    }

    public Task<List<TaxGroupInfo>> LoadTaxGroupsAsync(CafePosDbContext db, int? explicitTenantId) =>
        TenantScoped(db.TaxGroups, explicitTenantId)
            .Select(t => new TaxGroupInfo(t.Id, t.RatePct, t.IsDefault))
            .ToListAsync();

    public async Task<OrderItem?> AddOrUpdateCartItemAsync(CafePosDbContext db, Order order, int menuItemId, int qty, string? modifier, int explicitTenantId,
        int? variantId = null, List<int>? modifierOptionIds = null)
    {
        if (qty < 0) throw new ApiValidationException("Quantity cannot be negative.");

        var menuItem = await TenantScoped(db.MenuItems, explicitTenantId).Include(m => m.Station).FirstOrDefaultAsync(m => m.Id == menuItemId);
        if (menuItem is null) throw new ApiValidationException("Menu item not found.");

        // A distinct cart line per (menu item, variant, exact set of add-ons, free-text note) —
        // "Half + Extra Cheese" and "Full, no cheese" must never collapse into the same line.
        var normalizedOptionIds = (modifierOptionIds ?? []).OrderBy(x => x).ToList();
        bool SameLine(OrderItem i) =>
            i.FireBatch == 0 && i.MenuItemId == menuItemId && i.Modifier == modifier && i.VariantId == variantId &&
            i.SelectedModifiers.Select(m => m.ModifierOptionId).OrderBy(x => x).SequenceEqual(normalizedOptionIds);

        var existing = order.Items.FirstOrDefault(SameLine);

        if (qty == 0)
        {
            if (existing is not null)
            {
                order.Items.Remove(existing);
                db.OrderItems.Remove(existing);
            }
            // If this removal emptied out every unfired line while the order sat in the
            // staff confirmation queue, drop the flag — otherwise the order lingers there
            // as a "0 items" card whose Confirm can never succeed.
            if (order.PendingStaffConfirmation && !order.Items.Any(i => i.FireBatch == 0))
                order.PendingStaffConfirmation = false;
            order.Subtotal = order.Items.Where(i => !i.Voided).Sum(i => i.Price * i.Qty);
            await ApplyOffersAsync(db, order, explicitTenantId);
            RecomputeTotals(order, await GetTaxRatePctAsync(db, explicitTenantId));
            return null;
        }

        if (!menuItem.Available)
            throw new ApiValidationException($"{menuItem.Name} is currently unavailable.");

        // MRP items are priced by the biller at the till (see MenuItem.IsOpenPrice) and a guest
        // has no way to type a rate, so they can't be self-ordered. MenuController.List already
        // keeps them off the QR menu; this is the matching guard on the write path, phrased for
        // a customer rather than reusing ResolveLinePricingAsync's "enter its rate" message.
        if (menuItem.IsOpenPrice)
            throw new ApiValidationException($"{menuItem.Name} has to be added by a staff member — please ask them for it.");

        if (existing is not null)
        {
            existing.Qty = qty;
        }
        else
        {
            var lineSettings = await TenantScoped(db.Settings, explicitTenantId)
                .Select(s => new { s.DefaultHsnCode, s.MenuPricesIncludeTax }).FirstOrDefaultAsync();
            var (linePrice, variantName, selections, stationName, taxRatePct, priceIncludesTax, hsnCode) =
                await ResolveLinePricingAsync(db, menuItem, variantId, modifierOptionIds, explicitTenantId, openPrice: null,
                    lineSettings?.DefaultHsnCode, lineSettings?.MenuPricesIncludeTax ?? false);
            existing = new OrderItem
            {
                OrderId = order.Id,
                MenuItemId = menuItem.Id,
                Name = menuItem.Name,
                Qty = qty,
                Price = linePrice,
                Modifier = modifier,
                VariantId = variantId,
                VariantName = variantName,
                SelectedModifiers = selections,
                StationName = stationName,
                VegNonVegType = menuItem.VegNonVegType,
                Subtitle = string.IsNullOrWhiteSpace(menuItem.Subtitle) ? null : menuItem.Subtitle,
                TaxRatePct = taxRatePct,
                PriceIncludesTax = priceIncludesTax,
                HsnCode = hsnCode,
                FireBatch = 0,
                // Guest cart lines are created without a JWT — stamp the tenant explicitly or
                // StampTenantIds defaults them to tenant 1, hiding them from the cafe's staff.
                TenantId = explicitTenantId,
            };
            order.Items.Add(existing);
        }

        order.Subtotal = order.Items.Where(i => !i.Voided).Sum(i => i.Price * i.Qty);
        await ApplyOffersAsync(db, order, explicitTenantId);
        RecomputeTotals(order, await GetTaxRatePctAsync(db, explicitTenantId));

        // No inventory deduction here — this line stays FireBatch == 0 (unfired) until the
        // guest cart's next fire/place-order call, which is the single deduction point
        // (see FireUnfiredItemsAsync).
        return existing;
    }

    public async Task<(decimal Price, string? VariantName, List<OrderItemModifier> Modifiers, string StationName, decimal? TaxRatePct, bool PriceIncludesTax, string? HsnCode)> ResolveLinePricingAsync(
        CafePosDbContext db, MenuItem menuItem, int? variantId, List<int>? modifierOptionIds, int? explicitTenantId,
        decimal? openPrice = null, string? defaultHsnCode = null, bool tenantPricesIncludeTax = false,
        IReadOnlyDictionary<int, List<ModifierGroupInfo>>? preloadedGroups = null,
        IReadOnlyList<TaxGroupInfo>? preloadedTaxGroups = null)
    {
        var price = menuItem.Price;
        string? variantName = null;
        if (variantId is int vid)
        {
            var variant = await TenantScoped(db.Variants, explicitTenantId).FirstOrDefaultAsync(v => v.Id == vid);
            if (variant is null || variant.MenuItemId != menuItem.Id)
                throw new ApiValidationException("Selected variant not found for this item.");
            if (!variant.IsAvailable)
                throw new ApiValidationException($"'{variant.Name}' is currently unavailable.");
            price = variant.Price;
            variantName = variant.Name;
        }

        // MRP item (see MenuItem.IsOpenPrice): the rate the biller typed replaces whatever the
        // catalog holds — base price OR the selected variant's — because the number printed on
        // this particular pack is the only rate actually valid for it. The variant is still
        // resolved above, so "Coke 500ml" keeps its name/availability check and only the money
        // comes from the till. Add-on deltas below still apply on top of the typed rate.
        //
        // openPrice on an ordinary item is IGNORED rather than honoured: pricing stays
        // server-authoritative everywhere the cafe didn't explicitly open it up, so a crafted
        // request can't re-price the menu.
        if (menuItem.IsOpenPrice)
        {
            if (openPrice is not decimal typedRate)
                throw new ApiValidationException($"{menuItem.Name} is billed at MRP — enter its rate to add it to the order.");
            if (typedRate <= 0)
                throw new ApiValidationException($"Enter a rate greater than zero for {menuItem.Name}.");
            price = decimal.Round(typedRate, 2);
        }

        // Every modifier group on this item, needed for three separate checks below:
        // option ownership, per-type selection limits, and required-group enforcement.
        //
        // Skipped when the caller preloaded the whole order's groups (see
        // LoadModifierGroupsAsync). An item with no groups at all is a present-but-empty entry
        // there, so a miss genuinely means "not preloaded", not "none".
        var groups = preloadedGroups is not null
            ? preloadedGroups.GetValueOrDefault(menuItem.Id) ?? []
            : await TenantScoped(db.Modifiers, explicitTenantId)
                .Where(m => m.MenuItemId == menuItem.Id)
                .Select(m => new ModifierGroupInfo(m.Id, m.Name, m.Type, m.IsRequired))
                .ToListAsync();

        var selections = new List<OrderItemModifier>();
        var chosenGroupIds = new List<int>();
        if (modifierOptionIds is { Count: > 0 })
        {
            // A REPEATED id means "N of this option" (2x Extra Cheese) — that's how a
            // Quantity-type group is sent, without widening the wire contract from
            // List<int> and breaking the QR ordering page.
            var qtyByOptionId = modifierOptionIds.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
            var distinctIds = qtyByOptionId.Keys.ToList();
            var options = await TenantScoped(db.ModifierOptions, explicitTenantId)
                .Where(o => distinctIds.Contains(o.Id))
                .ToListAsync();
            if (options.Count != distinctIds.Count)
                throw new ApiValidationException("One or more selected add-ons were not found.");

            var groupById = groups.ToDictionary(g => g.Id);
            if (options.Any(o => !groupById.ContainsKey(o.ModifierId)))
                throw new ApiValidationException("One or more selected add-ons don't belong to this item.");

            // A Radio group is "pick exactly one" — the POS enforces it in the picker, but
            // the QR page and any direct API caller reach this same path, so it's checked here too.
            var overPickedRadio = options
                .GroupBy(o => o.ModifierId)
                .FirstOrDefault(g => groupById[g.Key].Type == "Radio" && g.Count() > 1);
            if (overPickedRadio is not null)
                throw new ApiValidationException($"Choose only one option for '{groupById[overPickedRadio.Key].Name}'.");

            foreach (var option in options)
            {
                // Only a Quantity group can take more than one of the same option; a stray
                // duplicate on a Radio/MultiSelect group collapses to a single unit.
                var qty = groupById[option.ModifierId].Type == "Quantity" ? qtyByOptionId[option.Id] : 1;
                price += option.Price * qty;
                var selection = new OrderItemModifier
                {
                    ModifierOptionId = option.Id,
                    Name = option.Name,
                    Price = option.Price,
                    Qty = qty,
                };
                // Same explicit stamp as the OrderItem itself — guest requests have no JWT
                // for StampTenantIds to derive the tenant from.
                if (explicitTenantId is int selTid) selection.TenantId = selTid;
                selections.Add(selection);
            }
            chosenGroupIds = options.Select(o => o.ModifierId).Distinct().ToList();
        }

        // Required groups must each contribute a selection. Deliberately outside the block
        // above so an order that sends NO options at all is still rejected — until now
        // Modifier.IsRequired was stored and shown in the UI but never actually enforced.
        var missingRequired = groups
            .Where(g => g.IsRequired && !chosenGroupIds.Contains(g.Id))
            .Select(g => g.Name)
            .ToList();
        if (missingRequired.Count > 0)
            throw new ApiValidationException($"{menuItem.Name}: please choose {string.Join(", ", missingRequired)}.");

        // The item's own slab wins; otherwise the tenant's default group. Both come back in
        // one query. Null means neither exists — RecomputeTotals then bills this line at
        // CafeSettings.TaxRatePct, i.e. exactly the pre-tax-group behaviour.
        // Filtered in memory when the caller preloaded the cafe's slabs (see LoadTaxGroupsAsync);
        // the predicate is identical either way.
        var taxGroups = preloadedTaxGroups is not null
            ? preloadedTaxGroups.Where(t => t.Id == menuItem.TaxGroupId || t.IsDefault).ToList()
            : await TenantScoped(db.TaxGroups, explicitTenantId)
                .Where(t => t.Id == menuItem.TaxGroupId || t.IsDefault)
                .Select(t => new TaxGroupInfo(t.Id, t.RatePct, t.IsDefault))
                .ToListAsync();
        var taxRatePct = taxGroups.FirstOrDefault(t => t.Id == menuItem.TaxGroupId)?.RatePct
            ?? taxGroups.FirstOrDefault(t => t.IsDefault)?.RatePct;

        // The item's own HSN wins; otherwise the cafe-wide default (a restaurant bills its whole
        // menu under one SAC). Blank is normalised to null so "code not set" is one value rather
        // than two, and the invoice's HSN column can be dropped on a simple null check.
        var hsn = Blank(menuItem.HsnCode) ?? Blank(defaultHsnCode);

        // An MRP line is inclusive because its printed rate is a legal ceiling; a cafe with
        // CafeSettings.MenuPricesIncludeTax on chooses the same treatment for every other line
        // too, by billing policy rather than by law. Either reason lands on the same flag —
        // RecomputeTotals doesn't distinguish why a line is inclusive, only that it is.
        var priceIncludesTax = menuItem.IsOpenPrice || tenantPricesIncludeTax;
        return (price, variantName, selections, menuItem.Station?.Name ?? "Kitchen", taxRatePct, priceIncludesTax, hsn);

        static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    public async Task<bool> FireUnfiredItemsAsync(CafePosDbContext db, Order order, int? explicitTenantId)
    {
        var unfired = order.Items.Where(i => i.FireBatch == 0).ToList();
        if (unfired.Count == 0) return false;

        order.CurrentFireBatch += 1;
        foreach (var item in unfired) item.FireBatch = order.CurrentFireBatch;
        var fireBatch = new OrderFireBatch { OrderId = order.Id, BatchNumber = order.CurrentFireBatch };
        if (explicitTenantId is int batchTid) fireBatch.TenantId = batchTid;
        order.FireBatches.Add(fireBatch);
        RecomputeBatchStatus(db, order, order.CurrentFireBatch);
        RecomputeOrderStatus(order);

        var isFirstFire = order.CurrentFireBatch == 1;
        var count = unfired.Count;
        var notification = new AppNotification
        {
            Title = isFirstFire ? "New order placed" : "New items added to order",
            Body = isFirstFire
                ? $"{order.Title} — {count} item{(count == 1 ? "" : "s")}, ₹{order.Total:0.00}."
                : $"{order.Title} — {count} new item{(count == 1 ? "" : "s")} fired.",
            Category = NotificationCategory.OrderPlaced,
            Channel = NotificationChannel.InApp,
            ActionUrl = $"/orders/{order.Id}",
        };
        if (explicitTenantId is int tid) notification.TenantId = tid;
        db.Notifications.Add(notification);

        // The single point inventory is consumed at — food is physically being made now.
        var menuIds = unfired.Select(i => i.MenuItemId).Distinct().ToList();
        var menu = await TenantScoped(db.MenuItems, explicitTenantId).Where(m => menuIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id);
        await ConsumeInventoryAsync(db, menu, unfired, order.Id, explicitTenantId);

        return true;
    }

    public void MarkPendingConfirmation(CafePosDbContext db, Order order, int? explicitTenantId)
    {
        if (order.PendingStaffConfirmation) return; // already flagged — avoid a duplicate ping
        order.PendingStaffConfirmation = true;

        var notification = new AppNotification
        {
            Title = "New order awaiting confirmation",
            Body = $"{order.Title} — confirm to send this to the kitchen.",
            Category = NotificationCategory.OrderPendingConfirmation,
            Channel = NotificationChannel.InApp,
            ActionUrl = $"/orders/{order.Id}",
        };
        if (explicitTenantId is int tid) notification.TenantId = tid;
        db.Notifications.Add(notification);
    }

    /// <summary>What one fire batch's status should be, given the statuses of its own NON-VOIDED
    /// lines — the batch sits at its least-progressed live line, and is Served once none of them
    /// is outstanding.
    ///
    /// An empty sequence means every line on that KOT has since been voided, and it rolls up
    /// Served for the same reason a finished batch does: there is nothing left for the kitchen to
    /// make. This case used to leave the batch pinned at whatever it held before the last void
    /// (New/Preparing), which RecomputeOrderStatus below then picked as the order's
    /// least-progressed work forever. Since a table only frees once its order is both Paid AND
    /// Served (see TablesController's occupancy query), a table whose last remaining KOT was
    /// voided stayed Occupied even after the bill was fully settled, with no way to clear it.</summary>
    public static OrderStatus BatchRollup(IReadOnlyCollection<OrderStatus> liveItemStatuses)
    {
        var active = liveItemStatuses.Where(s => s != OrderStatus.Served).ToList();
        return active.Count > 0 ? active.Min() : OrderStatus.Served;
    }

    public void RecomputeBatchStatus(CafePosDbContext db, Order order, int batchNumber)
    {
        var batch = order.FireBatches.FirstOrDefault(b => b.BatchNumber == batchNumber);
        if (batch is null) return;
        // Voided lines are excluded — a voided-while-Preparing item's Status is frozen
        // (nobody advances it further, the kitchen was told to stop), so counting it here
        // would permanently block the batch from ever rolling up to Ready/Served.
        var items = order.Items.Where(i => i.FireBatch == batchNumber && !i.Voided).ToList();

        var previous = batch.Status;
        batch.Status = BatchRollup(items.Select(i => i.Status).ToList());

        if (previous != OrderStatus.Ready && batch.Status == OrderStatus.Ready)
        {
            db.Notifications.Add(new AppNotification
            {
                Title = "Order ready to serve",
                Body = $"{order.Title} is ready — {items.Count} item{(items.Count == 1 ? "" : "s")}.",
                Category = NotificationCategory.Order,
                Channel = NotificationChannel.InApp,
                ActionUrl = $"/orders/{order.Id}",
            });
        }
    }

    /// <summary>Rolls the order up to its least-progressed work, the same way a batch rolls up its
    /// items. Requires order.Items to be loaded (every call site loads them).
    ///
    /// A not-yet-fired line pins the order at New even when every KOT on it is Served. Fire batches
    /// alone used to decide this, and an unfired line belongs to no batch — so an order that had
    /// one (added late via Add Item, or a quantity top-up, see OrdersController.UpdateItemQty)
    /// rolled up to SERVED with an item the kitchen had never been told about. That order then
    /// dropped out of the activeOnly list, freed its table, and could be settled for a total that
    /// billed food nobody ever made.
    ///
    /// That pin lifts once the bill is Paid, and it has to: settled means nothing more is going to
    /// the kitchen, so an unfired line left over is a line that never will be made, not outstanding
    /// work. Pinning it at New past the settle stranded the order — a table frees only once its
    /// order is both Paid AND Served (see TablesController's occupancy query), while every route
    /// out (Fire, AddItem, RemoveItem, Cancel) refuses a paid order, so the seat could never be
    /// cleared again. Serving progress is deliberately still allowed after payment (see
    /// OrdersController.AdvanceUnitsEndpoint/ServeAll), so the fired KOTs alone decide the rollup
    /// from here and marking them served releases the table exactly as it does on any other
    /// bill.</summary>
    public static void RecomputeOrderStatus(Order order)
    {
        var hasUnfired = !order.Paid && order.Items.Any(i => i.FireBatch == 0 && !i.Voided);
        var active = order.FireBatches.Where(b => b.Status != OrderStatus.Served).ToList();
        order.Status = hasUnfired
            ? OrderStatus.New
            : active.Count > 0
                ? active.Min(b => b.Status)
                : (order.FireBatches.Count > 0 ? OrderStatus.Served : OrderStatus.New);
    }

    /// <summary>Flips an order to Paid and everything that has to happen in the same instant —
    /// shared by OrdersController.Pay/Close (the normal settle path) and
    /// ApprovalsController.ExecuteApprovedActionAsync (a Complimentary write-off that only
    /// gets here once an Owner approves it, well after the original Pay call already
    /// returned). Callers still own SaveChangesAsync/concurrency handling around this.</summary>
    public static async Task CloseOrderAsync(CafePosDbContext db, Order order)
    {
        order.Paid = true;

        // Re-roll the status now that Paid has flipped: any leftover unfired line stops
        // counting as outstanding kitchen work the moment the bill closes (see
        // RecomputeOrderStatus above). Without this the order stayed pinned at New with no
        // way back — Fire/AddItem/RemoveItem/Cancel all refuse a paid order — so its table
        // could never be freed again.
        RecomputeOrderStatus(order);

        // Guest-session settle hook (doc Section 5.1): the exact instant a table's bill
        // is settled, close its GuestSession too — this is what makes the old phone's
        // cookie start getting 410s immediately instead of staying usable until the
        // next expiry sweep. IgnoreQueryFilters: an anonymous QR settle has no JWT tenant.
        var session = await db.GuestSessions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.OrderId == order.Id && (s.Status == GuestSessionStatus.Active || s.Status == GuestSessionStatus.Locked));
        if (session is not null)
        {
            session.Status = GuestSessionStatus.Closed;
            session.ClosedReason = SessionCloseReason.Settled;
            session.ClosedAt = DateTime.UtcNow;
        }
    }

    /// <summary>Recomputes tax and total from the order's live lines.
    ///
    /// Tax is charged PER LINE at that line's own snapshotted rate (OrderItem.TaxRatePct),
    /// so one order can mix slabs — a 5% item and a 12% item bill correctly side by side.
    /// `fallbackTaxRatePct` covers lines with no snapshot: rows placed before tax groups
    /// existed, and items with neither their own group nor a tenant default. When no line
    /// has a rate of its own this reduces to exactly the previous flat-rate arithmetic.
    ///
    /// Order-level discounts (item, bill, coupon, gift card) are split across lines in
    /// proportion to each line's gross, because a discount on a mixed-slab bill has to
    /// reduce the taxable value of each slab — putting it all against one slab would
    /// understate or overstate the tax due on the other.
    ///
    /// A line flagged OrderItem.PriceIncludesTax (an MRP item) has its tax carved OUT of its
    /// price instead of added on top, so its total lands exactly on the printed rate. This
    /// method therefore also restates o.Subtotal — callers set it to the plain gross first,
    /// which is still the right answer whenever no such line is present.
    ///
    /// Order.TaxSuppressed overrides every rate above to 0 — a bill settled on a tender this
    /// cafe doesn't charge tax on. Reading it off the ORDER rather than taking it as an argument
    /// is what makes it stick: every one of this method's ~20 call sites keeps a settled bill's
    /// tax off without having to know the setting exists.</summary>
    /// <inheritdoc cref="IOrderBuildingService.ApplyOffersAsync"/>
    public async Task ApplyOffersAsync(CafePosDbContext db, Order order, int? explicitTenantId)
    {
        var active = order.Items.Where(i => !i.Voided).ToList();

        // Clear first, so an offer that stopped qualifying (a BOGO whose items were removed, a
        // happy hour that ended before a re-price) leaves nothing stale behind.
        foreach (var item in order.Items) item.OfferDiscountAmount = 0;
        order.OfferDiscountAmount = 0;
        order.AppliedOfferTitle = null;

        if (active.Count == 0) return;

        var offers = await TenantScoped(db.Offers, explicitTenantId)
            .Include(o => o.Items)
            .Where(o => o.IsActive)
            .ToListAsync();
        if (offers.Count == 0) return;

        // Category scope matches on MenuItem.Category, which the line doesn't carry — resolve it
        // for every distinct item on the cart in one query.
        var menuItemIds = active.Select(i => i.MenuItemId).Distinct().ToList();
        var categoryByItemId = await TenantScoped(db.MenuItems, explicitTenantId)
            .Where(m => menuItemIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Category })
            .ToDictionaryAsync(m => m.Id, m => m.Category);

        // Key each cart line by its index in `active` (not OrderItem.Id, which is still 0 for a
        // not-yet-saved order being created) so the engine's per-line result maps straight back.
        var lines = active
            .Select((it, idx) => new OfferCartLine(
                idx, it.MenuItemId, categoryByItemId.GetValueOrDefault(it.MenuItemId), it.Name, it.Price, it.Qty))
            .ToList();

        var evaluation = OfferEngine.Evaluate(lines, offers, DateTime.UtcNow);

        foreach (var (lineIndex, amount) in evaluation.PerLineDiscount)
            active[lineIndex].OfferDiscountAmount = amount;

        order.OfferDiscountAmount = evaluation.TotalDiscount;
        order.AppliedOfferTitle = evaluation.Applied.Count == 0
            ? null
            : string.Join(", ", evaluation.Applied.Select(a => a.Title));
    }

    /// <summary>What this order's Total WOULD be if it were settled on a tender the cafe charges
    /// no tax on (see PaymentModeTax) — what the payment screen needs to show a cashier before
    /// they commit to a tender, and what OrderDto carries for exactly that.
    ///
    /// Derived rather than recomputed, and exactly equal to what RecomputeTotals produces with
    /// Order.TaxSuppressed set: dropping the tax takes `Tax` off the total, except for the part
    /// of it that was carved OUT of a tax-inclusive line's price, which was never added on top in
    /// the first place. Tax charged on the Service/Packing/Delivery charges (Order.ChargesTaxAmount,
    /// included in Tax) needs no special case here — it is added on top like ordinary line tax, so
    /// subtracting Tax removes it. On an already-suppressed order Tax is 0 and this is just the
    /// total.</summary>
    public static decimal TaxFreeTotal(Order o)
    {
        var embedded = o.Items.Where(i => !i.Voided && i.PriceIncludesTax).Sum(i => i.TaxAmount);
        return o.Total - (o.Tax - embedded);
    }

    public static void RecomputeTotals(Order o, decimal fallbackTaxRatePct)
    {
        // Per-line tax means this now reads o.Items, where the old flat-rate version only
        // needed the o.Subtotal scalar. An order fetched without .Include(o => o.Items)
        // would therefore recompute to a zero total — fail loudly instead of silently
        // rewriting someone's bill. (All items voided is not this case: Subtotal is the
        // sum of non-voided lines, so it's 0 too.)
        if (o.Items.Count == 0 && o.Subtotal > 0)
            throw new InvalidOperationException(
                $"RecomputeTotals needs order {o.Id}'s Items loaded — fetch it with .Include(o => o.Items).");

        var lines = o.Items.Where(i => !i.Voided).ToList();
        var gross = lines.Sum(i => i.Price * i.Qty);

        // Offers are attributed to specific lines (OrderItem.OfferDiscountAmount, set by
        // ApplyOffersAsync) so they land on the right GST slab — they come off each line directly,
        // BEFORE the proportional pool below, rather than being spread across the whole bill. The
        // order's own OfferDiscountAmount is restated from the clamped per-line sum so the stored
        // total can never claim more than was actually deducted.
        var offerTotal = lines.Sum(i => Math.Min(Math.Max(0, i.OfferDiscountAmount), i.Price * i.Qty));
        o.OfferDiscountAmount = offerTotal;
        var afterOfferGross = gross - offerTotal;

        // The order-level discounts (manual, bill, coupon, gift card, loyalty) still spread
        // proportionally, now over what's left once offers have been taken off — so a line an
        // offer already made free doesn't also soak up a share of the coupon.
        var poolRaw = o.DiscountAmount + o.BillDiscountAmount + o.CouponDiscountAmount + o.GiftCardAmountApplied + o.LoyaltyDiscountAmount + o.MilestoneDiscountAmount;
        var pool = Math.Min(Math.Max(0, poolRaw), Math.Max(0, afterOfferGross));

        decimal tax = 0;
        // Tax already sitting INSIDE tax-inclusive lines' prices (MRP items — see
        // OrderItem.PriceIncludesTax). It's real tax, so it counts towards o.Tax and the bill's
        // GST breakdown, but it must not be charged a second time on top: both o.Subtotal and
        // o.Total back it out below, which lands an MRP line's total exactly on its printed
        // rate. Stays 0 on an order with no such line, so ordinary bills are untouched.
        decimal embeddedTax = 0;
        // The last line absorbs the rounding remainder so the per-line taxable amounts
        // always add back up to the order's taxable total, however the shares divide.
        var allocated = 0m;
        for (var idx = 0; idx < lines.Count; idx++)
        {
            var line = lines[idx];
            var lineGross = line.Price * line.Qty;
            // This line's own offer discount comes off first (clamped to its value), so tax is
            // charged on what's actually paid for it and the discount stays on its own slab.
            var lineOffer = Math.Min(Math.Max(0, line.OfferDiscountAmount), lineGross);
            var lineAfterOffer = lineGross - lineOffer;
            // Proportional share of the order-level pool, split over the post-offer value. Last
            // line absorbs the rounding remainder so the shares always add back to `pool`.
            var poolShare = idx == lines.Count - 1
                ? pool - allocated
                : (afterOfferGross > 0 ? Math.Round(pool * (lineAfterOffer / afterOfferGross), 2) : 0);
            allocated += poolShare;

            var net = Math.Max(0, lineAfterOffer - poolShare);
            // A bill settled on a tender this cafe doesn't charge tax on (see Order.TaxSuppressed
            // and PaymentModeTax) bills every line at 0% — including a tax-inclusive MRP line,
            // which then simply keeps its printed price with nothing carved out of it. That's the
            // right answer for an MRP item: the customer pays the price on the packet either way,
            // so suppressing tax must not re-price it, only stop reporting tax inside it.
            var ratePct = o.TaxSuppressed ? 0m : line.TaxRatePct ?? fallbackTaxRatePct;
            if (line.PriceIncludesTax)
            {
                // Carve the tax out of the rate rather than adding it on: taxable = net / (1 + rate).
                // Deriving TaxAmount by subtraction (not a second Round) keeps taxable + tax
                // exactly equal to net, so the line can't drift a paisa off the printed MRP.
                line.TaxableAmount = Math.Round(net / (1 + ratePct / 100), 2);
                line.TaxAmount = net - line.TaxableAmount;
                embeddedTax += line.TaxAmount;
            }
            else
            {
                line.TaxableAmount = net;
                line.TaxAmount = Math.Round(net * ratePct / 100, 2);
            }
            tax += line.TaxAmount;
        }

        // Service/Packing/Delivery are ancillary to the same supply as the food, so at a cafe
        // that has opted in (Order.ChargesTaxRatePct — see CafeSettings.TaxChargesEnabled) they
        // carry tax too. Added ON TOP of the charge rather than carved out of it: unlike an MRP,
        // these are figures the cafe sets itself, so the amount configured is the charge and the
        // tax is additional — carving it out would quietly cut the cafe's own service revenue.
        //
        // Tip and RoundOff are excluded on purpose. A tip is not consideration for the supply,
        // and the round-off is an adjustment to a figure tax has already been computed on —
        // taxing it would make the printed total un-roundable.
        //
        // The order-level discount pool doesn't reduce this: those discounts are given on the
        // goods, and the charge is billed at its stated amount either way.
        //
        // Null rate (the setting off, and every order placed before the column existed) leaves
        // both figures at 0 and the arithmetic below identical to what it always was.
        var chargeBase = o.ServiceChargeAmount + o.PackingChargeAmount + o.DeliveryChargeAmount;
        decimal? chargesRate = o.ChargesTaxRatePct is null ? null : (o.TaxSuppressed ? 0m : o.ChargesTaxRatePct.Value);
        o.ChargesTaxableAmount = chargesRate is null ? 0m : chargeBase;
        o.ChargesTaxAmount = chargesRate is null ? 0m : Math.Round(chargeBase * chargesRate.Value / 100, 2);
        tax += o.ChargesTaxAmount;

        o.Tax = tax;
        // Restated (callers set it to the plain gross before calling) so the printed
        // Subtotal + Tax = Total still holds once an inclusive line's tax has been carved out
        // of its price. With no inclusive line embeddedTax is 0 and this is the gross, exactly
        // as before.
        o.Subtotal = gross - embeddedTax;
        // Both the offer (per line) and the pool (proportional) come off the gross; clamped to 0
        // so a bill fully covered by discounts never goes negative before charges are added.
        o.Total = Math.Max(0, gross - offerTotal - pool) - embeddedTax + tax
            + o.ServiceChargeAmount + o.PackingChargeAmount + o.DeliveryChargeAmount + o.TipAmount + o.RoundOffAmount;
    }

    /// <summary>Starting Service/Packing/Delivery charge for a brand-new order, driven purely
    /// by CafeSettings' Auto Charges config (see Entities.CafeSettings) — a charge with no
    /// default set (null) or not enabled for this order type comes back 0, same as a biller
    /// never having touched that tile. DINE_IN/TAKEAWAY/DELIVERY/QSR (Token) are matched;
    /// CASH counter sales don't carry any of these three charges by default.</summary>
    private static (decimal Service, decimal Packing, decimal Delivery) ComputeDefaultCharges(CafeSettings s, string orderType, decimal subtotal)
    {
        var service = s.ServiceChargeDefaultPct is decimal svcPct && AppliesTo(orderType, s.ServiceChargeAutoApplyDineIn, s.ServiceChargeAutoApplyTakeaway, s.ServiceChargeAutoApplyDelivery, s.ServiceChargeAutoApplyToken)
            ? Math.Round(subtotal * svcPct / 100, 2) : 0;
        var packing = s.PackingChargeDefaultAmount is decimal pkgAmt && AppliesTo(orderType, s.PackingChargeAutoApplyDineIn, s.PackingChargeAutoApplyTakeaway, s.PackingChargeAutoApplyDelivery, s.PackingChargeAutoApplyToken)
            ? pkgAmt : 0;
        var delivery = s.DeliveryChargeDefaultAmount is decimal dlvAmt && AppliesTo(orderType, s.DeliveryChargeAutoApplyDineIn, s.DeliveryChargeAutoApplyTakeaway, s.DeliveryChargeAutoApplyDelivery, s.DeliveryChargeAutoApplyToken)
            ? dlvAmt : 0;
        return (service, packing, delivery);
    }

    private static bool AppliesTo(string orderType, bool dineIn, bool takeaway, bool delivery, bool token) => orderType switch
    {
        "DINE_IN" => dineIn,
        "TAKEAWAY" => takeaway,
        "DELIVERY" => delivery,
        "QSR" => token,
        _ => false,
    };

    public async Task<Customer> FindOrCreateCustomerAsync(CafePosDbContext db, string guestName, string? guestPhone, int? explicitTenantId = null, string? guestAddress = null)
    {
        var customersQuery = TenantScoped(db.Customers, explicitTenantId).Include(c => c.FavoriteItems);

        Customer? customer = null;
        if (guestPhone is not null)
            customer = await customersQuery.FirstOrDefaultAsync(c => c.Phone == guestPhone);

        // Name-only matching requires a phone to check the match against — without one, two
        // strangers who happen to share a name (or every unnamed walk-in, which all land on
        // the literal "Walk-in Guest" string) would otherwise merge onto the same record.
        // Callers with no guestPhone always get a fresh Customer below instead.
        if (customer is null && guestPhone is not null)
        {
            var normalizedName = guestName.Trim().ToLower();
            var byName = await customersQuery.FirstOrDefaultAsync(c => c.Name.ToLower() == normalizedName);
            // A name match only counts when it can't contradict the phone we were given: the
            // matched record either has no phone on file yet (it adopts this one just below)
            // or already has this same one. Same name but a DIFFERENT number on file is a
            // different person — fall through and create a fresh customer instead of
            // crediting this visit to a stranger and silently dropping the new number.
            if (byName?.Phone is null) customer = byName;
        }

        var trimmedAddress = string.IsNullOrWhiteSpace(guestAddress) ? null : guestAddress.Trim();

        if (customer is not null)
        {
            if (guestPhone is not null && customer.Phone is null) customer.Phone = guestPhone;
            // Overwrite rather than "only if blank" — a returning guest who gives a new
            // address at the counter has moved, the old one on file is stale either way.
            if (trimmedAddress is not null) customer.AddressLine1 = trimmedAddress;
            return customer;
        }

        var slug = new string(guestName.Where(char.IsLetter).ToArray()).ToUpperInvariant();
        customer = new Customer
        {
            Name = guestName,
            Phone = guestPhone,
            AddressLine1 = trimmedAddress,
            ReferralCode = $"{(slug.Length >= 4 ? slug[..4] : slug.PadRight(4, 'X'))}{Random.Shared.Next(100, 999)}",
        };
        if (explicitTenantId is int tid) customer.TenantId = tid;
        db.Customers.Add(customer);
        return customer;
    }

    private static void RecordVisit(Customer customer, decimal amountSpent, decimal loyaltyEarnPct)
    {
        customer.VisitCount += 1;
        customer.TotalSpent += amountSpent;
        customer.TotalPoints += LoyaltyPoints.ForSpend(amountSpent, loyaltyEarnPct);
        customer.LastVisitAt = DateTime.UtcNow;
    }

    private void TrackFavorites(CafePosDbContext db, Customer customer, List<OrderItem> items, int? explicitTenantId = null)
    {
        var existing = customer.FavoriteItems;
        foreach (var line in items)
        {
            var fav = existing.FirstOrDefault(f => f.MenuItemId == line.MenuItemId);
            if (fav is null)
            {
                var newFav = new FavoriteItem { Customer = customer, MenuItemId = line.MenuItemId, OrderCount = line.Qty };
                if (explicitTenantId is int tid) newFav.TenantId = tid;
                db.FavoriteItems.Add(newFav);
            }
            else
            {
                fav.OrderCount += line.Qty;
            }
        }
    }

    /// <summary>Marks a ledger row as the second-or-later draw against an already-fired line (a
    /// quantity increase), which is what keeps it out of the fire-time idempotency index — see
    /// CafePosDbContext, which filters that index on Reason IS NULL.</summary>
    public const string TopUpDeductionReason = "Quantity increased after fire";

    public async Task ConsumeInventoryForAddedUnitsAsync(CafePosDbContext db, OrderItem line, int extraQty, int orderId)
    {
        if (extraQty <= 0) return;
        var menu = await db.MenuItems.Where(m => m.Id == line.MenuItemId).ToDictionaryAsync(m => m.Id);
        // A stand-in line carrying ONLY the added units, so the recipe maths below multiplies by
        // the delta instead of the line's new total (whose original units are already off the
        // shelf). Never attached to the context — ConsumeInventoryAsync only reads Id/MenuItemId/
        // Qty off it, and the real line's Id is what the ledger rows must point at.
        var addedUnits = new OrderItem { Id = line.Id, MenuItemId = line.MenuItemId, Name = line.Name, Qty = extraQty };
        await ConsumeInventoryAsync(db, menu, [addedUnits], orderId, explicitTenantId: null,
            reason: TopUpDeductionReason, skipAlreadyDeductedCheck: true);
    }

    public async Task ConsumeInventoryAsync(CafePosDbContext db, Dictionary<int, MenuItem> menu, List<OrderItem> items, int orderId,
        int? explicitTenantId = null, string? reason = null, bool skipAlreadyDeductedCheck = false)
    {
        // Idempotency pre-check — skip any line already deducted (e.g. a retried Fire
        // call). The DB unique index on (OrderItemId, InventoryItemId) WHERE Type='Sale'
        // is the hard backstop for a genuine concurrent double-fire; this is just the
        // happy-path guard that avoids hitting it in the common case.
        // Bypassed for a quantity top-up, where the line having been deducted before is precisely
        // the expected state rather than a sign of a retry (see ConsumeInventoryForAddedUnitsAsync).
        var candidateItemIds = skipAlreadyDeductedCheck ? [] : items.Select(i => i.Id).Where(id => id != 0).ToList();
        if (candidateItemIds.Count > 0)
        {
            var alreadyDeducted = await TenantScoped(db.InventoryTransactions, explicitTenantId)
                .Where(t => t.Type == InventoryTransactionType.Sale && t.OrderItemId != null && candidateItemIds.Contains(t.OrderItemId!.Value))
                .Select(t => t.OrderItemId!.Value)
                .Distinct()
                .ToListAsync();
            if (alreadyDeducted.Count > 0)
                items = items.Where(i => !alreadyDeducted.Contains(i.Id)).ToList();
        }
        if (items.Count == 0) return;

        var lineMenuItemIds = items.Select(i => i.MenuItemId).ToHashSet();
        var preparedMenuItemIds = menu.Values
            .Where(m => lineMenuItemIds.Contains(m.Id) && m.ProductType == ProductType.Prepared)
            .Select(m => m.Id)
            .ToList();

        var recipes = await TenantScoped(db.Recipes, explicitTenantId)
            .Include(r => r.Items)
            .Where(r => preparedMenuItemIds.Contains(r.MenuItemId))
            .ToListAsync();
        var recipeByMenuItem = recipes.ToDictionary(r => r.MenuItemId);

        var inventoryIds = recipes.SelectMany(r => r.Items).Select(ri => ri.InventoryItemId).ToHashSet();
        foreach (var m in menu.Values)
            if (m.ProductType == ProductType.Independent && m.LinkedInventoryItemId is int linkedId)
                inventoryIds.Add(linkedId);

        // Lock every ingredient this fire touches BEFORE their balances are read below — see
        // InventoryBatchService.LockIngredientsAsync. Two orders sharing an ingredient would
        // otherwise both start from the same balance and the second save would overwrite the
        // first's deduction. Taking the whole set in one ordered statement (rather than one
        // lock per Deduct call) also stops two concurrent fires with overlapping ingredients
        // from grabbing them in opposite orders and deadlocking.
        await InventoryBatchService.LockIngredientsAsync(db, inventoryIds);

        var inventory = await TenantScoped(db.InventoryItems, explicitTenantId)
            .Where(i => inventoryIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id);

        // Every batch this fire could draw from, in one query, instead of one query per
        // (line x ingredient) inside ConsumeFifoAsync — see LoadConsumableBatchesAsync for why
        // sharing one load across the whole unit of work is equivalent to re-querying each time.
        var batchesByIngredient = await InventoryBatchService.LoadConsumableBatchesAsync(db, inventoryIds);

        // An ingredient with no consumable batches passes an EMPTY group rather than null: null
        // means "nothing preloaded, go and query", and querying would only confirm the emptiness
        // the bulk load already established.
        Task Deduct(InventoryItem ingredient, double amount, int orderItemId) =>
            InventoryBatchService.ConsumeFifoAsync(db, ingredient, amount, InventoryTransactionType.Sale,
                orderId.ToString(), orderItemId, reason, wasteReasonCode: null, userId: null, userName: "System",
                preloadedBatches: batchesByIngredient.GetValueOrDefault(ingredient.Id) ?? []);

        // Prefetched once, in one query, for every prepared menu item ON THIS FIRE that has no
        // recipe — this used to be a FirstOrDefaultAsync per LINE inside the loop below, so a
        // cafe that simply hasn't set up recipes yet (every prepared item "missing") paid one
        // query per line on every single Fire for a lookup the loop asks the same handful of
        // times over. Keyed by menu item id and mutated in place as TrackMissingRecipe runs, so
        // two lines on the same fire sharing a never-before-seen menu item bump the same
        // in-memory alert's OccurrenceCount instead of the old per-line query path's blind spot:
        // its second FirstOrDefaultAsync call couldn't see the first call's still-unsaved Add
        // and would have inserted a second alert row for the same menu item.
        var missingRecipeMenuItemIds = preparedMenuItemIds.Where(id => !recipeByMenuItem.ContainsKey(id)).ToHashSet();
        var missingRecipeAlerts = missingRecipeMenuItemIds.Count > 0
            ? await TenantScoped(db.MissingRecipeAlerts, explicitTenantId)
                .Where(a => missingRecipeMenuItemIds.Contains(a.MenuItemId))
                .ToDictionaryAsync(a => a.MenuItemId)
            : new Dictionary<int, MissingRecipeAlert>();

        void TrackMissingRecipe(MenuItem menuItem)
        {
            logger.LogInformation("No recipe defined for menu item {MenuItemName} (id {MenuItemId}) — order {OrderId} deducted no ingredients for this line.", menuItem.Name, menuItem.Id, orderId);
            if (missingRecipeAlerts.TryGetValue(menuItem.Id, out var alert))
            {
                alert.OccurrenceCount++;
                alert.LastOccurredAt = DateTime.UtcNow;
                alert.Dismissed = false; // a resurfaced gap should reappear even if previously dismissed
            }
            else
            {
                var newAlert = new MissingRecipeAlert { MenuItemId = menuItem.Id };
                if (explicitTenantId is int tid) newAlert.TenantId = tid;
                db.MissingRecipeAlerts.Add(newAlert);
                missingRecipeAlerts[menuItem.Id] = newAlert;
            }
        }

        foreach (var line in items)
        {
            if (!menu.TryGetValue(line.MenuItemId, out var menuItem)) continue;

            if (menuItem.ProductType == ProductType.Independent)
            {
                if (menuItem.LinkedInventoryItemId is int linkedId && inventory.TryGetValue(linkedId, out var linked))
                    await Deduct(linked, line.Qty, line.Id);
                continue;
            }

            if (!recipeByMenuItem.TryGetValue(menuItem.Id, out var recipe))
            {
                TrackMissingRecipe(menuItem);
                continue;
            }

            // One Deduct per DISTINCT ingredient, not per recipe row. A recipe listing the
            // same ingredient on two rows (the Recipe Builder never prevented it) would
            // otherwise call ConsumeFifoAsync twice for the same (line, ingredient) — the
            // second call re-draws the same batch (its SQL filter sees the database's
            // quantities, not the first call's in-memory drain) and collides with the Sale
            // idempotency index, failing the whole fire/confirm.
            var amountByIngredientId = new Dictionary<int, double>();
            foreach (var recipeItem in recipe.Items)
            {
                if (!inventory.TryGetValue(recipeItem.InventoryItemId, out var ingredient)) continue;
                var amount = UnitConverter.Convert(recipeItem.Quantity * line.Qty, recipeItem.Unit, ingredient.Unit);
                amountByIngredientId[ingredient.Id] = amountByIngredientId.GetValueOrDefault(ingredient.Id) + amount;
            }
            foreach (var (ingredientId, amount) in amountByIngredientId)
                await Deduct(inventory[ingredientId], amount, line.Id);
        }
    }
}
