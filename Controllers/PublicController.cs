using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>
/// Anonymous, TENANT-AWARE endpoints for the customer-facing QR ordering page
/// (PublicOrderPageController) — every route takes an encrypted QrToken (see
/// QrTokenService) that resolves to (tenantId, tableCode) server-side. Neither the
/// cafe's identity nor the table code ever appears in plain text in the URL/querystring
/// a customer's phone holds — only the opaque token does, and it's authenticated
/// (tamper-evident) so it can't be edited to point at a different table.
/// Everything here bypasses the normal global query filter deliberately
/// (IgnoreQueryFilters + explicit TenantId match): there's no JWT to derive a tenant
/// from, so the decoded token IS the tenant signal.
/// </summary>
[ApiController]
[Route("api/public")]
[AllowAnonymous]
public class PublicController(
    CafePosDbContext db,
    QrTokenService qrTokens,
    ReceiptTokenService receiptTokens,
    IOrderBuildingService orderBuilder,
    IRealtimeNotifier realtime) : ControllerBase
{
    /// <summary>
    /// The bill-PDF link sent over WhatsApp after an order is paid — see
    /// ReceiptTokenService for why the order id is never exposed in plain text here.
    /// Generated fresh on every request straight from the order's current DB state
    /// (see ReceiptPdfBuilder) rather than a stored file, so it can never go stale.
    /// </summary>
    [HttpGet("receipt/{token}")]
    public async Task<IActionResult> GetReceipt(string token)
    {
        var orderId = receiptTokens.TryDecode(token);
        if (orderId is null) return NotFound();

        // Payments are needed as well as Items: the scan-to-pay QR ReceiptPdfBuilder adds is
        // charged on what's still outstanding, and an unloaded collection would read as zero
        // paid and ask a part-paid guest for the whole bill again.
        var order = await db.Orders.IgnoreQueryFilters().Include(o => o.Items).ThenInclude(i => i.SelectedModifiers)
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == orderId.Value);
        if (order is null) return NotFound();

        var settings = await db.Settings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.TenantId == order.TenantId);
        if (settings is null) return NotFound();

        var pdfBytes = ReceiptPdfBuilder.Build(settings, order);
        return File(pdfBytes, "application/pdf");
    }


    [HttpGet("{token}/table")]
    public async Task<ActionResult<object>> GetTable(string token)
    {
        var decoded = qrTokens.TryDecode(token);
        if (decoded is null) return NotFound();
        var (tenantId, tableCode) = decoded.Value;

        // `Mode` tells the page which of the three QRs was scanned — it is what makes the
        // delivery flow opt-in rather than something the dine-in page has to reason about.
        var mode = QrTokenService.ModeFor(tableCode);

        // A delivery QR belongs to no seat and never joins a table session; the page swaps in
        // the address + location step on the strength of this alone.
        if (mode == "delivery")
            return new { Mode = mode, Code = (string?)null, Zone = (string?)null, Seats = (int?)null, Occupied = false };

        // Empty table code == the generic "menu only, no table" token (see
        // TablesController.GetMenuOnlyQrToken) — CustomerOrderPage renders this as a
        // takeaway/counter order instead of showing a table number.
        if (string.IsNullOrEmpty(tableCode))
            return new { Mode = mode, Code = (string?)null, Zone = (string?)null, Seats = (int?)null, Occupied = false };

        var table = await db.Tables.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Code == tableCode);
        if (table is null) return NotFound();

        var busy = await db.Orders.IgnoreQueryFilters()
            .AnyAsync(o => o.TenantId == tenantId && o.TableCode == tableCode && !o.Cancelled && (!o.Paid || o.Status != OrderStatus.Served));
        return new { Mode = mode, table.Code, table.Zone, table.Seats, Occupied = busy };
    }

    /// <summary>
    /// Places a home-delivery order from the delivery QR. Deliberately its own endpoint rather
    /// than a branch inside the dine-in path: that path is built around a table, a seat, and a
    /// shared guest session that several phones fight over, and none of that exists here. One
    /// scan, one customer, one order — so there is no session to join, nothing to lock, and the
    /// live dine-in flow is not touched at all.
    ///
    /// The order lands as an ordinary DELIVERY order in the cafe's list. No rider is booked and
    /// nothing is spent: the kitchen accepts it, sets its own prep time, and presses Book rider
    /// (DeliveryController) when it means to.
    /// </summary>
    [HttpPost("{token}/delivery-order")]
    public async Task<ActionResult<object>> CreateDeliveryOrder(string token, CreateDeliveryOrderRequest req, CancellationToken ct)
    {
        var decoded = qrTokens.TryDecode(token);
        if (decoded is null) throw new ApiValidationException("This ordering link is invalid. Please re-scan the QR code.");
        var (tenantId, tableCode) = decoded.Value;

        // Only the delivery QR opens this door. A table or menu-only token reaching here would
        // mean a hand-edited request, not a scan.
        if (QrTokenService.ModeFor(tableCode) != "delivery")
            throw new ApiValidationException("This QR code isn’t set up for home delivery.");

        if (req.Items is null || req.Items.Count == 0)
            throw new ApiValidationException("Add at least one item before placing the order.");

        var name = req.GuestName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new ApiValidationException("Please enter your name.");
        if (name.Length > MaxDeliveryNameLength) throw new ApiValidationException($"Name can be at most {MaxDeliveryNameLength} characters.");

        // The rider phones this number from the gate, so unlike dine-in it isn't optional.
        var phone = new string((req.GuestPhone ?? "").Where(char.IsDigit).ToArray());
        if (phone.Length != 10) throw new ApiValidationException("Enter a 10-digit mobile number so the rider can call you.");

        var address = req.Address?.Trim();
        if (string.IsNullOrWhiteSpace(address)) throw new ApiValidationException("Please enter your delivery address.");
        if (address.Length > MaxDeliveryAddressLength)
            throw new ApiValidationException($"Address can be at most {MaxDeliveryAddressLength} characters.");

        // Coordinates are optional here on purpose. A customer who declines the location prompt
        // should still be able to order — the cafe simply can't dispatch a rider automatically,
        // and DeliveryController.Blocker says exactly that when someone presses Book rider.
        // Rejecting the order instead would trade a completed sale for a booking convenience.
        var (lat, lng) = ValidateCoordinates(req.Latitude, req.Longitude);

        // guestAddress flows into the CRM customer record (FindOrCreateCustomerAsync) so the
        // cafe's customer list learns where this person lives; DeliveryAddress below is this
        // one order's destination, which is a different fact — people order to work, to a
        // friend's flat, to a hotel.
        var order = await orderBuilder.BuildOrderAsync(
            db, "DELIVERY", tableCode: null, guestName: name, items: req.Items,
            discountPct: 0, user: null, explicitTenantId: tenantId, guestPhone: phone, guestAddress: address);

        order.DeliveryAddress = address;
        order.DeliveryLatitude = lat;
        order.DeliveryLongitude = lng;

        // Same gate a table's QR order passes through (Staff-Confirm Mode), and for a stronger
        // reason: a prank dine-in order wastes food, a prank delivery order can also send a paid
        // rider across town. MarkPendingConfirmation holds the items unfired and alerts the floor
        // — PendingOrdersHost already picks it up with no changes, since it filters on pending
        // status, not order type, and shows Title ("Delivery – Priya") when there's no table.
        var settings = await db.Settings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        var pendingConfirmation = settings?.RequireStaffOrderConfirmation ?? true;
        if (pendingConfirmation) orderBuilder.MarkPendingConfirmation(db, order, tenantId);

        await db.SaveChangesAsync(ct);

        // Same push the POS uses, so the order appears on the cafe's screens as it is placed
        // rather than whenever someone next refreshes.
        await realtime.NotifyOrdersChangedAsync(new HashSet<int> { tenantId });

        return new {
            order.Id,
            order.Title,
            order.Total,
            HasLocation = lat is not null,
            PendingConfirmation = pendingConfirmation,
            // Lets the customer's own browser poll DeliveryOrderStatus below for exactly this
            // order, without exposing a plain /order/{id} lookup that would let anyone holding
            // the (shared, printed) delivery QR enumerate every other customer's order status.
            // Reuses the same signed-id scheme as the bill-PDF link (ReceiptTokenService) rather
            // than inventing a second one.
            OrderToken = receiptTokens.Encode(order.Id),
        };
    }

    /// <summary>
    /// What the customer's own confirmation screen polls while Staff-Confirm Mode holds their
    /// order — "has the cafe looked at this yet". Deliberately the bare minimum: pending/
    /// cancelled and a total, nothing about who else ordered or what's in any other order. The
    /// token (not the order id) is what's public, for the same reason GetReceipt below uses one
    /// — Order.Id is a small sequential integer, and a plain /orders/{id}/status route would
    /// let anyone holding the printed delivery QR walk every order this cafe has ever taken.
    /// </summary>
    [HttpGet("delivery-order-status/{orderToken}")]
    public async Task<ActionResult<object>> DeliveryOrderStatus(string orderToken, CancellationToken ct)
    {
        var orderId = receiptTokens.TryDecode(orderToken);
        if (orderId is null) return NotFound();

        var order = await db.Orders.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == orderId.Value, ct);
        if (order is null) return NotFound();

        return new {
            order.PendingStaffConfirmation,
            order.Cancelled,
            order.CancelReason,
            order.Total,
            CourierTrackingUrl = order.CourierTrackingUrl,
        };
    }

    private const int MaxDeliveryNameLength = 60;
    /// <summary>Long enough for a real Indian address with landmark and floor, short enough that
    /// the field can't be used to stuff the database from an anonymous endpoint.</summary>
    private const int MaxDeliveryAddressLength = 300;

    /// <summary>Keeps a nonsense pin out of the courier request. Either both coordinates are
    /// present and in range, or neither is stored — half a location is worse than none, since it
    /// would look dispatchable right up until the rider was sent somewhere off the map.</summary>
    private static (decimal? Lat, decimal? Lng) ValidateCoordinates(decimal? lat, decimal? lng)
    {
        if (lat is null || lng is null) return (null, null);
        if (lat is < -90 or > 90 || lng is < -180 or > 180)
            throw new ApiValidationException("That location doesn’t look right. Try sharing your location again.");
        return (lat, lng);
    }

    [HttpGet("{token}/menu-items")]
    public async Task<ActionResult<IEnumerable<MenuItem>>> GetMenu(string token)
    {
        var decoded = qrTokens.TryDecode(token);
        if (decoded is null) return NotFound();

        return Ok(await db.MenuItems.IgnoreQueryFilters()
            .Where(m => m.TenantId == decoded.Value.TenantId)
            .OrderBy(m => m.Category).ThenBy(m => m.Name)
            .ToListAsync());
    }

    [HttpGet("{token}/settings")]
    public async Task<ActionResult<CafeSettings>> GetSettings(string token)
    {
        var decoded = qrTokens.TryDecode(token);
        if (decoded is null) return NotFound();

        var settings = await db.Settings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.TenantId == decoded.Value.TenantId);
        return settings is null ? NotFound() : settings;
    }

    private const int PublicBestSellerCount = 5;

    /// <summary>
    /// Always aims for a full row of PublicBestSellerCount items so a customer never
    /// scans into an empty/sparse "Best Sellers" strip: real units-sold in the last 30
    /// days first, then Popular-flagged items, then any other available item — so even a
    /// brand-new cafe with zero order history still shows a full row (as long as the menu
    /// itself has that many items).
    /// </summary>
    [HttpGet("{token}/best-sellers")]
    public async Task<ActionResult<IEnumerable<MenuItem>>> GetBestSellers(string token)
    {
        var decoded = qrTokens.TryDecode(token);
        if (decoded is null) return NotFound();
        var tenantId = decoded.Value.TenantId;

        var cutoff = DateTime.UtcNow.AddDays(-30);
        var sales = await (
            from oi in db.OrderItems.IgnoreQueryFilters()
            join o in db.Orders.IgnoreQueryFilters() on oi.OrderId equals o.Id
            where o.TenantId == tenantId && o.CreatedAt >= cutoff
            group oi.Qty by oi.MenuItemId into g
            select new { MenuItemId = g.Key, UnitsSold = g.Sum() })
            .OrderByDescending(x => x.UnitsSold)
            .Take(PublicBestSellerCount)
            .ToListAsync();

        var salesIds = sales.Select(s => s.MenuItemId).ToList();
        var menuById = await db.MenuItems.IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && salesIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id);

        var results = sales
            .Where(s => menuById.ContainsKey(s.MenuItemId))
            .Select(s => menuById[s.MenuItemId])
            .ToList();

        if (results.Count < PublicBestSellerCount)
        {
            var usedIds = results.Select(m => m.Id).ToHashSet();
            var fallback = await db.MenuItems.IgnoreQueryFilters()
                .Where(m => m.TenantId == tenantId && m.Available && !usedIds.Contains(m.Id))
                .OrderByDescending(m => m.Popular).ThenBy(m => m.Category).ThenBy(m => m.Name)
                .Take(PublicBestSellerCount - results.Count)
                .ToListAsync();
            results.AddRange(fallback);
        }

        return Ok(results);
    }
}
