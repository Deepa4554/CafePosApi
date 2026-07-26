using System.Security.Claims;
using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Order pricing/creation/firing core — extracted out of OrdersController so it has a
/// third caller (GuestSessionController's cart/order endpoints) without duplicating
/// pricing, inventory, or CRM behaviour a second time. Originally this logic lived only
/// in OrdersController.Create/CreatePublic; the extraction is purely mechanical — same
/// behaviour, just callable from more than one controller.
/// </summary>
public interface IOrderBuildingService
{
    Task<Order> BuildOrderAsync(
        CafePosDbContext db, string orderType, string? tableCode, string? guestName, List<CreateOrderItemDto> items,
        decimal discountPct, ClaimsPrincipal? user, int? explicitTenantId = null, int? branchId = null,
        string? guestPhone = null, int? servedByStaffId = null, string? guestAddress = null);

    /// <summary>Adds a new unfired cart line or overwrites an existing unfired line's qty for
    /// the same (menuItemId, modifier) pair — qty is the line's FINAL quantity, not a delta.
    /// qty == 0 removes the line. Only deducts inventory for a qty INCREASE (mirrors
    /// OrdersController.AddItem/RemoveItem: stock is debited on add, never credited back on
    /// removal/decrease — an existing, deliberately-unchanged behaviour). Recomputes
    /// order.Subtotal/Tax/Total. Does not save — the caller saves.</summary>
    Task<OrderItem?> AddOrUpdateCartItemAsync(CafePosDbContext db, Order order, int menuItemId, int qty, string? modifier, int explicitTenantId,
        int? variantId = null, List<int>? modifierOptionIds = null);

    /// <summary>Prices one order line: the selected Variant's price (or the item's own base
    /// price if none), plus every selected ModifierOption's price delta — both validated as
    /// actually belonging to this menu item. Also snapshots the item's kitchen station name
    /// (see OrderItem.StationName) — the caller must have loaded menuItem with
    /// .Include(m => m.Station) for this to resolve to anything but the "Kitchen" fallback.
    /// Also resolves the line's tax slab (item's TaxGroup → tenant default group → null,
    /// meaning "bill at CafeSettings.TaxRatePct") for RecomputeTotals to snapshot.
    /// Throws ApiValidationException on an unknown/unavailable variant or an option that
    /// belongs to a different item. Shared by BuildOrderAsync, AddOrUpdateCartItemAsync, and
    /// OrdersController.AddItem so the three order-item-creation paths can never compute this
    /// differently.</summary>
    Task<(decimal Price, string? VariantName, List<OrderItemModifier> Modifiers, string StationName, decimal? TaxRatePct)> ResolveLinePricingAsync(
        CafePosDbContext db, MenuItem menuItem, int? variantId, List<int>? modifierOptionIds, int? explicitTenantId);

    /// <summary>Assigns the next fire-batch number to every not-yet-fired item, creates that
    /// batch's own kitchen-ticket row, notifies the kitchen about just those items, and
    /// deducts their ingredients — this is THE single point inventory is consumed at (see
    /// doc Section 4.1: deduct when the kitchen actually starts cooking, not at order
    /// creation). Returns false if there was nothing new to fire. Recomputes Order.Status
    /// but does not save — the caller saves.</summary>
    Task<bool> FireUnfiredItemsAsync(CafePosDbContext db, Order order, int? explicitTenantId);

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
    Task ConsumeInventoryAsync(CafePosDbContext db, Dictionary<int, MenuItem> menu, List<OrderItem> items, int orderId, int? explicitTenantId = null);
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

    public async Task<Order> BuildOrderAsync(
        CafePosDbContext db, string orderType, string? tableCode, string? guestName, List<CreateOrderItemDto> items,
        decimal discountPct, ClaimsPrincipal? user, int? explicitTenantId = null, int? branchId = null,
        string? guestPhone = null, int? servedByStaffId = null, string? guestAddress = null)
    {
        if (items.Count == 0)
            throw new ApiValidationException("Order must contain at least one item.");

        if (orderType == "DINE_IN")
        {
            if (string.IsNullOrWhiteSpace(tableCode))
                throw new ApiValidationException("Dine-in orders need a tableCode.");

            var tableCheck = await TenantScoped(db.Tables, explicitTenantId)
                .Where(t => t.Code == tableCode)
                .Select(t => new
                {
                    Busy = TenantScoped(db.Orders, explicitTenantId)
                        .Any(o => o.TableCode == tableCode && !o.Cancelled && (!o.Paid || o.Status != OrderStatus.Served)),
                })
                .FirstOrDefaultAsync();
            if (tableCheck is null)
                throw new ApiValidationException($"Table {tableCode} does not exist.");
            if (tableCheck.Busy)
                throw new ApiConflictException($"Table {tableCode} already has an open order.");
        }

        var menuIds = items.Select(i => i.MenuItemId).ToList();
        var menu = await TenantScoped(db.MenuItems, explicitTenantId).Include(m => m.Station)
            .Where(m => menuIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id);

        var orderItems = new List<OrderItem>();
        foreach (var line in items)
        {
            if (!menu.TryGetValue(line.MenuItemId, out var menuItem))
                throw new ApiValidationException($"Menu item {line.MenuItemId} not found.");
            if (!menuItem.Available)
                throw new ApiValidationException($"{menuItem.Name} is currently unavailable.");
            if (line.Qty <= 0)
                throw new ApiValidationException($"Invalid quantity for {menuItem.Name}.");

            var (linePrice, variantName, selections, stationName, lineTaxRatePct) = await ResolveLinePricingAsync(db, menuItem, line.VariantId, line.ModifierOptionIds, explicitTenantId);
            orderItems.Add(new OrderItem
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
                TaxRatePct = lineTaxRatePct,
            });
        }

        var effectiveTenantId = explicitTenantId ?? tenantContext.TenantIdOrDefault;
        var taxRatePct = await GetTaxRatePctAsync(db, effectiveTenantId);
        var subtotal = orderItems.Sum(i => i.Price * i.Qty);
        var clampedDiscountPct = Math.Clamp(discountPct, 0, 100);
        var discountAmount = Math.Round(subtotal * clampedDiscountPct / 100, 2);

        var settings = await TenantScoped(db.Settings, explicitTenantId).FirstAsync();
        var (defaultServiceCharge, defaultPackingCharge, defaultDeliveryCharge) = ComputeDefaultCharges(settings, orderType, subtotal);

        // QSR counter orders get a daily-resetting token instead of a table — stamped here
        // (not inside the SaveChanges transaction below) since the UPSERT is already
        // atomic on its own; a rolled-back order can at worst waste one token number, same
        // as any sequence counter skipping on a failed insert.
        int? tokenNumber = null;
        DateOnly? tokenDate = null;
        if (orderType == "QSR")
        {
            tokenDate = DateOnly.FromDateTime(DateTime.UtcNow);
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
        };
        RecomputeTotals(order, taxRatePct);
        if (explicitTenantId is int tid2) order.TenantId = tid2;
        db.Orders.Add(order);

        var customer = await FindOrCreateCustomerAsync(db, guest ?? "Walk-in Guest", guestPhone, explicitTenantId, guestAddress);
        order.Customer = customer;
        RecordVisit(customer, order.Total);
        TrackFavorites(db, customer, orderItems, explicitTenantId);

        // No inventory deduction here — orders are created "Open"/unfired (see doc Section
        // 4.1). Stock is only consumed once FireUnfiredItemsAsync actually sends items to
        // the kitchen, so a held or abandoned order never leaves a phantom deduction.
        IDbContextTransaction? txn = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync() : null;
        try
        {
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
            order.Subtotal = order.Items.Where(i => !i.Voided).Sum(i => i.Price * i.Qty);
            RecomputeTotals(order, await GetTaxRatePctAsync(db, explicitTenantId));
            return null;
        }

        if (!menuItem.Available)
            throw new ApiValidationException($"{menuItem.Name} is currently unavailable.");

        if (existing is not null)
        {
            existing.Qty = qty;
        }
        else
        {
            var (linePrice, variantName, selections, stationName, taxRatePct) = await ResolveLinePricingAsync(db, menuItem, variantId, modifierOptionIds, explicitTenantId);
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
                TaxRatePct = taxRatePct,
                FireBatch = 0,
            };
            order.Items.Add(existing);
        }

        order.Subtotal = order.Items.Where(i => !i.Voided).Sum(i => i.Price * i.Qty);
        RecomputeTotals(order, await GetTaxRatePctAsync(db, explicitTenantId));

        // No inventory deduction here — this line stays FireBatch == 0 (unfired) until the
        // guest cart's next fire/place-order call, which is the single deduction point
        // (see FireUnfiredItemsAsync).
        return existing;
    }

    public async Task<(decimal Price, string? VariantName, List<OrderItemModifier> Modifiers, string StationName, decimal? TaxRatePct)> ResolveLinePricingAsync(
        CafePosDbContext db, MenuItem menuItem, int? variantId, List<int>? modifierOptionIds, int? explicitTenantId)
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

        // Every modifier group on this item, needed for three separate checks below:
        // option ownership, per-type selection limits, and required-group enforcement.
        var groups = await TenantScoped(db.Modifiers, explicitTenantId)
            .Where(m => m.MenuItemId == menuItem.Id)
            .Select(m => new { m.Id, m.Name, m.Type, m.IsRequired })
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
                selections.Add(new OrderItemModifier
                {
                    ModifierOptionId = option.Id,
                    Name = option.Name,
                    Price = option.Price,
                    Qty = qty,
                });
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
        var taxGroups = await TenantScoped(db.TaxGroups, explicitTenantId)
            .Where(t => t.Id == menuItem.TaxGroupId || t.IsDefault)
            .Select(t => new { t.Id, t.RatePct, t.IsDefault })
            .ToListAsync();
        var taxRatePct = taxGroups.FirstOrDefault(t => t.Id == menuItem.TaxGroupId)?.RatePct
            ?? taxGroups.FirstOrDefault(t => t.IsDefault)?.RatePct;

        return (price, variantName, selections, menuItem.Station?.Name ?? "Kitchen", taxRatePct);
    }

    public async Task<bool> FireUnfiredItemsAsync(CafePosDbContext db, Order order, int? explicitTenantId)
    {
        var unfired = order.Items.Where(i => i.FireBatch == 0).ToList();
        if (unfired.Count == 0) return false;

        order.CurrentFireBatch += 1;
        foreach (var item in unfired) item.FireBatch = order.CurrentFireBatch;
        order.FireBatches.Add(new OrderFireBatch { OrderId = order.Id, BatchNumber = order.CurrentFireBatch });
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

    public void RecomputeBatchStatus(CafePosDbContext db, Order order, int batchNumber)
    {
        var batch = order.FireBatches.FirstOrDefault(b => b.BatchNumber == batchNumber);
        if (batch is null) return;
        // Voided lines are excluded — a voided-while-Preparing item's Status is frozen
        // (nobody advances it further, the kitchen was told to stop), so counting it here
        // would permanently block the batch from ever rolling up to Ready/Served.
        var items = order.Items.Where(i => i.FireBatch == batchNumber && !i.Voided).ToList();
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

    public static void RecomputeOrderStatus(Order order)
    {
        var active = order.FireBatches.Where(b => b.Status != OrderStatus.Served).ToList();
        order.Status = active.Count > 0
            ? active.Min(b => b.Status)
            : (order.FireBatches.Count > 0 ? OrderStatus.Served : OrderStatus.New);
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
    /// understate or overstate the tax due on the other.</summary>
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

        var totalDiscount = o.DiscountAmount + o.BillDiscountAmount + o.CouponDiscountAmount + o.GiftCardAmountApplied + o.LoyaltyDiscountAmount;
        var lines = o.Items.Where(i => !i.Voided).ToList();
        var gross = lines.Sum(i => i.Price * i.Qty);
        var discount = Math.Min(Math.Max(0, totalDiscount), gross);

        decimal tax = 0;
        // The last line absorbs the rounding remainder so the per-line taxable amounts
        // always add back up to the order's taxable total, however the shares divide.
        var allocated = 0m;
        for (var idx = 0; idx < lines.Count; idx++)
        {
            var line = lines[idx];
            var lineGross = line.Price * line.Qty;
            var lineDiscount = idx == lines.Count - 1
                ? discount - allocated
                : (gross > 0 ? Math.Round(discount * (lineGross / gross), 2) : 0);
            allocated += lineDiscount;

            line.TaxableAmount = Math.Max(0, lineGross - lineDiscount);
            line.TaxAmount = Math.Round(line.TaxableAmount * (line.TaxRatePct ?? fallbackTaxRatePct) / 100, 2);
            tax += line.TaxAmount;
        }

        o.Tax = tax;
        o.Total = Math.Max(0, gross - discount) + tax
            + o.ServiceChargeAmount + o.PackingChargeAmount + o.DeliveryChargeAmount + o.TipAmount + o.RoundOffAmount;
    }

    /// <summary>Starting Service/Packing/Delivery charge for a brand-new order, driven purely
    /// by CafeSettings' Auto Charges config (see Entities.CafeSettings) — a charge with no
    /// default set (null) or not enabled for this order type comes back 0, same as a biller
    /// never having touched that tile. Only DINE_IN/TAKEAWAY/DELIVERY are ever matched; QSR/
    /// CASH counter sales don't carry any of these three charges by default.</summary>
    private static (decimal Service, decimal Packing, decimal Delivery) ComputeDefaultCharges(CafeSettings s, string orderType, decimal subtotal)
    {
        var service = s.ServiceChargeDefaultPct is decimal svcPct && AppliesTo(orderType, s.ServiceChargeAutoApplyDineIn, s.ServiceChargeAutoApplyTakeaway, s.ServiceChargeAutoApplyDelivery)
            ? Math.Round(subtotal * svcPct / 100, 2) : 0;
        var packing = s.PackingChargeDefaultAmount is decimal pkgAmt && AppliesTo(orderType, s.PackingChargeAutoApplyDineIn, s.PackingChargeAutoApplyTakeaway, s.PackingChargeAutoApplyDelivery)
            ? pkgAmt : 0;
        var delivery = s.DeliveryChargeDefaultAmount is decimal dlvAmt && AppliesTo(orderType, s.DeliveryChargeAutoApplyDineIn, s.DeliveryChargeAutoApplyTakeaway, s.DeliveryChargeAutoApplyDelivery)
            ? dlvAmt : 0;
        return (service, packing, delivery);
    }

    private static bool AppliesTo(string orderType, bool dineIn, bool takeaway, bool delivery) => orderType switch
    {
        "DINE_IN" => dineIn,
        "TAKEAWAY" => takeaway,
        "DELIVERY" => delivery,
        _ => false,
    };

    private async Task<Customer> FindOrCreateCustomerAsync(CafePosDbContext db, string guestName, string? guestPhone, int? explicitTenantId = null, string? guestAddress = null)
    {
        var customersQuery = TenantScoped(db.Customers, explicitTenantId).Include(c => c.FavoriteItems);

        Customer? customer = null;
        if (guestPhone is not null)
            customer = await customersQuery.FirstOrDefaultAsync(c => c.Phone == guestPhone);

        if (customer is null)
        {
            var normalizedName = guestName.Trim().ToLower();
            customer = await customersQuery.FirstOrDefaultAsync(c => c.Name.ToLower() == normalizedName);
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

    private static void RecordVisit(Customer customer, decimal amountSpent)
    {
        customer.VisitCount += 1;
        customer.TotalSpent += amountSpent;
        customer.TotalPoints += (int)Math.Floor(amountSpent);
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

    public async Task ConsumeInventoryAsync(CafePosDbContext db, Dictionary<int, MenuItem> menu, List<OrderItem> items, int orderId, int? explicitTenantId = null)
    {
        // Idempotency pre-check — skip any line already deducted (e.g. a retried Fire
        // call). The DB unique index on (OrderItemId, InventoryItemId) WHERE Type='Sale'
        // is the hard backstop for a genuine concurrent double-fire; this is just the
        // happy-path guard that avoids hitting it in the common case.
        var candidateItemIds = items.Select(i => i.Id).Where(id => id != 0).ToList();
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

        var inventory = await TenantScoped(db.InventoryItems, explicitTenantId)
            .Where(i => inventoryIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id);

        Task Deduct(InventoryItem ingredient, double amount, int orderItemId) =>
            InventoryBatchService.ConsumeFifoAsync(db, ingredient, amount, InventoryTransactionType.Sale,
                orderId.ToString(), orderItemId, reason: null, wasteReasonCode: null, userId: null, userName: "System");

        async Task TrackMissingRecipeAsync(MenuItem menuItem)
        {
            logger.LogInformation("No recipe defined for menu item {MenuItemName} (id {MenuItemId}) — order {OrderId} deducted no ingredients for this line.", menuItem.Name, menuItem.Id, orderId);
            var alert = await TenantScoped(db.MissingRecipeAlerts, explicitTenantId).FirstOrDefaultAsync(a => a.MenuItemId == menuItem.Id);
            if (alert is null)
            {
                var newAlert = new MissingRecipeAlert { MenuItemId = menuItem.Id };
                if (explicitTenantId is int tid) newAlert.TenantId = tid;
                db.MissingRecipeAlerts.Add(newAlert);
            }
            else
            {
                alert.OccurrenceCount++;
                alert.LastOccurredAt = DateTime.UtcNow;
                alert.Dismissed = false; // a resurfaced gap should reappear even if previously dismissed
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
                await TrackMissingRecipeAsync(menuItem);
                continue;
            }

            foreach (var recipeItem in recipe.Items)
            {
                if (!inventory.TryGetValue(recipeItem.InventoryItemId, out var ingredient)) continue;
                var amount = UnitConverter.Convert(recipeItem.Quantity * line.Qty, recipeItem.Unit, ingredient.Unit);
                await Deduct(ingredient, amount, line.Id);
            }
        }
    }
}
