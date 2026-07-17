using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CafePOS.Api.Controllers;

[ApiController]
[Route("api/orders")]
public class OrdersController(
    CafePosDbContext db, IAuditService audit, QrTokenService qrTokens, ReceiptTokenService receiptTokens, ILogger<OrdersController> logger,
    ITaxRateCache taxRateCache, ITenantContext tenantContext) : ControllerBase
{
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
        [FromQuery] bool kdsReady = false)
    {
        var query = db.Orders.Include(o => o.Items).Include(o => o.FireBatches).AsQueryable();
        // "Active" means still needs attention — matches the table-occupancy rule:
        // an order stays active (visible on KDS, counted as in-progress) until it's
        // BOTH paid AND served. Paying early must not make it vanish from the
        // kitchen's ticket list while the food still hasn't gone out.
        if (activeOnly) query = query.Where(o => !o.Paid || o.Status != OrderStatus.Served);
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
        var order = await BuildOrderAsync(req.OrderType, req.TableCode, req.GuestName, req.Items, req.DiscountPct, branchId: req.BranchId, guestPhone: normalizedPhone, servedByStaffId: req.ServedByStaffId);
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

        var order = await BuildOrderAsync("DINE_IN", tableCode, req.GuestName, req.Items, discountPct: 0, explicitTenantId: tenantId, guestPhone: normalizedPhone);

        // A QR-self-ordering guest has no POS to come back and "fire" from — so unlike the
        // staff path (which fires as an explicit second step), a public order auto-fires
        // immediately on creation, keeping it a single atomic create-and-send action.
        FireUnfiredItems(order, tenantId);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = order.Id }, OrderDto.From(order));
    }

    /// <summary>Restricts a DbSet to one specific tenant, bypassing the ambient
    /// JWT-derived filter — used only by the anonymous QR flow, which has no JWT and so
    /// must be told the tenant explicitly via <paramref name="explicitTenantId"/>.
    /// Returns the DbSet unchanged (normal ambient-filtered behaviour) for the staff POS
    /// path, where explicitTenantId is null.</summary>
    private static IQueryable<T> TenantScoped<T>(DbSet<T> set, int? explicitTenantId) where T : class, ITenantScoped =>
        explicitTenantId is int tid ? set.IgnoreQueryFilters().Where(e => e.TenantId == tid) : set;

    /// <summary>
    /// Shared order-pricing/creation core used by both the staff POS (<see cref="Create"/>)
    /// and the anonymous QR ordering flow (<see cref="CreatePublic"/>), so the two can never
    /// drift on pricing, inventory, or CRM behaviour.
    /// </summary>
    private async Task<Order> BuildOrderAsync(
        string orderType, string? tableCode, string? guestName, List<CreateOrderItemDto> items,
        decimal discountPct, int? explicitTenantId = null, int? branchId = null, string? guestPhone = null,
        int? servedByStaffId = null)
    {
        if (items.Count == 0)
            throw new ApiValidationException("Order must contain at least one item.");

        if (orderType == "DINE_IN")
        {
            if (string.IsNullOrWhiteSpace(tableCode))
                throw new ApiValidationException("Dine-in orders need a tableCode.");

            // One round trip instead of two separate AnyAsync calls (table-exists,
            // then table-busy) — each round trip to the remote Postgres instance is a
            // real, measurable cost here, so every one removed from this hot path
            // (fired on every single order) helps. A table only frees up once its
            // order is BOTH paid AND served — matches TablesController/PublicController.
            var tableCheck = await TenantScoped(db.Tables, explicitTenantId)
                .Where(t => t.Code == tableCode)
                .Select(t => new
                {
                    Busy = TenantScoped(db.Orders, explicitTenantId)
                        .Any(o => o.TableCode == tableCode && (!o.Paid || o.Status != OrderStatus.Served)),
                })
                .FirstOrDefaultAsync();
            if (tableCheck is null)
                throw new ApiValidationException($"Table {tableCode} does not exist.");
            if (tableCheck.Busy)
                throw new ApiConflictException($"Table {tableCode} already has an open order.");
        }

        var menuIds = items.Select(i => i.MenuItemId).ToList();
        var menu = await TenantScoped(db.MenuItems, explicitTenantId).Where(m => menuIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id);

        var orderItems = new List<OrderItem>();
        foreach (var line in items)
        {
            if (!menu.TryGetValue(line.MenuItemId, out var menuItem))
                throw new ApiValidationException($"Menu item {line.MenuItemId} not found.");
            if (!menuItem.Available)
                throw new ApiValidationException($"{menuItem.Name} is currently unavailable.");
            if (line.Qty <= 0)
                throw new ApiValidationException($"Invalid quantity for {menuItem.Name}.");

            orderItems.Add(new OrderItem
            {
                MenuItemId = menuItem.Id,
                Name = menuItem.Name,
                Qty = line.Qty,
                Price = menuItem.Price,
                Modifier = line.Modifier,
            });
        }

        // Order pricing only ever needs the tax rate out of Settings, and that number
        // almost never changes — cached (30s TTL, invalidated on SettingsController.Update)
        // instead of paying a full round trip to the remote Postgres instance on every
        // single order, which was the single biggest recurring cost on this hot path.
        var effectiveTenantId = explicitTenantId ?? tenantContext.TenantIdOrDefault;
        var taxRatePct = await taxRateCache.GetTaxRatePctAsync(effectiveTenantId,
            async () => (await TenantScoped(db.Settings, explicitTenantId).FirstAsync()).TaxRatePct);
        var subtotal = orderItems.Sum(i => i.Price * i.Qty);
        var clampedDiscountPct = Math.Clamp(discountPct, 0, 100);
        // Order-time discount is the manual % only now. Coupons and gift cards moved to the
        // billing stage (bill-coupon / bill-giftcard on a Served order) — see plan.
        var discountAmount = Math.Round(subtotal * clampedDiscountPct / 100, 2);

        // Anonymous QR self-orders (explicitTenantId set) have no JWT/logged-in staff
        // member at all — CreatedByUserId stays null for those, same as a guest
        // ordering from their own table with no staff involvement.
        int? createdByUserId = null;
        string? createdByName = null;
        StaffMember? servedBy = null;
        if (explicitTenantId is null)
        {
            var idClaim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (idClaim is not null && int.TryParse(idClaim, out var currentUserId))
            {
                var currentUser = await db.Users.FindAsync(currentUserId);
                createdByUserId = currentUser?.Id;
                createdByName = currentUser?.Name;
            }

            // Who actually served this order: explicit pick (a Cashier/Manager ringing
            // up on behalf of a waiter from a shared counter POS) takes priority; with
            // none given, default to the logged-in user's own StaffMember record — the
            // common case of a waiter taking their own order on their own login.
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
            _ => "Dine In",
        };
        var title = orderType == "DINE_IN"
            ? $"Table #{tableCode}{guestSuffix}"
            : $"{typeLabel} – {guest ?? "Walk-in"}";

        var order = new Order
        {
            BranchId = branchId,
            Title = title,
            OrderType = orderType,
            TableCode = orderType == "DINE_IN" ? tableCode : null,
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
        };
        // Tax/Total from the single shared formula (Subtotal minus all discount components).
        RecomputeTotals(order, taxRatePct);
        // Anonymous QR orders have no JWT, so the DbContext's auto-stamp (which reads
        // the ambient tenant from the token) would default to tenant 1 — set it
        // explicitly from the resolved slug instead.
        if (explicitTenantId is int tid) order.TenantId = tid;
        db.Orders.Add(order);

        var customer = await FindOrCreateCustomerAsync(guest ?? "Walk-in Guest", guestPhone, explicitTenantId);
        // Link via the navigation, not a copied CustomerId int: for a brand-new
        // customer, customer.Id is still 0 until SaveChanges assigns the real
        // Postgres-generated identity value. EF Core's change tracker fixes up
        // the FK automatically through the tracked object reference.
        order.Customer = customer;
        RecordVisit(customer, order.Total);
        TrackFavorites(customer, orderItems, explicitTenantId);

        // Explicit transaction (relational providers only — InMemory, used when no
        // Postgres connection string is configured, doesn't support transactions) so
        // that if inventory deduction fails after the order row is written, the whole
        // order rolls back too rather than leaving stock un-deducted for a placed order.
        IDbContextTransaction? txn = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync() : null;
        try
        {
            // Order needs its real, Postgres-assigned Id before inventory transactions
            // (and the discount audit entry below) can reference it, so this happens
            // as its own save inside the transaction. NOTE: no kitchen notification here
            // anymore — the order is created "Open" and only reaches the kitchen when
            // POST /orders/{id}/fire runs (see Fire), keeping ordering and dispatch separate.

            // Added to the same tracked context rather than via IAuditService.LogAsync
            // (which does its own immediate SaveChangesAsync) — riding along in the
            // save below instead of costing a third round trip to the remote Postgres
            // instance on every discounted order.
            await db.SaveChangesAsync();
            if (clampedDiscountPct > 0)
            {
                var discountEntry = new AuditLogEntry
                {
                    Action = AuditAction.Discount,
                    Resource = AuditResource.Order,
                    ResourceId = order.Id.ToString(),
                    Details = $"Order {order.Id} applied {clampedDiscountPct}% discount (−{discountAmount:C}).",
                    Severity = AuditSeverity.Medium,
                };
                if (explicitTenantId is int auditTid) discountEntry.TenantId = auditTid;
                db.AuditLog.Add(discountEntry);
            }

            await ConsumeInventoryAsync(menu, orderItems, order.Id, explicitTenantId);
            await db.SaveChangesAsync();

            if (txn is not null) await txn.CommitAsync();
        }
        catch
        {
            if (txn is not null) await txn.RollbackAsync();
            throw;
        }
        finally
        {
            if (txn is not null) await txn.DisposeAsync();
        }

        return order;
    }

    /// <summary>Moves ONE item one step along NEW → PREPARING → READY → SERVED — the chef can
    /// advance each item independently, so within one KOT the Paneer can be Ready while the
    /// Roti is still New. The item's batch status and the order status are recomputed as
    /// rollups afterward (least-progressed active item).</summary>
    [HttpPatch("{id:int}/items/{itemId:int}/advance")]
    public async Task<ActionResult<OrderDto>> AdvanceItem(int id, int itemId)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.FireBatches).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Paid) throw new ApiConflictException("Order is already paid.");
        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null) return NotFound();
        if (item.FireBatch == 0) throw new ApiValidationException("Item hasn't been fired to the kitchen yet.");

        var idx = Array.IndexOf(StatusFlow, item.Status);
        if (idx < StatusFlow.Length - 1) item.Status = StatusFlow[idx + 1];
        RecomputeBatchStatus(order, item.FireBatch);
        RecomputeOrderStatus(order);

        await db.SaveChangesAsync();
        return OrderDto.From(order);
    }

    /// <summary>Advance-all: moves every not-yet-Served item in ONE fire batch one step
    /// forward at once (the KDS "Advance All" button and the Tables screen's per-batch "Mark
    /// Served"). Every other batch on the order is untouched. Recomputes the batch + order
    /// status rollups afterward.</summary>
    [HttpPatch("{id:int}/advance/{batchNumber:int}")]
    public async Task<ActionResult<OrderDto>> Advance(int id, int batchNumber)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.FireBatches).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Paid) throw new ApiConflictException("Order is already paid.");
        var batch = order.FireBatches.FirstOrDefault(b => b.BatchNumber == batchNumber);
        if (batch is null) return NotFound();

        foreach (var item in order.Items.Where(i => i.FireBatch == batchNumber && i.Status != OrderStatus.Served))
        {
            var idx = Array.IndexOf(StatusFlow, item.Status);
            if (idx < StatusFlow.Length - 1) item.Status = StatusFlow[idx + 1];
        }
        RecomputeBatchStatus(order, batchNumber);
        RecomputeOrderStatus(order);

        await db.SaveChangesAsync();
        return OrderDto.From(order);
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

    /// <summary>Recomputes ONE fire batch's status as a rollup of its items (least-progressed
    /// active item, or Served once all its items are). Fires the "ready to serve" notification
    /// when the whole batch first reaches READY (i.e. every item in the round is Ready).</summary>
    private void RecomputeBatchStatus(Order order, int batchNumber)
    {
        var batch = order.FireBatches.FirstOrDefault(b => b.BatchNumber == batchNumber);
        if (batch is null) return;
        var items = order.Items.Where(i => i.FireBatch == batchNumber).ToList();
        if (items.Count == 0) return;

        var previous = batch.Status;
        var active = items.Where(i => i.Status != OrderStatus.Served).ToList();
        batch.Status = active.Count > 0 ? active.Min(i => i.Status) : OrderStatus.Served;

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

    /// <summary>Order.Status is a computed rollup of every active (non-Served) fire batch,
    /// not a value set directly — the LEAST-progressed active batch surfaces here (enum
    /// ordering New&lt;Preparing&lt;Ready&lt;Served means Min picks it), so table
    /// occupancy/billing-eligibility checks elsewhere in the app (which only ever ask "is
    /// this order fully served") keep working unchanged: Served only once every batch is.
    /// Called after any batch is created (Fire) or advanced.</summary>
    private static void RecomputeOrderStatus(Order order)
    {
        var active = order.FireBatches.Where(b => b.Status != OrderStatus.Served).ToList();
        order.Status = active.Count > 0
            ? active.Min(b => b.Status)
            : (order.FireBatches.Count > 0 ? OrderStatus.Served : OrderStatus.New);
    }

    /// <summary>Assigns the next fire-batch number to every not-yet-fired item, creates that
    /// batch's own independent kitchen-ticket row (starts New), and notifies the kitchen
    /// about JUST those items (never the whole order again on a re-fire) — any OTHER batch
    /// already Preparing/Ready/Served on this order keeps its own status, untouched. Returns
    /// false if there was nothing new to fire. Recomputes Order.Status but does not save —
    /// the caller saves.</summary>
    private bool FireUnfiredItems(Order order, int? explicitTenantId)
    {
        var unfired = order.Items.Where(i => i.FireBatch == 0).ToList();
        if (unfired.Count == 0) return false;

        order.CurrentFireBatch += 1;
        foreach (var item in unfired) item.FireBatch = order.CurrentFireBatch;
        order.FireBatches.Add(new OrderFireBatch { OrderId = order.Id, BatchNumber = order.CurrentFireBatch });
        RecomputeBatchStatus(order, order.CurrentFireBatch); // freshly-fired items are all New → batch New
        RecomputeOrderStatus(order);

        // OrderPlaced is the ONLY category Chef/KitchenStaff logins see (see
        // NotificationsController.List's role filter). Wording branches so the kitchen can
        // tell a fresh order from extra items appended to one already on the line.
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
        // Anonymous QR orders have no JWT to auto-stamp the tenant from — same reason
        // order.TenantId is set explicitly in BuildOrderAsync.
        if (explicitTenantId is int tid) notification.TenantId = tid;
        db.Notifications.Add(notification);
        return true;
    }

    /// <summary>Fires all not-yet-fired items on an existing order to the kitchen as their
    /// own new fire batch. The separate "dispatch" step that the staff POS calls right after
    /// Create (or later, for a held order) — distinct from ordering so items can be
    /// added/edited before hitting the line. Order.Status is recomputed as a rollup (see
    /// RecomputeOrderStatus) — any other batch already Preparing/Ready/Served on this order
    /// keeps its own status, untouched.</summary>
    [HttpPost("{id:int}/fire")]
    public async Task<ActionResult<OrderDto>> Fire(int id)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.FireBatches).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Paid) throw new ApiConflictException("Order is already paid.");
        if (!FireUnfiredItems(order, explicitTenantId: null))
            throw new ApiValidationException("No new items to fire.");

        await db.SaveChangesAsync();
        return OrderDto.From(order);
    }

    /// <summary>Adds one item to an existing, not-yet-paid order (new item starts unfired,
    /// FireBatch 0, so it only reaches the kitchen — as its own new fire batch, see
    /// FireUnfiredItems — on the next Fire). Allowed at any stage, even after every existing
    /// batch has been Served (e.g. the table asks for one more item at the billing stage):
    /// adding the item doesn't touch any existing OrderFireBatch, so whatever's already
    /// Preparing/Ready/Served keeps its own status undisturbed — only once this item is
    /// fired does it become its own separate ticket. The bill still totals every item
    /// together regardless of which fire round it came from. Recomputes totals and deducts
    /// inventory for just the new line.</summary>
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
        order.Subtotal = order.Items.Sum(i => i.Price * i.Qty);
        RecomputeTotals(order, await GetTaxRatePctAsync());

        await ConsumeInventoryAsync(new Dictionary<int, MenuItem> { [menuItem.Id] = menuItem }, [newItem], order.Id);
        await db.SaveChangesAsync();
        return OrderDto.From(order);
    }

    /// <summary>Removes an item from a not-yet-paid order. Freely allowed while the item is
    /// still unfired (even if other items on the same order are already Served — this one
    /// hasn't reached the kitchen at all yet); once it's been fired (FireBatch &gt; 0) only an
    /// Owner/Manager may pull it back, and only if that item itself hasn't been Served.</summary>
    [HttpDelete("{id:int}/items/{itemId:int}")]
    public async Task<ActionResult<OrderDto>> RemoveItem(int id, int itemId)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.FireBatches).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Paid) throw new ApiConflictException("Cannot modify a paid order.");

        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null) return NotFound();
        if (order.Items.Count == 1) throw new ApiValidationException("Order must contain at least one item.");
        if (item.FireBatch > 0)
        {
            if (!IsOwnerOrManager()) return Forbid();
            if (item.Status == OrderStatus.Served) throw new ApiConflictException("Cannot remove an item that's already been served.");
        }

        order.Items.Remove(item);
        db.OrderItems.Remove(item);
        order.Subtotal = order.Items.Sum(i => i.Price * i.Qty);
        RecomputeTotals(order, await GetTaxRatePctAsync());
        // Pulling a fired item can shift its batch's rollup (e.g. removing the last still-New
        // item leaves the batch all-Ready) — and the order status with it.
        if (item.FireBatch > 0)
        {
            RecomputeBatchStatus(order, item.FireBatch);
            RecomputeOrderStatus(order);
        }
        await db.SaveChangesAsync();
        return OrderDto.From(order);
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
        RecomputeTotals(order, await GetTaxRatePctAsync());
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
        RecomputeTotals(order, await GetTaxRatePctAsync());
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
        RecomputeTotals(order, await GetTaxRatePctAsync());
        await db.SaveChangesAsync();
        return OrderDto.From(order);
    }

    /// <summary>Recomputes Tax + Total from Subtotal minus every discount component (order
    /// discount, bill discount, coupon, gift card). The single source of truth for order
    /// pricing math, so create/add/remove/bill-discount/coupon/giftcard never drift.</summary>
    private static void RecomputeTotals(Order o, decimal taxRatePct)
    {
        var totalDiscount = o.DiscountAmount + o.BillDiscountAmount + o.CouponDiscountAmount + o.GiftCardAmountApplied;
        var taxable = Math.Max(0, o.Subtotal - totalDiscount);
        o.Tax = Math.Round(taxable * taxRatePct / 100, 2);
        o.Total = taxable + o.Tax;
    }

    /// <summary>Tax rate for the current (staff/JWT) tenant — same cached source
    /// BuildOrderAsync uses.</summary>
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

    /// <summary>
    /// Deducts real ingredient stock for every line item: Prepared items consume their
    /// admin-defined Recipe (BOM), scaled by quantity ordered; Independent items decrease
    /// their own linked InventoryItem directly. Every deduction writes an auditable
    /// InventoryTransaction row referencing this order. Stock is allowed to go negative —
    /// insufficient stock never blocks the sale, it's surfaced via low-stock alerts
    /// instead. Prepared items with no recipe yet (nothing built for them in the Recipe
    /// Builder) deduct nothing — logged, not silently ignored.
    /// </summary>
    private async Task ConsumeInventoryAsync(Dictionary<int, MenuItem> menu, List<OrderItem> items, int orderId, int? explicitTenantId = null)
    {
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

        var inventory = await TenantScoped(db.InventoryItems, explicitTenantId)
            .Where(i => inventoryIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id);

        void Deduct(InventoryItem ingredient, double amount)
        {
            var previous = ingredient.Current;
            ingredient.Current -= amount;
            db.InventoryTransactions.Add(new InventoryTransaction
            {
                TenantId = ingredient.TenantId,
                InventoryItemId = ingredient.Id,
                Type = InventoryTransactionType.Sale,
                PreviousStock = previous,
                ChangedQuantity = -amount,
                RemainingStock = ingredient.Current,
                ReferenceId = orderId.ToString(),
            });
        }

        foreach (var line in items)
        {
            if (!menu.TryGetValue(line.MenuItemId, out var menuItem)) continue;

            if (menuItem.ProductType == ProductType.Independent)
            {
                if (menuItem.LinkedInventoryItemId is int linkedId && inventory.TryGetValue(linkedId, out var linked))
                    Deduct(linked, line.Qty);
                continue;
            }

            if (!recipeByMenuItem.TryGetValue(menuItem.Id, out var recipe))
            {
                logger.LogInformation("No recipe defined for menu item {MenuItemName} (id {MenuItemId}) — order {OrderId} deducted no ingredients for this line.", menuItem.Name, menuItem.Id, orderId);
                continue;
            }

            foreach (var recipeItem in recipe.Items)
            {
                if (!inventory.TryGetValue(recipeItem.InventoryItemId, out var ingredient)) continue;
                var amount = UnitConverter.Convert(recipeItem.Quantity * line.Qty, recipeItem.Unit, ingredient.Unit);
                Deduct(ingredient, amount);
            }
        }
    }

    /// <summary>
    /// Identifies a returning customer primarily by phone number (guestPhone, already
    /// digits-only from Create()'s normalization) — a name alone is unreliable, since two
    /// guests can share one and the same guest can spell theirs differently across visits.
    /// Falls back to a name match only when no phone is available (the anonymous QR flow,
    /// which has no phone field yet) — and backfills the phone onto that record once one
    /// does show up, so identity converges onto phone over time instead of staying split.
    /// </summary>
    private async Task<Customer> FindOrCreateCustomerAsync(string guestName, string? guestPhone, int? explicitTenantId = null)
    {
        // Includes FavoriteItems so TrackFavoritesAsync can reuse this same result
        // instead of paying its own separate round trip right after.
        var customersQuery = TenantScoped(db.Customers, explicitTenantId).Include(c => c.FavoriteItems);

        Customer? customer = null;
        if (guestPhone is not null)
            customer = await customersQuery.FirstOrDefaultAsync(c => c.Phone == guestPhone);

        if (customer is null)
        {
            var normalizedName = guestName.Trim().ToLower();
            customer = await customersQuery.FirstOrDefaultAsync(c => c.Name.ToLower() == normalizedName);
        }

        if (customer is not null)
        {
            // Backfill: a legacy/no-phone record just supplied one for the first time.
            if (guestPhone is not null && customer.Phone is null) customer.Phone = guestPhone;
            return customer;
        }

        var slug = new string(guestName.Where(char.IsLetter).ToArray()).ToUpperInvariant();
        customer = new Customer
        {
            Name = guestName,
            Phone = guestPhone,
            ReferralCode = $"{(slug.Length >= 4 ? slug[..4] : slug.PadRight(4, 'X'))}{Random.Shared.Next(100, 999)}",
        };
        if (explicitTenantId is int tid) customer.TenantId = tid;
        db.Customers.Add(customer);
        return customer;
    }

    private static void RecordVisit(Customer customer, decimal amountSpent)
    {
        customer.VisitCount += 1;
        customer.TotalSpent += amountSpent;
        customer.TotalPoints += (int)Math.Floor(amountSpent);
        customer.LastVisitAt = DateTime.UtcNow;
    }

    private void TrackFavorites(Customer customer, List<OrderItem> items, int? explicitTenantId = null)
    {
        // customer.FavoriteItems is already loaded — FindOrCreateCustomerAsync's query
        // includes it, so this no longer needs its own separate round trip. A brand-new
        // customer's collection is simply empty, which behaves the same as the old
        // "skip the lookup" case did.
        var existing = customer.FavoriteItems;

        foreach (var line in items)
        {
            var fav = existing.FirstOrDefault(f => f.MenuItemId == line.MenuItemId);
            if (fav is null)
            {
                // Link via the navigation (not CustomerId) so EF resolves the
                // real id for brand-new customers — see Order.Customer comment.
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
}
