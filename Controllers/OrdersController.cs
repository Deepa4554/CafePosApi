using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

[ApiController]
[Route("api/orders")]
public class OrdersController(
    CafePosDbContext db, IAuditService audit, QrTokenService qrTokens, ReceiptTokenService receiptTokens,
    ITaxRateCache taxRateCache, ITenantContext tenantContext, IOrderBuildingService orderBuilder) : ControllerBase
{
    private static readonly OrderStatus[] StatusFlow =
        [OrderStatus.New, OrderStatus.Read, OrderStatus.Preparing, OrderStatus.Ready, OrderStatus.Served];

    [HttpGet]
    public async Task<PagedResult<OrderDto>> List(
        [FromQuery] bool activeOnly = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] int? branchId = null,
        // Calendar-day range (yyyy-MM-dd), inclusive on both ends — same "from"/"to"
        // shape as DashboardController.Analytics, for the same reason: a caller that
        // needs "everything today" (e.g. the Billing screen's revenue/transaction
        // count) must not silently lose orders past the default pageSize once a busy
        // day pushes the count past it.
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        // KDS-only: exclude "Open" orders that exist but have never been fired (all items
        // FireBatch == 0). Additive, so `activeOnly`'s meaning is unchanged — the Tables
        // screen still counts unfired Open orders as active/occupied.
        [FromQuery] bool kdsReady = false)
    {
        var query = db.Orders.Include(o => o.Items).Include(o => o.FireBatches).AsQueryable();
        // "Active" means still needs attention — matches the table-occupancy rule:
        // an order stays active (visible on KDS, counted as in-progress) until it's
        // BOTH paid AND served. Paying early must not make it vanish from the
        // kitchen's ticket list while the food still hasn't gone out.
        if (activeOnly) query = query.Where(o => !o.Cancelled && (!o.Paid || o.Status != OrderStatus.Served));
        if (kdsReady) query = query.Where(o => o.Items.Any(i => i.FireBatch > 0));
        // No branch selected -> see everything (single-location cafes, and cafes that
        // haven't set up branches yet, are unaffected). A branch selected -> only that
        // branch's orders; pre-branch-scoping orders (BranchId null) intentionally drop
        // out of a branch-filtered view since they can't be attributed to one.
        if (branchId is int bid) query = query.Where(o => o.BranchId == bid);
        if (from is not null) query = query.Where(o => o.CreatedAt >= from.Value.ToDateTime(TimeOnly.MinValue));
        if (to is not null) query = query.Where(o => o.CreatedAt < to.Value.ToDateTime(TimeOnly.MinValue).AddDays(1));

        var paged = await query.OrderByDescending(o => o.CreatedAt).ToPagedResultAsync(page, pageSize);
        return new PagedResult<OrderDto>(paged.Items.Select(OrderDto.From).ToList(), paged.Page, paged.PageSize, paged.TotalCount);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<OrderDto>> Get(int id)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.FireBatches).FirstOrDefaultAsync(o => o.Id == id);
        return order is null ? NotFound() : OrderDto.From(order);
    }

    /// <summary>
    /// Real math on real order history for the KDS "busy period ahead" card — not AI, just
    /// an average order count per DaypartBuckets slot over the last 14 days, compared
    /// against the upcoming slot. Deliberately its own free-tier endpoint rather than
    /// reusing DashboardController's peak-hours chart, which is Plus-gated; KDS (and the
    /// Chef/KitchenStaff logins that only see KDS) needs this on every plan.
    /// </summary>
    [HttpGet("rush-forecast")]
    public async Task<RushForecastDto> RushForecast()
    {
        const int historyDays = 14;
        const int minHistoryDays = 3;
        var since = DateTime.UtcNow.AddDays(-historyDays);

        // Cafe local time (Asia/Kolkata, IST = UTC+5:30, no DST) — CreatedAt is stored UTC,
        // and "which part of the day" only means anything in the cafe's own clock.
        var createdAtIst = await db.Orders
            .Where(o => o.Paid && o.CreatedAt >= since)
            .Select(o => o.CreatedAt.AddHours(5.5))
            .ToListAsync();

        var daysWithData = createdAtIst.Select(d => d.Date).Distinct().Count();
        if (daysWithData < minHistoryDays)
            return new RushForecastDto(false, false, null, null);

        var buckets = DaypartBuckets.All;
        var avgCounts = buckets
            .Select(b => createdAtIst.Count(d => d.Hour >= b.StartHour && d.Hour < b.EndHour) / (double)daysWithData)
            .ToList();

        var nowIst = DateTime.UtcNow.AddHours(5.5);
        var currentIdx = Array.FindIndex(buckets, b => nowIst.Hour >= b.StartHour && nowIst.Hour < b.EndHour);
        var currentAvg = currentIdx >= 0 ? avgCounts[currentIdx] : 0;
        var nextIdx = Array.FindIndex(buckets, b => b.StartHour > nowIst.Hour);

        if (nextIdx < 0)
            return new RushForecastDto(true, false, null, null); // last daypart of the day has already started

        var nextAvg = avgCounts[nextIdx];
        // "Meaningfully busier" — at least 2 orders/day average (so a single noisy day
        // doesn't trigger it) and at least 30% above the current slot.
        var rushExpected = nextAvg >= 2 && nextAvg > currentAvg * 1.3;

        return new RushForecastDto(true, rushExpected, buckets[nextIdx].DisplayLabel, Math.Round(nextAvg, 1));
    }

    /// <summary>The token half of the WhatsApp bill-PDF link — pair with
    /// "{apiBaseUrl}/api/public/receipt/{token}" client-side to get the full shareable
    /// URL. Kept as just the token (not the full URL) so the API never has to know or
    /// guess its own public base address.</summary>
    [HttpGet("{id:int}/receipt-token")]
    public async Task<ActionResult<object>> GetReceiptToken(int id)
    {
        var exists = await db.Orders.AnyAsync(o => o.Id == id);
        if (!exists) return NotFound();
        return new { token = receiptTokens.Encode(id) };
    }

    /// <summary>
    /// Fires an order. The server is the source of truth: it prices items from the
    /// menu, applies tax from settings and any coupon, consumes inventory, and
    /// records the guest's CRM visit + favorite items — the client only says what
    /// was ordered.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<OrderDto>> Create(CreateOrderRequest req)
    {
        // Mandatory so every order can be matched to a Customer by phone (see
        // FindOrCreateCustomerAsync) instead of by name, which two different guests can
        // share and one guest can spell inconsistently across visits. Enforced here (the
        // staff POS path) only — CreatePublic (anonymous QR self-ordering) has no phone
        // field on its request DTO yet and is deliberately left unaffected.
        var normalizedPhone = string.IsNullOrWhiteSpace(req.GuestPhone) ? null : new string(req.GuestPhone.Where(char.IsDigit).ToArray());
        if (normalizedPhone is null || normalizedPhone.Length != 10)
            throw new ApiValidationException("A valid 10-digit guest mobile number is required.");

        // Creates the order in the "Open" state (persisted, table occupied) WITHOUT firing
        // it to the kitchen — the POS calls POST /orders/{id}/fire as an explicit second
        // step (or "Hold Order" skips firing). This is what separates ordering from kitchen
        // dispatch: an order can exist and be edited before any item is sent to the line.
        var order = await orderBuilder.BuildOrderAsync(db, req.OrderType, req.TableCode, req.GuestName, req.Items, req.DiscountPct, User, branchId: req.BranchId, guestPhone: normalizedPhone, servedByStaffId: req.ServedByStaffId);
        return CreatedAtAction(nameof(Get), new { id = order.Id }, OrderDto.From(order));
    }

    /// <summary>
    /// Customer self-ordering from the QR table page (see PublicOrderPageController) — no
    /// staff login. The encrypted token in the route IS the tenant+table signal (there's
    /// no JWT to derive one from, and the table is never trusted from client input —
    /// see CreatePublicOrderRequest) — decoded here and threaded through BuildOrderAsync
    /// so every lookup/insert lands on the scanned cafe/table, not whatever the default
    /// tenant is. Always dine-in, and never carries a discount or coupon: those stay a
    /// deliberate staff action from the POS, not something a QR guest can apply to
    /// themselves.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("public/{token}")]
    public async Task<ActionResult<OrderDto>> CreatePublic(string token, CreatePublicOrderRequest req)
    {
        var decoded = qrTokens.TryDecode(token);
        if (decoded is null)
            throw new ApiValidationException("This ordering link is invalid. Please re-scan the QR code.");
        var (tenantId, tableCode) = decoded.Value;

        // Same mandatory-phone rule as the staff POS path (see Create above) — matches
        // orders to a Customer by phone, and now required here too so QR self-orders
        // aren't the one path that skips CRM matching entirely.
        var normalizedPhone = string.IsNullOrWhiteSpace(req.GuestPhone) ? null : new string(req.GuestPhone.Where(char.IsDigit).ToArray());
        if (normalizedPhone is null || normalizedPhone.Length != 10)
            throw new ApiValidationException("A valid 10-digit mobile number is required.");

        // An empty table code is the generic "menu only" QR (see
        // TablesController.GetMenuOnlyQrToken) — browsing only, by design: there's no
        // seat for the kitchen to deliver to and no staff member watching that QR the
        // way they would a table, so self-ordering from it is rejected outright rather
        // than silently becoming an untracked takeaway order. CustomerOrderPage already
        // hides the Add/cart UI entirely for this case; this is the server-side backstop.
        if (string.IsNullOrEmpty(tableCode))
            throw new ApiValidationException("This code is for browsing the menu only. Please order from your table's QR code.");

        var order = await orderBuilder.BuildOrderAsync(db, "DINE_IN", tableCode, req.GuestName, req.Items, discountPct: 0, user: null, explicitTenantId: tenantId, guestPhone: normalizedPhone);

        // A QR-self-ordering guest has no POS to come back and "fire" from — so unlike the
        // staff path (which fires as an explicit second step), a public order auto-fires
        // immediately on creation, keeping it a single atomic create-and-send action.
        try
        {
            await orderBuilder.FireUnfiredItemsAsync(db, order, tenantId);
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            throw new ApiConflictException("This order was already placed — please try again.");
        }

        return CreatedAtAction(nameof(Get), new { id = order.Id }, OrderDto.From(order));
    }

    /// <summary>Moves a given number of ONE line's units one stage forward (New→Read→
    /// Preparing→Ready→Served) — the partial-quantity primitive. "Chowmein ×6" line se sirf
    /// 3 units Preparing pe le jaao, baaki pending. fromStage omit karo to line ki current
    /// least-progressed stage se; qty omit karo to us stage ke saare units.</summary>
    [HttpPatch("{id:int}/items/{itemId:int}/advance-units")]
    public async Task<ActionResult<OrderDto>> AdvanceUnitsEndpoint(int id, int itemId, AdvanceUnitsRequest req)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.FireBatches).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Paid) throw new ApiConflictException("Order is already paid.");
        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null) return NotFound();
        if (item.FireBatch == 0) throw new ApiValidationException("Item hasn't been fired to the kitchen yet.");

        var fromStage = ResolveFromStage(item, req.FromStage);
        if (fromStage is null) throw new ApiValidationException("Nothing to advance on this item.");
        var qty = req.Qty ?? UnitsAtStage(item, fromStage.Value);
        AdvanceUnits(item, fromStage.Value, qty);
        orderBuilder.RecomputeBatchStatus(db, order, item.FireBatch);
        OrderBuildingService.RecomputeOrderStatus(order);

        await db.SaveChangesAsync();
        return OrderDto.From(order);
    }

    /// <summary>Advance-all: moves every not-yet-Served unit in ONE fire batch (KOT) one stage
    /// forward at once — the KDS "whole KOT" action and the Tables screen's "Advance All".
    /// Every other KOT on the order is untouched.</summary>
    [HttpPatch("{id:int}/advance/{batchNumber:int}")]
    public async Task<ActionResult<OrderDto>> Advance(int id, int batchNumber)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.FireBatches).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Paid) throw new ApiConflictException("Order is already paid.");
        var batch = order.FireBatches.FirstOrDefault(b => b.BatchNumber == batchNumber);
        if (batch is null) return NotFound();

        // Each line's least-progressed units move one stage — repeated taps march the whole
        // KOT forward. (A line already partly ahead keeps its ahead units where they are.)
        foreach (var item in order.Items.Where(i => i.FireBatch == batchNumber))
        {
            var fromStage = ResolveFromStage(item, null);
            if (fromStage is not null) AdvanceUnits(item, fromStage.Value, UnitsAtStage(item, fromStage.Value));
        }
        orderBuilder.RecomputeBatchStatus(db, order, batchNumber);
        OrderBuildingService.RecomputeOrderStatus(order);

        await db.SaveChangesAsync();
        return OrderDto.From(order);
    }

    /// <summary>Production View bulk action: advance `Qty` units of one dish (menuItemId) from
    /// one stage across MANY KOTs at once. `Allocations` diye to exactly wahi lines/quantities;
    /// warna `Qty` ko us dish ki us-stage-wali saari fired lines pe FIFO (oldest KOT first)
    /// allocate karta hai. Baaki qty pending rehti.</summary>
    [HttpPost("kds/bulk-advance")]
    public async Task<ActionResult<IEnumerable<OrderDto>>> BulkAdvance(BulkAdvanceRequest req)
    {
        if (!Enum.TryParse<OrderStatus>(req.FromStage, ignoreCase: true, out var fromStage))
            throw new ApiValidationException($"Unknown stage '{req.FromStage}'.");
        if (fromStage == OrderStatus.Served) throw new ApiValidationException("Served units can't advance further.");

        var touchedOrderIds = new HashSet<int>();

        if (req.Allocations is { Count: > 0 })
        {
            // Manual allocation — exact lines + quantities the chef picked.
            foreach (var alloc in req.Allocations)
            {
                var order = await db.Orders.Include(o => o.Items).Include(o => o.FireBatches).FirstOrDefaultAsync(o => o.Id == alloc.OrderId);
                var item = order?.Items.FirstOrDefault(i => i.Id == alloc.ItemId);
                if (order is null || item is null || order.Paid) continue;
                AdvanceUnits(item, fromStage, alloc.Qty);
                orderBuilder.RecomputeBatchStatus(db, order, item.FireBatch);
                OrderBuildingService.RecomputeOrderStatus(order);
                touchedOrderIds.Add(order.Id);
            }
        }
        else
        {
            // FIFO — spread req.Qty across this dish's lines, oldest KOT first.
            var remaining = req.Qty ?? 0;
            if (remaining <= 0) throw new ApiValidationException("Enter a quantity or pick specific KOTs.");
            var candidates = await db.Orders
                .Include(o => o.Items).Include(o => o.FireBatches)
                .Where(o => !o.Paid && o.Items.Any(i => i.MenuItemId == req.MenuItemId && i.FireBatch > 0))
                .ToListAsync();
            // Oldest fire batch first (FIFO), then order id for a stable tie-break.
            var lines = candidates
                .SelectMany(o => o.Items.Where(i => i.MenuItemId == req.MenuItemId && i.FireBatch > 0).Select(i => new { Order = o, Item = i }))
                .Select(x => new { x.Order, x.Item, Fired = x.Order.FireBatches.FirstOrDefault(b => b.BatchNumber == x.Item.FireBatch)?.FiredAt ?? DateTime.MaxValue })
                .OrderBy(x => x.Fired).ThenBy(x => x.Order.Id)
                .ToList();
            foreach (var line in lines)
            {
                if (remaining <= 0) break;
                var take = Math.Min(remaining, UnitsAtStage(line.Item, fromStage));
                if (take <= 0) continue;
                AdvanceUnits(line.Item, fromStage, take);
                orderBuilder.RecomputeBatchStatus(db, line.Order, line.Item.FireBatch);
                OrderBuildingService.RecomputeOrderStatus(line.Order);
                touchedOrderIds.Add(line.Order.Id);
                remaining -= take;
            }
        }

        await db.SaveChangesAsync();
        // Return just the affected orders so the client can patch its cache.
        var affected = await db.Orders.Include(o => o.Items).Include(o => o.FireBatches)
            .Where(o => touchedOrderIds.Contains(o.Id)).ToListAsync();
        return affected.Select(OrderDto.From).ToList();
    }

    /// <summary>Legacy manual override — sets Order.Status directly, bypassing the
    /// per-batch model entirely. Not called from any current UI (Advance/Fire drive
    /// everything now); left as a rough admin/debugging escape hatch. Note the value set
    /// here doesn't persist past the next batch change, since Order.Status is recomputed as
    /// a rollup of OrderFireBatches whenever any batch advances or a new one fires.</summary>
    [HttpPatch("{id:int}/status")]
    public async Task<ActionResult<OrderDto>> SetStatus(int id, SetStatusRequest req)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.FireBatches).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();

        if (!Enum.TryParse<OrderStatus>(req.Status, ignoreCase: true, out var status))
            throw new ApiValidationException($"Unknown status '{req.Status}'.");

        order.Status = status;
        await db.SaveChangesAsync();
        return OrderDto.From(order);
    }

    /// <summary>How many units of a line are CURRENTLY at a given stage (non-cumulative).</summary>
    private static int UnitsAtStage(OrderItem item, OrderStatus stage) => stage switch
    {
        OrderStatus.New => item.NewQty,
        OrderStatus.Read => item.ReadQty,
        OrderStatus.Preparing => item.PreparingQty,
        OrderStatus.Ready => item.ReadyQty,
        OrderStatus.Served => item.ServedQty,
        _ => 0,
    };

    /// <summary>The line's least-progressed stage that still has ≥1 unit, or null if every
    /// unit is Served. Used when the caller doesn't name an explicit from-stage.</summary>
    private static OrderStatus? ResolveFromStage(OrderItem item, string? explicitStage)
    {
        if (!string.IsNullOrWhiteSpace(explicitStage) && Enum.TryParse<OrderStatus>(explicitStage, ignoreCase: true, out var s))
            return UnitsAtStage(item, s) > 0 ? s : null;
        foreach (var stage in StatusFlow)
            if (stage != OrderStatus.Served && UnitsAtStage(item, stage) > 0) return stage;
        return null;
    }

    /// <summary>Moves min(qty, unitsAt(fromStage)) units of a line one stage forward, then
    /// refreshes the line's derived Status. Forward-only; the non-cumulative stage counters
    /// always keep summing (with NewQty) to Qty.</summary>
    private static void AdvanceUnits(OrderItem item, OrderStatus fromStage, int qty)
    {
        var n = Math.Min(Math.Max(qty, 0), UnitsAtStage(item, fromStage));
        if (n == 0) return;
        switch (fromStage)
        {
            case OrderStatus.New: item.ReadQty += n; break;                             // New→Read (NewQty derived, shrinks)
            case OrderStatus.Read: item.ReadQty -= n; item.PreparingQty += n; break;    // Read→Preparing
            case OrderStatus.Preparing: item.PreparingQty -= n; item.ReadyQty += n; break; // Preparing→Ready
            case OrderStatus.Ready: item.ReadyQty -= n; item.ServedQty += n; break;     // Ready→Served
        }
        RecomputeItemStatus(item);
    }

    /// <summary>Derived overall stage for a line = least-progressed stage with ≥1 unit, or
    /// Served once every unit is served.</summary>
    private static void RecomputeItemStatus(OrderItem item)
    {
        foreach (var stage in StatusFlow)
            if (UnitsAtStage(item, stage) > 0) { item.Status = stage; return; }
        item.Status = OrderStatus.Served;
    }

    /// <summary>Fires all not-yet-fired items on an existing order to the kitchen as their
    /// own new fire batch. The separate "dispatch" step that the staff POS calls right after
    /// Create (or later, for a held order) — distinct from ordering so items can be
    /// added/edited before hitting the line. Order.Status is recomputed as a rollup (see
    /// OrderBuildingService.RecomputeOrderStatus) — any other batch already
    /// Preparing/Ready/Served on this order keeps its own status, untouched.</summary>
    [HttpPost("{id:int}/fire")]
    public async Task<ActionResult<OrderDto>> Fire(int id)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.FireBatches).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Paid) throw new ApiConflictException("Order is already paid.");
        try
        {
            if (!await orderBuilder.FireUnfiredItemsAsync(db, order, explicitTenantId: null))
                throw new ApiValidationException("No new items to fire.");
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            throw new ApiConflictException("This order was already fired — refresh and try again.");
        }
        return OrderDto.From(order);
    }

    /// <summary>Adds one item to an existing, not-yet-paid order (new item starts unfired,
    /// FireBatch 0, so it only reaches the kitchen — and only then deducts inventory — as
    /// its own new fire batch, see FireUnfiredItemsAsync — on the next Fire). Allowed at any
    /// stage, even after every existing batch has been Served (e.g. the table asks for one
    /// more item at the billing stage): adding the item doesn't touch any existing
    /// OrderFireBatch, so whatever's already Preparing/Ready/Served keeps its own status
    /// undisturbed — only once this item is fired does it become its own separate ticket.
    /// The bill still totals every item together regardless of which fire round it came from.</summary>
    [HttpPost("{id:int}/items")]
    public async Task<ActionResult<OrderDto>> AddItem(int id, AddOrderItemRequest req)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.FireBatches).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Paid) throw new ApiConflictException("Cannot modify a paid order.");
        if (req.Qty <= 0) throw new ApiValidationException("Quantity must be a positive number.");

        var menuItem = await db.MenuItems.FirstOrDefaultAsync(m => m.Id == req.MenuItemId);
        if (menuItem is null) throw new ApiValidationException("Menu item not found.");
        if (!menuItem.Available) throw new ApiValidationException($"{menuItem.Name} is currently unavailable.");

        var newItem = new OrderItem
        {
            OrderId = order.Id,
            MenuItemId = menuItem.Id,
            Name = menuItem.Name,
            Qty = req.Qty,
            Price = menuItem.Price,
            Modifier = req.Modifier,
            FireBatch = 0,
        };
        order.Items.Add(newItem);
        order.Subtotal = order.Items.Where(i => !i.Voided).Sum(i => i.Price * i.Qty);
        OrderBuildingService.RecomputeTotals(order, await GetTaxRatePctAsync());

        await db.SaveChangesAsync();
        return OrderDto.From(order);
    }

    /// <summary>Removes/voids an item from a not-yet-paid order.
    /// Unfired (FireBatch == 0): freely hard-deleted — nothing was ever deducted, even if
    /// other items on the same order are already Served (this one never reached the kitchen).
    /// Fired: only an Owner/Manager may pull it back, only if it hasn't been Served, and it's
    /// VOIDED rather than deleted (see VoidItemAsync) so KOT/ledger history survives. Still
    /// New/Read (prep hasn't started) reverses its inventory deduction automatically; once
    /// Preparing/Ready a reason is required and stock is NOT reversed (food is genuinely
    /// spent) — matches the doc's "void before cooking vs void with wastage" rule.</summary>
    [HttpDelete("{id:int}/items/{itemId:int}")]
    public async Task<ActionResult<OrderDto>> RemoveItem(int id, int itemId, [FromQuery] string? reason = null)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.FireBatches).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Paid) throw new ApiConflictException("Cannot modify a paid order.");

        var item = order.Items.FirstOrDefault(i => i.Id == itemId && !i.Voided);
        if (item is null) return NotFound();
        if (order.Items.Count(i => !i.Voided) == 1) throw new ApiValidationException("Order must contain at least one item.");
        if (item.FireBatch > 0)
        {
            if (!IsOwnerOrManager()) return Forbid();
            if (item.Status == OrderStatus.Served) throw new ApiConflictException("Cannot remove an item that's already been served.");
            if (item.Status is OrderStatus.Preparing or OrderStatus.Ready && string.IsNullOrWhiteSpace(reason))
                throw new ApiValidationException("A reason is required to void an item that's already in preparation.");
        }

        await VoidItemAsync(order, item, reason);
        order.Subtotal = order.Items.Where(i => !i.Voided).Sum(i => i.Price * i.Qty);
        OrderBuildingService.RecomputeTotals(order, await GetTaxRatePctAsync());
        // Pulling/voiding a fired item can shift its batch's rollup (e.g. removing the last
        // still-New item leaves the batch all-Ready) — and the order status with it.
        if (item.FireBatch > 0)
        {
            orderBuilder.RecomputeBatchStatus(db, order, item.FireBatch);
            OrderBuildingService.RecomputeOrderStatus(order);
        }
        await db.SaveChangesAsync();
        return OrderDto.From(order);
    }

    /// <summary>Cancels the whole order — voids every not-yet-served line via the same
    /// before-cook-reverses/after-cook-doesn't rule as RemoveItem (see VoidItemAsync).
    /// Already-served items are left untouched (food that's out can't be un-served); staff
    /// still Pay/Refund normally for whatever was actually served. Requires Owner/Manager if
    /// any item has already been served — a floor waiter can freely cancel a still-New order,
    /// but walking back served food needs a manager's say-so.</summary>
    [HttpPost("{id:int}/cancel")]
    public async Task<ActionResult<OrderDto>> Cancel(int id, CancelOrderRequest req)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.FireBatches).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Paid) throw new ApiConflictException("Cannot cancel a paid order — use Refund instead.");
        if (order.Cancelled) throw new ApiConflictException("Order is already cancelled.");

        var hasServedItems = order.Items.Any(i => !i.Voided && i.Status == OrderStatus.Served);
        if (hasServedItems && !IsOwnerOrManager()) return Forbid();

        foreach (var item in order.Items.Where(i => !i.Voided && i.Status != OrderStatus.Served).ToList())
            await VoidItemAsync(order, item, req.Reason);

        order.Cancelled = true;
        order.CancelledAt = DateTime.UtcNow;
        order.CancelReason = req.Reason;
        order.Subtotal = order.Items.Where(i => !i.Voided).Sum(i => i.Price * i.Qty);
        OrderBuildingService.RecomputeTotals(order, await GetTaxRatePctAsync());
        foreach (var batch in order.FireBatches)
            orderBuilder.RecomputeBatchStatus(db, order, batch.BatchNumber);
        OrderBuildingService.RecomputeOrderStatus(order);

        await db.SaveChangesAsync();
        await audit.LogAsync(AuditAction.Update, AuditResource.Order, order.Id.ToString(),
            $"Order {order.Id} cancelled. Reason: {req.Reason ?? "not specified"}.", AuditSeverity.Medium);
        return OrderDto.From(order);
    }

    /// <summary>Voids one line. Unfired (FireBatch==0): hard-deletes exactly as before fire-time
    /// deduction existed — nothing was ever deducted. Fired + New/Read (prep hasn't started):
    /// reverses the Sale deduction via new Return-type ledger rows keyed to this OrderItemId.
    /// Fired + Preparing/Ready: flags Voided with NO reversal (wastage) and audits it. Served
    /// is expected to already be rejected by the caller before this runs.</summary>
    private async Task VoidItemAsync(Order order, OrderItem item, string? reason)
    {
        if (item.FireBatch == 0)
        {
            order.Items.Remove(item);
            db.OrderItems.Remove(item);
            return;
        }

        item.Voided = true;
        item.VoidedAt = DateTime.UtcNow;
        item.VoidReason = reason;

        if (item.Status is OrderStatus.New or OrderStatus.Read)
        {
            var deductions = await db.InventoryTransactions
                .Where(t => t.OrderItemId == item.Id && t.Type == InventoryTransactionType.Sale)
                .ToListAsync();
            foreach (var d in deductions)
            {
                var ingredient = await db.InventoryItems.FindAsync(d.InventoryItemId);
                if (ingredient is null) continue;
                await InventoryBatchService.ReverseAsync(db, d, ingredient, "Void before prep",
                    CurrentUserId(), User.Identity?.Name ?? "Cafe Staff");
            }
        }
        else
        {
            await audit.LogAsync(AuditAction.InventoryChange, AuditResource.Order, order.Id.ToString(),
                $"Voided '{item.Name}' (order {order.Id}) after prep started — no stock reversal (wastage). Reason: {reason ?? "not specified"}.",
                AuditSeverity.Medium);
        }
    }

    private int? CurrentUserId()
    {
        var claim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }

    /// <summary>Manager-only markdown applied at the billing stage (order must be Served,
    /// not yet Paid). Kept as its own field, separate from the order-time DiscountAmount.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPatch("{id:int}/bill-discount")]
    public async Task<ActionResult<OrderDto>> ApplyBillDiscount(int id, BillDiscountRequest req)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.FireBatches).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Paid) throw new ApiConflictException("Cannot discount a paid order.");
        if (order.Status != OrderStatus.Served) throw new ApiConflictException("A bill discount can only be applied once the order has been served.");
        if ((req.Pct is null) == (req.Amount is null)) throw new ApiValidationException("Provide either a percentage or a flat amount, not both.");

        var amount = req.Amount ?? Math.Round(order.Subtotal * (req.Pct ?? 0) / 100, 2);
        if (amount < 0) throw new ApiValidationException("Discount cannot be negative.");

        order.BillDiscountAmount = amount;
        OrderBuildingService.RecomputeTotals(order, await GetTaxRatePctAsync());
        await db.SaveChangesAsync();
        await audit.LogAsync(AuditAction.Discount, AuditResource.Order, order.Id.ToString(),
            $"Bill discount of {amount:C} applied to order {order.Id}.", AuditSeverity.Medium);
        return OrderDto.From(order);
    }

    /// <summary>Redeems a coupon at billing time (order must be Served, not yet Paid). Only
    /// one coupon per order.</summary>
    [HttpPatch("{id:int}/bill-coupon")]
    public async Task<ActionResult<OrderDto>> ApplyBillCoupon(int id, BillCouponRequest req)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.FireBatches).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Paid) throw new ApiConflictException("Cannot apply a coupon to a paid order.");
        if (order.Status != OrderStatus.Served) throw new ApiConflictException("Coupons are applied at billing time, once the order has been served.");
        if (order.CouponCode is not null) throw new ApiConflictException("A coupon has already been applied to this order.");
        if (string.IsNullOrWhiteSpace(req.Code)) throw new ApiValidationException("Enter a coupon code.");

        var coupon = await db.Coupons.FirstOrDefaultAsync(c => c.Code == req.Code.ToUpperInvariant());
        if (coupon is null) throw new ApiValidationException("Coupon code is invalid or expired.");
        if (coupon.IsUsed) throw new ApiConflictException("Coupon has already been used.");
        if (coupon.ExpiresAt < DateTime.UtcNow) throw new ApiConflictException("Coupon has expired.");
        if (order.Subtotal < coupon.MinOrderValue) throw new ApiValidationException($"Minimum order value for this coupon is {coupon.MinOrderValue:C}.");

        order.CouponDiscountAmount = coupon.Type switch
        {
            CouponType.Percent => Math.Round(order.Subtotal * coupon.Value / 100, 2),
            CouponType.Flat => coupon.Value,
            _ => 0,
        };
        order.CouponCode = coupon.Code;
        coupon.IsUsed = true;
        OrderBuildingService.RecomputeTotals(order, await GetTaxRatePctAsync());
        await db.SaveChangesAsync();
        return OrderDto.From(order);
    }

    /// <summary>Redeems a gift card at billing time (order must be Served, not yet Paid).
    /// Debits only what this bill can absorb. Only one gift card per order.</summary>
    [HttpPatch("{id:int}/bill-giftcard")]
    public async Task<ActionResult<OrderDto>> ApplyBillGiftCard(int id, BillGiftCardRequest req)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.FireBatches).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Paid) throw new ApiConflictException("Cannot apply a gift card to a paid order.");
        if (order.Status != OrderStatus.Served) throw new ApiConflictException("Gift cards are applied at billing time, once the order has been served.");
        if (order.GiftCardCode is not null) throw new ApiConflictException("A gift card has already been applied to this order.");
        if (string.IsNullOrWhiteSpace(req.Code)) throw new ApiValidationException("Enter a gift card code.");

        var giftCard = await db.GiftCards.FirstOrDefaultAsync(g => g.Code == req.Code.ToUpperInvariant());
        if (giftCard is null) throw new ApiValidationException("Gift card code not found.");
        if (giftCard.Status != GiftCardStatus.Active) throw new ApiConflictException("Gift card is not active.");
        if (giftCard.ExpiresAt < DateTime.UtcNow) throw new ApiConflictException("Gift card has expired.");

        var owedBeforeGiftCard = Math.Max(0, order.Subtotal - order.DiscountAmount - order.BillDiscountAmount - order.CouponDiscountAmount);
        var redeem = Math.Min(giftCard.Balance, owedBeforeGiftCard);
        order.GiftCardCode = giftCard.Code;
        order.GiftCardAmountApplied = redeem;
        giftCard.Balance -= redeem;
        if (giftCard.Balance <= 0) giftCard.Status = GiftCardStatus.Used;
        OrderBuildingService.RecomputeTotals(order, await GetTaxRatePctAsync());
        await db.SaveChangesAsync();
        return OrderDto.From(order);
    }

    /// <summary>Tax rate for the current (staff/JWT) tenant — same cached source
    /// OrderBuildingService.BuildOrderAsync uses.</summary>
    private async Task<decimal> GetTaxRatePctAsync() =>
        await taxRateCache.GetTaxRatePctAsync(tenantContext.TenantIdOrDefault,
            async () => (await db.Settings.FirstAsync()).TaxRatePct);

    /// <summary>Inline Owner/Manager check for per-item conditional gating (mirrors the
    /// role check NotificationsController does in a method body).</summary>
    private bool IsOwnerOrManager() =>
        User.IsInRole(nameof(AppRole.Owner)) || User.IsInRole(nameof(AppRole.Manager));

    /// <summary>
    /// Marks the bill paid. Requires the order to be Served first — payment can never happen
    /// before the customer has been served (a core rule of the ordering→billing separation).
    /// Does NOT force Served on its own; a table frees up only once it's BOTH paid AND served
    /// (see TablesController/PublicController's occupancy check).
    /// </summary>
    [HttpPatch("{id:int}/pay")]
    public async Task<ActionResult<OrderDto>> Pay(int id, PayRequest? req = null)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.FireBatches).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Paid) throw new ApiConflictException("Order is already paid.");
        if (order.Status != OrderStatus.Served) throw new ApiValidationException("Order must be served before it can be marked paid.");

        order.Paid = true;
        order.PaymentMethod = string.IsNullOrWhiteSpace(req?.PaymentMethod) ? null : req.PaymentMethod.Trim();

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

        await db.SaveChangesAsync();
        return OrderDto.From(order);
    }

    /// <summary>Full or partial refund — financially sensitive, so unlike most of this
    /// controller it's explicitly restricted rather than relying on the auth fallback
    /// policy (any authenticated user) that everything else here uses.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("{id:int}/refund")]
    public async Task<ActionResult<OrderDto>> Refund(int id, RefundOrderRequest req)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.FireBatches).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (!order.Paid) throw new ApiValidationException("Only paid orders can be refunded.");
        if (order.Refunded) throw new ApiConflictException("Order has already been refunded.");

        var amount = req.Amount ?? order.Total;
        if (amount <= 0 || amount > order.Total)
            throw new ApiValidationException("Refund amount must be between 0 and the order total.");

        order.Refunded = true;
        order.RefundedAmount = amount;
        order.RefundReason = req.Reason;
        order.RefundedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await audit.LogAsync(AuditAction.Refund, AuditResource.Order, order.Id.ToString(),
            $"Refunded {amount:C} for order {order.Id}. Reason: {req.Reason ?? "not specified"}.", AuditSeverity.High);

        return OrderDto.From(order);
    }

}
