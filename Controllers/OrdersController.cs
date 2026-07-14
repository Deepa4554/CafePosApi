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
    public async Task<PagedResult<OrderDto>> List([FromQuery] bool activeOnly = false, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] int? branchId = null)
    {
        var query = db.Orders.Include(o => o.Items).AsQueryable();
        // "Active" means still needs attention — matches the table-occupancy rule:
        // an order stays active (visible on KDS, counted as in-progress) until it's
        // BOTH paid AND served. Paying early must not make it vanish from the
        // kitchen's ticket list while the food still hasn't gone out.
        if (activeOnly) query = query.Where(o => !o.Paid || o.Status != OrderStatus.Served);
        // No branch selected -> see everything (single-location cafes, and cafes that
        // haven't set up branches yet, are unaffected). A branch selected -> only that
        // branch's orders; pre-branch-scoping orders (BranchId null) intentionally drop
        // out of a branch-filtered view since they can't be attributed to one.
        if (branchId is int bid) query = query.Where(o => o.BranchId == bid);

        var paged = await query.OrderByDescending(o => o.CreatedAt).ToPagedResultAsync(page, pageSize);
        return new PagedResult<OrderDto>(paged.Items.Select(OrderDto.From).ToList(), paged.Page, paged.PageSize, paged.TotalCount);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<OrderDto>> Get(int id)
    {
        var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id);
        return order is null ? NotFound() : OrderDto.From(order);
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
        return new { token = receiptTokens.Encode(tenantContext.TenantIdOrDefault, id) };
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

        var order = await BuildOrderAsync(req.OrderType, req.TableCode, req.GuestName, req.Items, req.DiscountPct, req.CouponCode, branchId: req.BranchId, guestPhone: normalizedPhone);
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

        var order = await BuildOrderAsync("DINE_IN", tableCode, req.GuestName, req.Items, discountPct: 0, couponCode: null, explicitTenantId: tenantId);
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
        decimal discountPct, string? couponCode, int? explicitTenantId = null, int? branchId = null, string? guestPhone = null)
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
        var discountAmount = Math.Round(subtotal * clampedDiscountPct / 100, 2);

        Coupon? coupon = null;
        if (!string.IsNullOrWhiteSpace(couponCode))
        {
            coupon = await TenantScoped(db.Coupons, explicitTenantId).FirstOrDefaultAsync(c => c.Code == couponCode.ToUpperInvariant());
            if (coupon is null) throw new ApiValidationException("Coupon code is invalid or expired.");
            if (coupon.IsUsed) throw new ApiConflictException("Coupon has already been used.");
            if (coupon.ExpiresAt < DateTime.UtcNow) throw new ApiConflictException("Coupon has expired.");
            if (subtotal < coupon.MinOrderValue) throw new ApiValidationException($"Minimum order value for this coupon is {coupon.MinOrderValue:C}.");

            var couponDiscount = coupon.Type switch
            {
                CouponType.Percent => Math.Round(subtotal * coupon.Value / 100, 2),
                CouponType.Flat => coupon.Value,
                _ => 0,
            };
            discountAmount += couponDiscount;
        }

        var taxable = Math.Max(0, subtotal - discountAmount);
        var tax = Math.Round(taxable * taxRatePct / 100, 2);

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
            Tax = tax,
            Total = taxable + tax,
        };
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

        if (coupon is not null) coupon.IsUsed = true;

        // Explicit transaction (relational providers only — InMemory, used when no
        // Postgres connection string is configured, doesn't support transactions) so
        // that if inventory deduction fails after the order row is written, the whole
        // order rolls back too rather than leaving stock un-deducted for a placed order.
        IDbContextTransaction? txn = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync() : null;
        try
        {
            // Order needs its real, Postgres-assigned Id before inventory transactions
            // (and the discount audit entry below) can reference it, so this happens
            // as its own save inside the transaction.
            await db.SaveChangesAsync();
            NotifyOrderPlaced(order, explicitTenantId);

            // Added to the same tracked context rather than via IAuditService.LogAsync
            // (which does its own immediate SaveChangesAsync) — riding along in the
            // save below instead of costing a third round trip to the remote Postgres
            // instance on every discounted/coupon order.
            if (clampedDiscountPct > 0 || coupon is not null)
            {
                var discountEntry = new AuditLogEntry
                {
                    Action = AuditAction.Discount,
                    Resource = AuditResource.Order,
                    ResourceId = order.Id.ToString(),
                    Details = $"Order {order.Id} applied {(coupon is not null ? $"coupon {coupon.Code}" : $"{clampedDiscountPct}% discount")} (−{discountAmount:C}).",
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

    /// <summary>Moves the order one step along NEW → PREPARING → READY → SERVED (KDS buttons).</summary>
    [HttpPatch("{id:int}/advance")]
    public async Task<ActionResult<OrderDto>> Advance(int id)
    {
        var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();

        var idx = Array.IndexOf(StatusFlow, order.Status);
        if (idx < StatusFlow.Length - 1) order.Status = StatusFlow[idx + 1];
        NotifyIfReady(order);

        await db.SaveChangesAsync();
        return OrderDto.From(order);
    }

    [HttpPatch("{id:int}/status")]
    public async Task<ActionResult<OrderDto>> SetStatus(int id, SetStatusRequest req)
    {
        var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();

        if (!Enum.TryParse<OrderStatus>(req.Status, ignoreCase: true, out var status))
            throw new ApiValidationException($"Unknown status '{req.Status}'.");

        order.Status = status;
        NotifyIfReady(order);
        await db.SaveChangesAsync();
        return OrderDto.From(order);
    }

    /// <summary>Fires an in-app "ready to serve" notification the moment an order hits
    /// READY, so waiters/staff watching Notifications (not just the KDS screen) know to
    /// go pick it up.</summary>
    private void NotifyIfReady(Order order)
    {
        if (order.Status != OrderStatus.Ready) return;
        db.Notifications.Add(new AppNotification
        {
            Title = "Order ready to serve",
            Body = $"{order.Title} is ready — {order.Items.Count} item{(order.Items.Count == 1 ? "" : "s")}, ₹{order.Total:0.00}.",
            Category = NotificationCategory.Order,
            Channel = NotificationChannel.InApp,
            ActionUrl = $"/orders/{order.Id}",
        });
    }

    /// <summary>Fires the moment a new order is placed (fired to the kitchen) — this is
    /// the ONLY notification category Chef/KitchenStaff logins see (see
    /// NotificationsController.List's role filter); everyone else sees this alongside
    /// every other category exactly as before.</summary>
    private void NotifyOrderPlaced(Order order, int? explicitTenantId)
    {
        var notification = new AppNotification
        {
            Title = "New order placed",
            Body = $"{order.Title} — {order.Items.Count} item{(order.Items.Count == 1 ? "" : "s")}, ₹{order.Total:0.00}.",
            Category = NotificationCategory.OrderPlaced,
            Channel = NotificationChannel.InApp,
            ActionUrl = $"/orders/{order.Id}",
        };
        // Anonymous QR orders have no JWT to auto-stamp the tenant from — same reason
        // order.TenantId is set explicitly a few lines up in BuildOrderAsync.
        if (explicitTenantId is int tid) notification.TenantId = tid;
        db.Notifications.Add(notification);
    }

    /// <summary>
    /// Marks the bill paid. Does NOT force the order to Served — a table only frees up
    /// once it's BOTH paid AND served (see TablesController/PublicController's occupancy
    /// check). Paying before the kitchen has actually served the food shouldn't free the
    /// table; the guest is still sitting there.
    /// </summary>
    [HttpPatch("{id:int}/pay")]
    public async Task<ActionResult<OrderDto>> Pay(int id)
    {
        var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        if (order.Paid) throw new ApiConflictException("Order is already paid.");

        order.Paid = true;
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
        var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id);
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
