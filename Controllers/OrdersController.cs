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
    // REMOVED: Read stage - workflow simplified: New → Preparing → Ready → Served
    private static readonly OrderStatus[] StatusFlow =
        [OrderStatus.New, OrderStatus.Preparing, OrderStatus.Ready, OrderStatus.Served];

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
        [FromQuery] bool kdsReady = false,
        // Token Dashboard: "?orderType=QSR&activeOnly=true" is how it asks for just today's
        // counter orders, same active-order rule as every other order type.
        [FromQuery] string? orderType = null,
        // Staff-Confirm Mode: the floor polls this on a short interval to pop up "Table X —
        // confirm order?" the moment a guest submits one — see OrdersController.ConfirmGuestOrder.
        [FromQuery] bool? pendingConfirmation = null)
    {
        var query = db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).AsQueryable();
        // "Active" means still needs attention — matches the table-occupancy rule:
        // an order stays active (visible on KDS, counted as in-progress) until it's
        // BOTH paid AND served. Paying early must not make it vanish from the
        // kitchen's ticket list while the food still hasn't gone out.
        if (activeOnly) query = query.Where(o => !o.Cancelled && (!o.Paid || o.Status != OrderStatus.Served));
        if (kdsReady) query = query.Where(o => o.Items.Any(i => i.FireBatch > 0));
        // pendingConfirmation=true also excludes cancelled orders — a rejected (cancelled)
        // guest order that somehow got re-flagged must not reappear as an unconfirmable
        // card in the staff confirmation queue.
        if (pendingConfirmation is bool pc) query = query.Where(o => o.PendingStaffConfirmation == pc && (!pc || !o.Cancelled));
        if (orderType is not null) query = query.Where(o => o.OrderType == orderType);
        // No branch selected -> see everything (single-location cafes, and cafes that
        // haven't set up branches yet, are unaffected). A branch selected -> only that
        // branch's orders; pre-branch-scoping orders (BranchId null) intentionally drop
        // out of a branch-filtered view since they can't be attributed to one.
        if (branchId is int bid) query = query.Where(o => o.BranchId == bid);
        // from/to are IST calendar days (what the app's date pickers and "today" mean) —
        // shifted to UTC bounds for the stored-UTC CreatedAt, same rule as
        // DashboardController.Analytics (see IstClock).
        if (from is not null) query = query.Where(o => o.CreatedAt >= IstClock.IstDateStartUtc(from.Value));
        if (to is not null) query = query.Where(o => o.CreatedAt < IstClock.IstDateStartUtc(to.Value).AddDays(1));

        var paged = await query.OrderByDescending(o => o.CreatedAt).ToPagedResultAsync(page, pageSize);
        return new PagedResult<OrderDto>(paged.Items.Select(OrderDto.From).ToList(), paged.Page, paged.PageSize, paged.TotalCount);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<OrderDto>> Get(int id)
    {
        // .Include(o => o.Customer) only here (not List()) — it's what powers OrderDto's
        // CustomerAvailablePoints preview on the live checkout/bill screens; List() renders
        // grids of many orders at once and doesn't need the extra join.
        var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).Include(o => o.Customer).FirstOrDefaultAsync(o => o.Id == id);
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
        // field on its request DTO yet and is deliberately left unaffected. QSR counter
        // orders and Cash Sales are the staff-POS exceptions: a token number (QSR) or the
        // fact that it's paid on the spot (Cash) already identifies/settles the order, so
        // the phone stays genuinely optional instead of being forced just for CRM matching.
        var skipsMandatoryPhone = req.OrderType is "QSR" or "CASH";
        var normalizedPhone = string.IsNullOrWhiteSpace(req.GuestPhone) ? null : new string(req.GuestPhone.Where(char.IsDigit).ToArray());
        if (!skipsMandatoryPhone && (normalizedPhone is null || normalizedPhone.Length != 10))
            throw new ApiValidationException("A valid 10-digit guest mobile number is required.");
        if (skipsMandatoryPhone && normalizedPhone is not null && normalizedPhone.Length != 10)
            throw new ApiValidationException("Mobile number must be exactly 10 digits.");

        // Creates the order in the "Open" state (persisted, table occupied) WITHOUT firing
        // it to the kitchen — the POS calls POST /orders/{id}/fire as an explicit second
        // step (or "Hold Order" skips firing). This is what separates ordering from kitchen
        // dispatch: an order can exist and be edited before any item is sent to the line.
        var order = await orderBuilder.BuildOrderAsync(db, req.OrderType, req.TableCode, req.GuestName, req.Items, req.DiscountPct, User, branchId: req.BranchId, guestPhone: normalizedPhone, servedByStaffId: req.ServedByStaffId, guestAddress: req.GuestAddress);

        // A QSR counter order has no staff member coming back to press "Fire" — like the
        // guest-QR path, it goes straight to the kitchen the instant it's rung up.
        if (req.OrderType == "QSR")
        {
            await orderBuilder.FireUnfiredItemsAsync(db, order, null);
            await db.SaveChangesAsync();
        }
        // Cash Sale: no kitchen involved at all — fire (so the order still gets a real
        // fire-batch for the usual status bookkeeping/rollup) and immediately jump every
        // line straight to Served, so it's payable the instant it's rung up. This never
        // surfaces on the Kitchen Display: KDS already excludes any batch/item that's
        // already Served (see KDSScreen's `tickets`/`itemWiseKotGroups`/`prodGroups`), so
        // no extra order-type filtering is needed there.
        else if (req.OrderType == "CASH")
        {
            await orderBuilder.FireUnfiredItemsAsync(db, order, null);
            foreach (var item in order.Items) JumpToServed(item);
            foreach (var batch in order.FireBatches) orderBuilder.RecomputeBatchStatus(db, order, batch.BatchNumber);
            OrderBuildingService.RecomputeOrderStatus(order);
            await db.SaveChangesAsync();
        }

        return CreatedAtAction(nameof(Get), new { id = order.Id }, OrderDto.From(order));
    }

    /// <summary>
    /// Tombstone for the pre-session QR ordering endpoint. The QR page has ordered through
    /// GuestSessionController's session flow (scan → cart → order) since Phase 1 of the
    /// session plan — nothing current calls this. It used to create-and-fire in one shot,
    /// which bypassed Staff-Confirm Mode and left no GuestSession behind (permanently
    /// wedging the table's scan flow in STAFF_ASSIST), so it's kept only as a clear 410
    /// for any stale cached page rather than a confusing 404 or a silent back door.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("public/{token}")]
    public ActionResult CreatePublic(string token, CreatePublicOrderRequest req) =>
        new ObjectResult(new ProblemDetails
        {
            Status = StatusCodes.Status410Gone,
            Title = "This ordering page is out of date — please re-scan the QR code on your table.",
        })
        { StatusCode = StatusCodes.Status410Gone };

    /// <summary>Moves a given number of ONE line's units one stage forward (New→Read→
    /// Preparing→Ready→Served) — the partial-quantity primitive. "Chowmein ×6" line se sirf
    /// 3 units Preparing pe le jaao, baaki pending. fromStage omit karo to line ki current
    /// least-progressed stage se; qty omit karo to us stage ke saare units.</summary>
    [HttpPatch("{id:int}/items/{itemId:int}/advance-units")]
    public async Task<ActionResult<OrderDto>> AdvanceUnitsEndpoint(int id, int itemId, AdvanceUnitsRequest req)
    {
        var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        // Serving progress is independent of payment — a QSR counter may collect payment
        // well before anything's cooked/served (see OrdersController.Pay).
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
        var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        // Serving progress is independent of payment — see AdvanceUnitsEndpoint above.
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

    /// <summary>Jumps every not-yet-served unit of a line straight to Served, skipping
    /// whatever intermediate stage it's currently at — the QSR token flow's "tap the status
    /// to mark served" action has no use for the granular stage-by-stage KDS pipeline (see
    /// AdvanceUnits above), it just wants one line done in one tap.</summary>
    private static void JumpToServed(OrderItem item)
    {
        item.ServedQty = item.Qty;
        item.ReadQty = 0;
        item.PreparingQty = 0;
        item.ReadyQty = 0;
        RecomputeItemStatus(item);
    }

    /// <summary>QSR token flow: mark one line fully served in a single tap (no stage-by-stage
    /// stepping, no confirmation) — see JumpToServed.</summary>
    [HttpPatch("{id:int}/items/{itemId:int}/serve")]
    public async Task<ActionResult<OrderDto>> ServeItem(int id, int itemId)
    {
        var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        // Serving progress is independent of payment — see AdvanceUnitsEndpoint above.
        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null) return NotFound();
        if (item.FireBatch == 0) throw new ApiValidationException("Item hasn't been fired to the kitchen yet.");

        JumpToServed(item);
        orderBuilder.RecomputeBatchStatus(db, order, item.FireBatch);
        OrderBuildingService.RecomputeOrderStatus(order);

        await db.SaveChangesAsync();
        return OrderDto.From(order);
    }

    /// <summary>QSR token flow: "Mark All as Served" — jumps every fired line on the order
    /// straight to Served in one tap, across every KOT/fire-batch. See JumpToServed.</summary>
    [HttpPatch("{id:int}/serve-all")]
    public async Task<ActionResult<OrderDto>> ServeAll(int id)
    {
        var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        // Serving progress is independent of payment — see AdvanceUnitsEndpoint above.

        var touchedBatches = new HashSet<int>();
        foreach (var item in order.Items.Where(i => i.FireBatch != 0 && !i.Voided))
        {
            JumpToServed(item);
            touchedBatches.Add(item.FireBatch);
        }
        foreach (var batchNumber in touchedBatches)
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
                var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == alloc.OrderId);
                var item = order?.Items.FirstOrDefault(i => i.Id == alloc.ItemId);
                if (order is null || item is null) continue;
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
                .Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments)
                .Where(o => o.Items.Any(i => i.MenuItemId == req.MenuItemId && i.FireBatch > 0))
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
        var affected = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments)
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
        var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == id);
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
            case OrderStatus.New: item.PreparingQty += n; break;                           // New→Preparing (READ stage removed)
            case OrderStatus.Preparing: item.PreparingQty -= n; item.ReadyQty += n; break; // Preparing→Ready
            case OrderStatus.Ready: item.ReadyQty -= n; item.ServedQty += n; break;       // Ready→Served
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
        var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == id);
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
        var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Paid) throw new ApiConflictException("Cannot modify a paid order.");
        if (req.Qty <= 0) throw new ApiValidationException("Quantity must be a positive number.");

        var menuItem = await db.MenuItems.Include(m => m.Station).FirstOrDefaultAsync(m => m.Id == req.MenuItemId);
        if (menuItem is null) throw new ApiValidationException("Menu item not found.");
        if (!menuItem.Available) throw new ApiValidationException($"{menuItem.Name} is currently unavailable.");

        var (linePrice, variantName, selections, stationName, taxRatePct) = await orderBuilder.ResolveLinePricingAsync(db, menuItem, req.VariantId, req.ModifierOptionIds, explicitTenantId: null);
        var newItem = new OrderItem
        {
            OrderId = order.Id,
            MenuItemId = menuItem.Id,
            Name = menuItem.Name,
            Qty = req.Qty,
            Price = linePrice,
            Modifier = req.Modifier,
            VariantId = req.VariantId,
            VariantName = variantName,
            SelectedModifiers = selections,
            StationName = stationName,
            VegNonVegType = menuItem.VegNonVegType,
            TaxRatePct = taxRatePct,
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
        var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == id);
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

    /// <summary>Staff-Confirm Mode: the floor's "yes, this table is real" gate on a guest QR
    /// order's first submission — actually fires it to the kitchen (see
    /// IOrderBuildingService.FireUnfiredItemsAsync) now that a staff member has looked at it.
    /// Any authenticated staff can confirm (same bar as Cancel on a still-unfired order) —
    /// there's no per-waiter assignment, whoever's free on the floor handles it. Rejecting a
    /// pending order is just the existing Cancel endpoint; no separate reject action needed.</summary>
    [HttpPost("{id:int}/confirm")]
    public async Task<ActionResult<OrderDto>> ConfirmGuestOrder(int id)
    {
        var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (!order.PendingStaffConfirmation) throw new ApiConflictException("This order isn't awaiting confirmation.");
        if (order.Cancelled) throw new ApiConflictException("Order is already cancelled.");

        order.PendingStaffConfirmation = false;
        try
        {
            if (!await orderBuilder.FireUnfiredItemsAsync(db, order, null))
            {
                // Empty cart at confirmation — the guest removed everything after placing (or
                // the order was stranded by an earlier bug). Auto-cancel instead of throwing:
                // the old throw happened before SaveChanges, so the flag reset above was lost
                // and the order sat in every staff member's confirmation queue forever.
                order.Cancelled = true;
                order.CancelledAt = DateTime.UtcNow;
                order.CancelReason = "Guest cart was empty at confirmation.";
                await db.SaveChangesAsync();
                await audit.LogAsync(AuditAction.Update, AuditResource.Order, order.Id.ToString(),
                    $"Order {order.Id} auto-cancelled at confirmation — guest cart was empty.", AuditSeverity.Low);
                return OrderDto.From(order);
            }

            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } pg
            && pg.ConstraintName is not null && pg.ConstraintName.StartsWith("IX_InventoryTransactions_OrderItemId_InventoryItemId"))
        {
            // Two staff members tapping Confirm on the same pending order at once (the pill
            // is floor-wide, visible on every device) both pass the PendingStaffConfirmation
            // check above before either commits — the loser's FireUnfiredItemsAsync then hits
            // the DB's idempotency index on InventoryTransactions, same backstop
            // ConsumeInventoryAsync's own doc comment describes. Same recovery as
            // GuestSessionController.PlaceOrder's identical race. Narrowed to this exact
            // constraint (not every DbUpdateException) so an unrelated save failure still
            // surfaces as a real 500 with its stack trace logged, instead of being masked
            // behind a misleading "already confirmed".
            throw new ApiConflictException("This order was already confirmed.");
        }

        await audit.LogAsync(AuditAction.Update, AuditResource.Order, order.Id.ToString(),
            $"Order {order.Id} confirmed by staff — sent to kitchen.", AuditSeverity.Low);
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
        var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == id);
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
        order.PendingStaffConfirmation = false; // rejecting a still-pending guest order clears the gate too

        // Cancelling/rejecting a guest QR order ends its session too (same hook as
        // CloseOrderAsync's settle path) — otherwise the guest's phone stays writable
        // against the cancelled order, silently re-adding items or re-flagging it for
        // confirmation with no idea staff turned it down.
        var guestSession = await db.GuestSessions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.OrderId == order.Id && (s.Status == GuestSessionStatus.Active || s.Status == GuestSessionStatus.Locked));
        if (guestSession is not null)
        {
            guestSession.Status = GuestSessionStatus.Closed;
            guestSession.ClosedReason = SessionCloseReason.StaffClosed;
            guestSession.ClosedAt = DateTime.UtcNow;
        }

        await ReleaseBillRedemptionsAsync(order);
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

    /// <summary>Moves an in-progress dine-in order to a different, currently-empty table —
    /// e.g. a party asks to move seats. Only relabels TableCode/Title; every line item, fire
    /// batch, and total is untouched. No Owner/Manager gate — same routine-floor-action
    /// availability as Fire/Add Items, not Cancel's stricter one, since nothing here is
    /// destructive or reversible-by-undo-only. Deliberately does NOT support swapping two
    /// already-occupied tables' orders — every real request for this feature turned out to
    /// mean "move this one order", not "exchange two".</summary>
    [HttpPost("{id:int}/shift-table")]
    public async Task<ActionResult<OrderDto>> ShiftTable(int id, ShiftTableRequest req)
    {
        var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Cancelled) throw new ApiConflictException("This order is cancelled.");
        if (order.TableCode is null) throw new ApiValidationException("This order isn't seated at a table.");

        var newCode = req.NewTableCode.Trim();
        if (string.IsNullOrWhiteSpace(newCode)) throw new ApiValidationException("Pick a table to shift to.");
        if (newCode == order.TableCode) throw new ApiValidationException($"Already at table {newCode}.");
        if (!await db.Tables.AnyAsync(t => t.Code == newCode)) throw new ApiValidationException($"Table {newCode} doesn't exist.");

        // Same "is this table free" rule TablesController.List/Delete already use — a table
        // only reads as occupied while it has a live (not cancelled, not fully paid+served) order.
        var busy = await db.Orders.AnyAsync(o => o.TableCode == newCode && !o.Cancelled && (!o.Paid || o.Status != OrderStatus.Served));
        if (busy) throw new ApiConflictException($"Table {newCode} already has an open order.");

        var oldCode = order.TableCode;
        order.TableCode = newCode;
        // Title is a creation-time snapshot (see OrderBuildingService's title-building
        // switch), not derived at read time — leaving it stale would mislead anywhere it's
        // shown directly (order history, notifications), even though KOT/receipt printing
        // already reconstructs its own title from TableCode fresh at print time.
        var guestSuffix = string.IsNullOrWhiteSpace(order.GuestName) ? "" : $" – {order.GuestName.Trim()}";
        order.Title = $"Table #{newCode}{guestSuffix}";

        await db.SaveChangesAsync();
        await audit.LogAsync(AuditAction.TableShift, AuditResource.Order, order.Id.ToString(),
            $"Order {order.Id} shifted from table {oldCode} to {newCode}.", AuditSeverity.Low);
        return OrderDto.From(order);
    }

    /// <summary>Cancels one whole KOT/fire-batch — voids every not-yet-served line in THAT
    /// round only, via the same before-cook-reverses/after-cook-doesn't rule as
    /// RemoveItem/Cancel (see VoidItemAsync). Every other round on the order (already
    /// Preparing/Ready/Served, or fired later) is untouched. If this was the order's last
    /// remaining active line anywhere, the order itself is marked Cancelled too — an order
    /// can't be left sitting open with nothing on it. Requires Owner/Manager if this KOT
    /// already has a served line (walking back served food needs a manager's say-so, same
    /// rule as the whole-order Cancel).</summary>
    [HttpPost("{id:int}/batches/{batchNumber:int}/cancel")]
    public async Task<ActionResult<OrderDto>> CancelBatch(int id, int batchNumber, CancelOrderRequest req)
    {
        var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Paid) throw new ApiConflictException("Cannot cancel a KOT on a paid order — use Refund instead.");
        var batch = order.FireBatches.FirstOrDefault(b => b.BatchNumber == batchNumber);
        if (batch is null) return NotFound();

        var batchItems = order.Items.Where(i => i.FireBatch == batchNumber && !i.Voided).ToList();
        if (batchItems.Count == 0) throw new ApiValidationException("This KOT has nothing left to cancel.");

        var hasServedItems = batchItems.Any(i => i.Status == OrderStatus.Served);
        if (hasServedItems && !IsOwnerOrManager()) return Forbid();

        foreach (var item in batchItems.Where(i => i.Status != OrderStatus.Served))
            await VoidItemAsync(order, item, req.Reason);

        order.Subtotal = order.Items.Where(i => !i.Voided).Sum(i => i.Price * i.Qty);
        OrderBuildingService.RecomputeTotals(order, await GetTaxRatePctAsync());
        orderBuilder.RecomputeBatchStatus(db, order, batchNumber);
        OrderBuildingService.RecomputeOrderStatus(order);

        if (!order.Items.Any(i => !i.Voided))
        {
            order.Cancelled = true;
            order.CancelledAt = DateTime.UtcNow;
            order.CancelReason = req.Reason;
            // Cancelling the last KOT cancels the whole order — release its redemptions
            // exactly like the whole-order Cancel above, then re-derive the (now zeroed)
            // discount totals.
            await ReleaseBillRedemptionsAsync(order);
            OrderBuildingService.RecomputeTotals(order, await GetTaxRatePctAsync());
        }

        await db.SaveChangesAsync();
        await audit.LogAsync(AuditAction.Update, AuditResource.Order, order.Id.ToString(),
            $"KOT #{1000 + batch.Id} (order {order.Id}) cancelled. Reason: {req.Reason ?? "not specified"}.", AuditSeverity.Medium);
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

    /// <summary>Returns billing-time redemptions to their owners when an UNPAID order is
    /// cancelled. The gift card's balance, the single-use coupon, and the loyalty points
    /// were all debited the instant they were applied (see ApplyBillGiftCard/
    /// ApplyBillCoupon/ApplyBillLoyalty) on the assumption the bill would be settled — a
    /// cancellation before payment means the guest bought nothing, so leaving them
    /// consumed silently burns real customer value. Zeroes the order's redemption amounts
    /// (codes stay on the order for the audit trail) — callers re-run RecomputeTotals
    /// after this. Paid orders never reach this: Cancel rejects them (that's Refund
    /// territory, where goods were actually delivered).</summary>
    private async Task ReleaseBillRedemptionsAsync(Order order)
    {
        if (order.GiftCardAmountApplied > 0 && order.GiftCardCode is not null)
        {
            var giftCard = await db.GiftCards.FirstOrDefaultAsync(g => g.Code == order.GiftCardCode);
            if (giftCard is not null)
            {
                giftCard.Balance += order.GiftCardAmountApplied;
                if (giftCard.Status == GiftCardStatus.Used && giftCard.Balance > 0)
                    giftCard.Status = GiftCardStatus.Active;
            }
            order.GiftCardAmountApplied = 0;
        }

        if (order.CouponCode is not null && order.CouponDiscountAmount > 0)
        {
            var coupon = await db.Coupons.FirstOrDefaultAsync(c => c.Code == order.CouponCode);
            if (coupon is not null) coupon.IsUsed = false;
            order.CouponDiscountAmount = 0;
        }

        if (order.LoyaltyPointsRedeemed > 0)
        {
            var customer = order.CustomerId is int cid ? await db.Customers.FirstOrDefaultAsync(c => c.Id == cid) : null;
            if (customer is not null)
                customer.RedeemedPoints = Math.Max(0, customer.RedeemedPoints - order.LoyaltyPointsRedeemed);
            order.LoyaltyPointsRedeemed = 0;
            order.LoyaltyDiscountAmount = 0;
        }
    }

    private int? CurrentUserId()
    {
        var claim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }

    /// <summary>Manager-only markdown applied at billing time (any time before Paid — not
    /// gated on Served, same reasoning as Pay: a QSR/Cash counter settles before anything's
    /// necessarily cooked). Kept as its own field, separate from the order-time
    /// DiscountAmount. Above ApprovalThresholds.DiscountAmount, a Manager (not the Owner —
    /// see Refund's comment for why) can't apply it directly: it goes to a pending
    /// ApprovalRequest and only actually lands on the order once the Owner approves it.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPatch("{id:int}/bill-discount")]
    public async Task<ActionResult<OrderDto>> ApplyBillDiscount(int id, BillDiscountRequest req)
    {
        var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Paid) throw new ApiConflictException("Cannot discount a paid order.");
        if ((req.Pct is null) == (req.Amount is null)) throw new ApiValidationException("Provide either a percentage or a flat amount, not both.");

        var amount = req.Amount ?? Math.Round(order.Subtotal * (req.Pct ?? 0) / 100, 2);
        if (amount < 0) throw new ApiValidationException("Discount cannot be negative.");

        if (!User.IsInRole(nameof(AppRole.Owner)) && amount > ApprovalThresholds.DiscountAmount)
        {
            db.Approvals.Add(new ApprovalRequest
            {
                Type = ApprovalType.Discount,
                RequestedById = CurrentUserId() ?? 0,
                Title = $"Bill discount — Order #{order.Id}",
                Description = $"{amount:C} discount on order {order.Id} (subtotal {order.Subtotal:C}).",
                Amount = amount,
                LinkedEntityId = order.Id,
            });
            await db.SaveChangesAsync();
            return Accepted(new { pendingApproval = true, message = $"Discount of {amount:C} needs Owner approval (above the {ApprovalThresholds.DiscountAmount:C} auto-approve limit) — sent to Approvals." });
        }

        order.BillDiscountAmount = amount;
        OrderBuildingService.RecomputeTotals(order, await GetTaxRatePctAsync());
        await db.SaveChangesAsync();
        await audit.LogAsync(AuditAction.Discount, AuditResource.Order, order.Id.ToString(),
            $"Bill discount of {amount:C} applied to order {order.Id}.", AuditSeverity.Medium);
        return OrderDto.From(order);
    }

    /// <summary>Redeems a coupon at billing time (any time before Paid — not gated on
    /// Served, see ApplyBillDiscount). Only one coupon per order.</summary>
    [HttpPatch("{id:int}/bill-coupon")]
    public async Task<ActionResult<OrderDto>> ApplyBillCoupon(int id, BillCouponRequest req)
    {
        var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Paid) throw new ApiConflictException("Cannot apply a coupon to a paid order.");
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

    /// <summary>Redeems a gift card at billing time (any time before Paid — not gated on
    /// Served, see ApplyBillDiscount). Debits only what this bill can absorb. Only one gift
    /// card per order.</summary>
    [HttpPatch("{id:int}/bill-giftcard")]
    public async Task<ActionResult<OrderDto>> ApplyBillGiftCard(int id, BillGiftCardRequest req)
    {
        var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Paid) throw new ApiConflictException("Cannot apply a gift card to a paid order.");
        if (order.GiftCardCode is not null) throw new ApiConflictException("A gift card has already been applied to this order.");
        if (string.IsNullOrWhiteSpace(req.Code)) throw new ApiValidationException("Enter a gift card code.");

        var giftCard = await db.GiftCards.FirstOrDefaultAsync(g => g.Code == req.Code.ToUpperInvariant());
        if (giftCard is null) throw new ApiValidationException("Gift card code not found.");
        if (giftCard.Status != GiftCardStatus.Active) throw new ApiConflictException("Gift card is not active.");
        if (giftCard.ExpiresAt < DateTime.UtcNow) throw new ApiConflictException("Gift card has expired.");

        var owedBeforeGiftCard = Math.Max(0, order.Subtotal - order.DiscountAmount - order.BillDiscountAmount - order.CouponDiscountAmount - order.LoyaltyDiscountAmount);
        var redeem = Math.Min(giftCard.Balance, owedBeforeGiftCard);
        order.GiftCardCode = giftCard.Code;
        order.GiftCardAmountApplied = redeem;
        giftCard.Balance -= redeem;
        if (giftCard.Balance <= 0) giftCard.Status = GiftCardStatus.Used;
        OrderBuildingService.RecomputeTotals(order, await GetTaxRatePctAsync());
        await db.SaveChangesAsync();
        return OrderDto.From(order);
    }

    /// <summary>Redeems the order's linked customer's loyalty points as a bill-time discount
    /// (1 point = ₹1, matching the earn rate in OrderBuildingService.RecordVisit). Same
    /// not-gated-on-Served timing as the other billing-time adjustments above. Gated on a real
    /// guest phone number — an anonymous walk-in's Customer row is a shared bucket (see
    /// OrderBuildingService.FindOrCreateCustomerAsync), not a real individual's point balance,
    /// so redeeming against it wouldn't mean anything.</summary>
    [HttpPatch("{id:int}/bill-loyalty")]
    public async Task<ActionResult<OrderDto>> ApplyBillLoyalty(int id, BillLoyaltyRequest req)
    {
        var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers)
            .Include(o => o.FireBatches).Include(o => o.Payments).Include(o => o.Customer)
            .FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Paid) throw new ApiConflictException("Cannot redeem points on a paid order.");
        if (order.LoyaltyPointsRedeemed > 0) throw new ApiConflictException("Loyalty points have already been redeemed on this order.");
        if (string.IsNullOrWhiteSpace(order.GuestPhone)) throw new ApiValidationException("A guest mobile number is needed to redeem loyalty points.");
        if (req.Points <= 0) throw new ApiValidationException("Points must be positive.");
        var customer = order.Customer ?? throw new ApiValidationException("No customer linked to this order.");
        if (req.Points > customer.AvailablePoints) throw new ApiValidationException($"Only {customer.AvailablePoints} points available.");

        var owedBeforeLoyalty = Math.Max(0, order.Subtotal - order.DiscountAmount - order.BillDiscountAmount - order.CouponDiscountAmount - order.GiftCardAmountApplied);
        var redeemedPoints = Math.Min(req.Points, (int)Math.Floor(owedBeforeLoyalty));
        if (redeemedPoints <= 0) throw new ApiConflictException("Nothing left on this bill for points to cover.");

        customer.RedeemedPoints += redeemedPoints;
        order.LoyaltyPointsRedeemed = redeemedPoints;
        order.LoyaltyDiscountAmount = redeemedPoints;
        OrderBuildingService.RecomputeTotals(order, await GetTaxRatePctAsync());
        await db.SaveChangesAsync();
        return OrderDto.From(order);
    }

    /// <summary>Sets Service Charge / Packing Charge / Delivery Charge / Tip / Round Off in
    /// one call — every field optional, only the ones supplied change (send 0 to clear one).
    /// Same not-gated-on-Served timing as the discount/coupon/gift-card adjustments above, and
    /// open to any authenticated staff (not Owner/Manager-only) since these are routine billing
    /// add-ons, not a discretionary markdown.</summary>
    [HttpPatch("{id:int}/bill-charges")]
    public async Task<ActionResult<OrderDto>> ApplyBillCharges(int id, BillChargesRequest req)
    {
        var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Paid) throw new ApiConflictException("Cannot adjust charges on a paid order.");
        if (req.ServiceChargePct is not null && req.ServiceChargeAmount is not null)
            throw new ApiValidationException("Provide either a service charge percentage or a flat amount, not both.");
        if (req.ServiceChargePct is < 0 || req.ServiceChargeAmount is < 0 || req.PackingChargeAmount is < 0
            || req.DeliveryChargeAmount is < 0 || req.TipAmount is < 0)
            throw new ApiValidationException("Charges cannot be negative.");

        if (req.ServiceChargePct is not null) order.ServiceChargeAmount = Math.Round(order.Subtotal * req.ServiceChargePct.Value / 100, 2);
        else if (req.ServiceChargeAmount is not null) order.ServiceChargeAmount = req.ServiceChargeAmount.Value;
        if (req.PackingChargeAmount is not null) order.PackingChargeAmount = req.PackingChargeAmount.Value;
        if (req.DeliveryChargeAmount is not null) order.DeliveryChargeAmount = req.DeliveryChargeAmount.Value;
        if (req.TipAmount is not null) order.TipAmount = req.TipAmount.Value;
        if (req.RoundOffAmount is not null) order.RoundOffAmount = req.RoundOffAmount.Value;

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
    /// Marks the bill paid. Payment can happen at any point in the order's lifecycle —
    /// before, during, or after serving (e.g. a QSR counter that collects payment up front
    /// and serves/bills around it) — this is intentionally NOT gated on OrderStatus.
    /// Does NOT force Served on its own; a table frees up only once it's BOTH paid AND served
    /// (see TablesController/PublicController's occupancy check, and the activeOnly filter
    /// in List() above).
    /// </summary>
    private static readonly HashSet<string> ValidPaymentMethods =
        new(StringComparer.OrdinalIgnoreCase) { "Cash", "Card", "UPI" };

    [HttpPatch("{id:int}/pay")]
    public async Task<ActionResult<OrderDto>> Pay(int id, PayRequest? req = null)
    {
        var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Cancelled) throw new ApiConflictException("A cancelled order can't be marked paid.");
        if (order.Paid) throw new ApiConflictException("Order is already paid.");

        if (req?.Splits is { Count: > 0 } splits)
        {
            foreach (var split in splits)
            {
                if (!ValidPaymentMethods.Contains(split.Method))
                    throw new ApiValidationException($"'{split.Method}' isn't a valid payment method.");
                if (split.Amount <= 0)
                    throw new ApiValidationException("Each split amount must be greater than zero.");
            }
            // Cumulative across every Pay call on this order — a partial payment (AllowPartial)
            // can be topped up by a later call, so what matters is the running total against
            // the bill, not just what this one call brought.
            var splitTotal = splits.Sum(s => s.Amount);
            var alreadyPaid = order.Payments.Sum(p => p.Amount);
            var remaining = order.Total - alreadyPaid;
            var newTotalPaid = alreadyPaid + splitTotal;

            if (splitTotal - remaining > 0.01m)
                throw new ApiValidationException($"Payment (₹{splitTotal:0.00}) is more than the remaining balance (₹{remaining:0.00}).");
            if (!req.AllowPartial && Math.Abs(splitTotal - remaining) > 0.01m)
                throw new ApiValidationException($"Split amounts (₹{splitTotal:0.00}) must add up to the remaining balance (₹{remaining:0.00}).");

            foreach (var split in splits)
                order.Payments.Add(new OrderPayment { OrderId = order.Id, Method = split.Method.Trim(), Amount = split.Amount });
            order.PaymentMethod = order.Payments.Count > 1 ? "Multiple" : order.Payments[0].Method;

            // Balance fully covered (allowing for rounding) — settle for real. Otherwise this
            // was a deliberate partial tender (AllowPartial): leave Paid false, the Payments
            // rows recorded above already carry the running AmountPaid/BalanceDue (see
            // OrderDto.From) and a later Pay call collects the rest.
            if (order.Total - newTotalPaid > 0.01m)
            {
                await db.SaveChangesAsync();
                return OrderDto.From(order);
            }
        }
        else
        {
            // A settle MUST name its tender. This branch used to accept a missing/blank
            // method and fall through to CloseOrderAsync anyway — marking the bill Paid
            // with no OrderPayment row at all, invisible to method-wise revenue reporting.
            var method = string.IsNullOrWhiteSpace(req?.PaymentMethod) ? null : req.PaymentMethod.Trim();
            if (method is null)
                throw new ApiValidationException("A payment method (or splits) is required to settle the bill.");
            if (!ValidPaymentMethods.Contains(method))
                throw new ApiValidationException($"'{method}' isn't a valid payment method.");

            // Only the still-owed balance, not order.Total — an earlier deliberate partial
            // (AllowPartial) already has its own Payments rows; recording the full total
            // again would make the ledger exceed the bill.
            var owed = order.Total - order.Payments.Sum(p => p.Amount);
            if (owed > 0)
                order.Payments.Add(new OrderPayment { OrderId = order.Id, Method = method, Amount = owed });
            order.PaymentMethod = order.Payments.Count > 1 ? "Multiple" : method;
        }

        // KeepOpen: the payment above fully covers the balance, but this is a deliberate
        // advance (Pay First) rather than a real settle — leave Paid false/PartiallyPaid true
        // so AddItem/RemoveItem/etc (which only gate on Paid) keep working. If more items get
        // added later, BalanceDue goes positive again on its own (Total grows, AmountPaid
        // doesn't); if not, Close finalizes it without a further payment.
        if (req?.KeepOpen == true)
        {
            await db.SaveChangesAsync();
            return OrderDto.From(order);
        }

        await CloseOrderAsync(order);
        await db.SaveChangesAsync();
        return OrderDto.From(order);
    }

    /// <summary>Marks Paid and closes any linked guest session — the actual "this bill is now
    /// settled" transition, shared by Pay (once a payment fully covers the balance) and Close
    /// (finalizing a KeepOpen/Pay First order that already covers its balance with no further
    /// payment to collect). See PayRequest.KeepOpen.</summary>
    private async Task CloseOrderAsync(Order order)
    {
        order.Paid = true;

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

    /// <summary>Finalizes a KeepOpen (Pay First) order once no more items are going to be
    /// added — the payment already recorded fully covers the balance, so there's nothing new
    /// to collect, just the "this is genuinely done" transition that Pay's balance-due flow
    /// can't express (it always requires a positive amount to submit). Rejects if anything's
    /// still owed; use Pay to collect that first.</summary>
    [HttpPatch("{id:int}/close")]
    public async Task<ActionResult<OrderDto>> Close(int id)
    {
        var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Cancelled) throw new ApiConflictException("A cancelled order can't be marked paid.");
        if (order.Paid) throw new ApiConflictException("Order is already paid.");

        var remaining = order.Total - order.Payments.Sum(p => p.Amount);
        if (remaining > 0.01m)
            throw new ApiValidationException($"₹{remaining:0.00} is still due — collect that before closing.");

        await CloseOrderAsync(order);
        await db.SaveChangesAsync();
        return OrderDto.From(order);
    }

    /// <summary>Fills in the guest's phone/name on an order that already exists — see
    /// UpdateOrderGuestRequest. Deliberately NOT gated on Paid: the reason a number gets added
    /// late is almost always "send the guest their bill", which happens after settling.
    ///
    /// Setting a phone also moves the order's CRM link. An order rung up without a number is
    /// attached to whatever FindOrCreateCustomerAsync matched at the time (typically the
    /// shared "Walk-in Guest" record), and its visit/spend/points were already credited there
    /// by RecordVisit. Re-running the same lookup with the phone either lands on that same
    /// record — in which case the phone is simply stamped onto it and nothing moves — or on
    /// the real customer who owns that number, in which case this order's visit is transferred
    /// so the walk-in placeholder isn't left holding a stranger's spend.</summary>
    [HttpPatch("{id:int}/guest")]
    public async Task<ActionResult<OrderDto>> UpdateGuest(int id, UpdateOrderGuestRequest req)
    {
        var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Cancelled) throw new ApiConflictException("A cancelled order can't be edited.");

        // Same digits-only normalization + 10-digit rule Create uses, so a number added here
        // is stored in the identical shape one captured up front would be — anything else and
        // the customer-by-phone lookup below would silently miss.
        var normalizedPhone = string.IsNullOrWhiteSpace(req.GuestPhone) ? null : new string(req.GuestPhone.Where(char.IsDigit).ToArray());
        if (normalizedPhone is not null && normalizedPhone.Length != 10)
            throw new ApiValidationException("A valid 10-digit guest mobile number is required.");
        var trimmedName = string.IsNullOrWhiteSpace(req.GuestName) ? null : req.GuestName.Trim();
        if (normalizedPhone is null && trimmedName is null)
            throw new ApiValidationException("Nothing to update — supply a guest name or mobile number.");

        if (trimmedName is not null) order.GuestName = trimmedName;
        if (normalizedPhone is not null) order.GuestPhone = normalizedPhone;

        if (normalizedPhone is not null)
        {
            var previous = order.CustomerId is int prevId ? await db.Customers.FirstOrDefaultAsync(c => c.Id == prevId) : null;
            var customer = await orderBuilder.FindOrCreateCustomerAsync(db, order.GuestName ?? "Walk-in Guest", normalizedPhone);
            if (previous is null || previous.Id != customer.Id)
            {
                // Reverse what RecordVisit credited to the old record at creation, then credit
                // the same visit to the one this order actually belongs to. Clamped at zero:
                // an order created before CRM linking existed can have a CustomerId pointing at
                // a record whose counters were never incremented for it.
                if (previous is not null)
                {
                    previous.VisitCount = Math.Max(0, previous.VisitCount - 1);
                    previous.TotalSpent = Math.Max(0m, previous.TotalSpent - order.Total);
                    previous.TotalPoints = Math.Max(0, previous.TotalPoints - (int)Math.Floor(order.Total));
                }
                customer.VisitCount += 1;
                customer.TotalSpent += order.Total;
                customer.TotalPoints += (int)Math.Floor(order.Total);
                customer.LastVisitAt = DateTime.UtcNow;
                order.Customer = customer;
            }
        }

        await db.SaveChangesAsync();
        return OrderDto.From(order);
    }

    /// <summary>Full or partial refund — financially sensitive, so unlike most of this
    /// controller it's explicitly restricted rather than relying on the auth fallback
    /// policy (any authenticated user) that everything else here uses. Above
    /// ApprovalThresholds.RefundAmount, a Manager (never the Owner — see below) can't
    /// refund directly: this creates a pending ApprovalRequest instead and the actual
    /// refund only happens once the Owner approves it (ApprovalsController.Approve).</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("{id:int}/refund")]
    public async Task<ActionResult<OrderDto>> Refund(int id, RefundOrderRequest req)
    {
        var order = await db.Orders.Include(o => o.Items).ThenInclude(i => i.SelectedModifiers).Include(o => o.FireBatches).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (!order.Paid) throw new ApiValidationException("Only paid orders can be refunded.");
        if (order.Refunded) throw new ApiConflictException("Order has already been refunded.");

        var amount = req.Amount ?? order.Total;
        if (amount <= 0 || amount > order.Total)
            throw new ApiValidationException("Refund amount must be between 0 and the order total.");

        // Owner bypasses always — they ARE the approver. A Manager over threshold needs
        // Owner sign-off instead of refunding straight away.
        if (!User.IsInRole(nameof(AppRole.Owner)) && amount > ApprovalThresholds.RefundAmount)
        {
            db.Approvals.Add(new ApprovalRequest
            {
                Type = ApprovalType.Refund,
                RequestedById = CurrentUserId() ?? 0,
                Title = $"Refund — Order #{order.Id}",
                Description = req.Reason ?? "No reason given.",
                Amount = amount,
                LinkedEntityId = order.Id,
            });
            await db.SaveChangesAsync();
            return Accepted(new { pendingApproval = true, message = $"Refund of {amount:C} needs Owner approval (above the {ApprovalThresholds.RefundAmount:C} auto-approve limit) — sent to Approvals." });
        }

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
