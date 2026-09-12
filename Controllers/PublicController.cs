using System.Text;
using System.Text.Json;
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
    IWhatsAppEventPublisher whatsApp,
    CafeLogoLoader logoLoader,
    TenantSecretProtector secrets,
    ITenantRazorpayClient tenantRazorpay,
    ILogger<PublicController> logger,
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

        var logo = await logoLoader.LoadAsync(settings.LogoUrl);
        var pdfBytes = ReceiptPdfBuilder.Build(settings, order, logo);
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

        // Neither a delivery nor a counter QR belongs to a seat, and neither joins a table
        // session. The page swaps in the address + location step, or the token flow, on the
        // strength of this alone.
        if (mode is "delivery" or "counter")
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
    /// Places a counter order from the till's token QR. Shaped on CreateDeliveryOrder, not on the
    /// dine-in path: there is no seat and no shared guest session, just one scan and one order.
    /// The difference from delivery is what it leaves out — no address, no coordinates, no rider —
    /// and what it hands back: the QSR token number the customer will be called by, which
    /// BuildOrderAsync allocates for us the moment the order type is QSR.
    ///
    /// Staff confirmation is FORCED ON here, whatever the cafe's own RequireStaffOrderConfirmation
    /// says. Every other QR flow has something behind it — a seat someone is sitting in, a phone
    /// number a rider will call — but a counter card is scannable by anyone walking past the shop,
    /// and a token number handed out before a human agreed to it is a queue position for food
    /// nobody is making. The page therefore shows the number only once staff accept (see
    /// CounterOrderStatus).
    /// </summary>
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("WaitlistJoinLimiter")]
    [HttpPost("{token}/counter-order")]
    public async Task<ActionResult<object>> CreateCounterOrder(string token, CreateCounterOrderRequest req, CancellationToken ct)
    {
        var decoded = qrTokens.TryDecode(token);
        if (decoded is null) throw new ApiValidationException("This ordering link is invalid. Please re-scan the QR code.");
        var (tenantId, tableCode) = decoded.Value;

        if (QrTokenService.ModeFor(tableCode) != "counter")
            throw new ApiValidationException("This QR code isn’t set up for counter orders.");

        if (req.Items is null || req.Items.Count == 0)
            throw new ApiValidationException("Add at least one item before placing the order.");

        var settings = await db.Settings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

        // A cafe that has switched counter/token service off has no Token Dashboard open to work
        // these from, so the order would land somewhere nobody is looking. Refused with a reason
        // rather than accepted into a screen that isn't staffed.
        if (settings is not null && !settings.QsrEnabled)
            throw new ApiValidationException("This cafe isn’t taking counter orders right now. Please order at the till.");

        var name = req.GuestName?.Trim();
        if (name is { Length: > MaxDeliveryNameLength })
            throw new ApiValidationException($"Name can be at most {MaxDeliveryNameLength} characters.");

        // Optional, unlike delivery: nobody has to be phoned: the customer is standing in the
        // shop and gets called by token number. Kept when given so the bill can be sent on
        // WhatsApp and the visit joins their CRM record.
        var phone = new string((req.GuestPhone ?? "").Where(char.IsDigit).ToArray());
        if (phone.Length is not 0 and not 10)
            throw new ApiValidationException("Enter a 10-digit mobile number, or leave it blank.");

        var order = await orderBuilder.BuildOrderAsync(
            db, "QSR", tableCode: null, guestName: string.IsNullOrWhiteSpace(name) ? null : name, items: req.Items,
            discountPct: 0, user: null, explicitTenantId: tenantId,
            guestPhone: phone.Length == 10 ? phone : null);

        orderBuilder.MarkPendingConfirmation(db, order, tenantId);
        await db.SaveChangesAsync(ct);

        await realtime.NotifyOrdersChangedAsync(new HashSet<int> { tenantId });

        // No TokenNumber in this reply on purpose — see the summary above. The page polls
        // CounterOrderStatus and shows the number when staff have accepted the order.
        return new {
            order.Id,
            order.Total,
            OrderToken = receiptTokens.Encode(order.Id),
        };
    }

    /// <summary>
    /// What the counter customer's own phone polls after ordering, scoped to this one order's
    /// signed token (same scheme as DeliveryOrderStatus — a plain /orders/{id} lookup would let
    /// anyone holding the shared printed QR walk every other customer's order).
    ///
    /// Carries TokenNumber only once staff have confirmed, so the page cannot show a queue
    /// position for an order still awaiting a human. Keeps answering through to settlement, since
    /// the bill PDF the customer is handed at the end is only meaningful after the till settles.
    /// </summary>
    [HttpGet("counter-order-status/{orderToken}")]
    public async Task<ActionResult<object>> CounterOrderStatus(string orderToken, CancellationToken ct)
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
            Status = order.Status.ToString(),
            order.Paid,
            TokenNumber = order.PendingStaffConfirmation ? null : order.TokenNumber,
        };
    }

    /// <summary>
    /// Lets a counter/token customer add another round of items to the SAME order after it's
    /// already been placed. Closes a gap the QR page otherwise had only for this flow: a dine-in
    /// guest can hit "Add more items" because a table GuestSession exists to append to, but a
    /// counter order has neither a table nor a session (see CreateCounterOrder) — there was
    /// previously no way back into an order once sent, so a second order for the same person had
    /// no choice but to queue as a second, unrelated token.
    ///
    /// Scoped by the order's own signed receipt token (same scheme as CounterOrderStatus), not
    /// the QR token — the QR token only identifies the cafe, this identifies the one order, so a
    /// customer can only ever add to their own. Restricted to QSR orders on purpose: this token
    /// scheme is shared with dine-in/delivery receipt links (see ReceiptTokenService), and those
    /// order types have their own session/locking rules this endpoint doesn't implement.
    ///
    /// New lines land unfired (FireBatch 0, see AddOrUpdateCartItemAsync) exactly like items on a
    /// brand-new counter order. If staff had already accepted this order (TokenNumber assigned,
    /// PendingStaffConfirmation false), this re-raises that flag — a token number already handed
    /// out must not be silently expanded with food nobody agreed to cook. Staff re-confirming
    /// (OrdersController.ConfirmOrder) then fires only the newly-added lines as their own batch
    /// under the SAME token; whatever's already fired/served is untouched.
    /// </summary>
    [HttpPost("counter-order/{orderToken}/items")]
    public Task<ActionResult<object>> AddCounterOrderItems(string orderToken, AddCounterOrderItemsRequest req, CancellationToken ct) =>
        DbConcurrency.InTransactionAsync<ActionResult<object>>(db, async () =>
    {
        var orderId = receiptTokens.TryDecode(orderToken);
        if (orderId is null) throw new ApiValidationException("We couldn't find this order. Please ask a staff member.");

        if (req.Items is null || req.Items.Count == 0)
            throw new ApiValidationException("Add at least one item first.");

        await DbConcurrency.LockRowsAsync<Order>(db, orderId.Value);
        var order = await db.Orders.IgnoreQueryFilters()
            .Include(o => o.Items).ThenInclude(i => i.SelectedModifiers)
            .FirstOrDefaultAsync(o => o.Id == orderId.Value, ct);
        if (order is null) return NotFound();
        if (order.OrderType != "QSR") throw new ApiValidationException("This order can't take more items here. Please ask a staff member.");
        if (order.Cancelled) throw new ApiValidationException("This order was cancelled. Please ask a staff member.");
        if (order.Paid) throw new ApiValidationException("This order is already settled — please place a new order.");

        foreach (var line in req.Items)
            await orderBuilder.AddOrUpdateCartItemAsync(db, order, line.MenuItemId, line.Qty, line.Modifier, order.TenantId, line.VariantId, line.ModifierOptionIds);

        orderBuilder.MarkPendingConfirmation(db, order, order.TenantId);
        await db.SaveChangesAsync(ct);

        await realtime.NotifyOrdersChangedAsync(new HashSet<int> { order.TenantId });

        return new { order.Id, order.Total };
    });

    /// <summary>
    /// Opens a Razorpay order for a guest paying their own bill from the same tab they ordered
    /// in. Charged against the CAFE's own Razorpay account, never the platform's — see
    /// CafeSettings.RazorpayKeyId for why that distinction is the whole design.
    ///
    /// Returns the cafe's public key id with the order so the page can open checkout inline; the
    /// guest is already holding the phone, so this is one tap into their UPI app rather than a QR
    /// they would have to scan with the device showing it.
    ///
    /// Deliberately does NOT settle anything. The browser telling us it paid is not proof — the
    /// bill is closed only by RazorpayWebhook below, on Razorpay's own signed word.
    /// </summary>
    [HttpPost("{token}/pay-bill")]
    public async Task<ActionResult<object>> CreateBillPayment(string token, PayBillRequest req, CancellationToken ct)
    {
        var decoded = qrTokens.TryDecode(token);
        if (decoded is null) throw new ApiValidationException("This link is invalid. Please re-scan the QR code.");
        var (tenantId, _) = decoded.Value;

        var orderId = receiptTokens.TryDecode(req.OrderToken ?? "");
        if (orderId is null) throw new ApiValidationException("We couldn't find this bill. Please ask a staff member.");

        var order = await db.Orders.IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.Id == orderId.Value && o.TenantId == tenantId, ct);
        if (order is null) return NotFound();
        if (order.Cancelled) throw new ApiValidationException("This order was cancelled.");
        if (order.Paid) throw new ApiValidationException("This bill has already been settled.");

        var creds = await LoadRazorpayCredentialsAsync(tenantId, ct);
        if (creds is null) throw new ApiValidationException("This cafe isn't taking online payments.");

        // Whatever is still owed, not the whole total: a bill part-settled at the till (a split,
        // a deposit) must not ask the guest for the full amount again.
        var due = order.Total - order.Payments.Sum(p => p.Amount);
        if (due <= 0) throw new ApiValidationException("There's nothing left to pay on this bill.");
        var amountPaise = (long)Math.Round(due * 100m, MidpointRounding.AwayFromZero);

        RazorpayOrder rzpOrder;
        try
        {
            rzpOrder = await tenantRazorpay.CreateOrderAsync(creds, amountPaise,
                receipt: $"bill-{order.Id}",
                // Read back from Razorpay in the webhook, so which order a payment settles never
                // depends on anything the browser said.
                notes: new Dictionary<string, string>
                {
                    ["tenantId"] = tenantId.ToString(),
                    ["orderId"] = order.Id.ToString(),
                });
        }
        catch (RazorpayApiException ex)
        {
            throw new ApiValidationException(ex.Message);
        }

        return new { KeyId = creds.KeyId, RazorpayOrderId = rzpOrder.Id, AmountPaise = amountPaise, Currency = "INR" };
    }

    /// <summary>
    /// Razorpay's own callback, one URL per cafe — the tenant is named in the path because the
    /// signature can only be checked against THAT cafe's webhook secret, and there is no other
    /// way to know whose secret to reach for before the body has been trusted.
    ///
    /// This, not the browser, is what settles a bill. A guest's phone reporting success can be
    /// replayed, faked or simply lost when they close the tab on the payment screen; Razorpay's
    /// signed `payment.captured` cannot.
    /// </summary>
    [HttpPost("razorpay-webhook/{token}")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> RazorpayWebhook(string token, CancellationToken ct)
    {
        var decoded = qrTokens.TryDecode(token);
        if (decoded is null) return Unauthorized();
        var (tenantId, _) = decoded.Value;

        // Read before anything can consume the stream — the HMAC covers Razorpay's exact bytes.
        string rawBody;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            rawBody = await reader.ReadToEndAsync(ct);

        var creds = await LoadRazorpayCredentialsAsync(tenantId, ct);
        if (creds is null || !creds.CanVerifyWebhook
            || !RazorpaySignature.IsValidWebhook(rawBody, Request.Headers["X-Razorpay-Signature"].FirstOrDefault(), creds.WebhookSecret))
        {
            // Also the "not configured" path: without the secret there is no way to tell a real
            // Razorpay call from anyone else's, so unconfigured must reject everything.
            logger.LogWarning("Rejected a Razorpay webhook for tenant {TenantId} (bad or missing signature)", tenantId);
            return Unauthorized();
        }

        string? eventName, rzpOrderId;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            eventName = doc.RootElement.TryGetProperty("event", out var e) ? e.GetString() : null;
            var entity = doc.RootElement.GetProperty("payload").GetProperty("payment").GetProperty("entity");
            rzpOrderId = entity.TryGetProperty("order_id", out var o) ? o.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Signature was valid, so this really is Razorpay — just an event shape we don't
            // handle. 200 on purpose: a retry would deliver the same body and fail the same way.
            logger.LogWarning(ex, "Razorpay webhook body wasn't in the expected payment shape; ignoring");
            return Ok();
        }

        if (!string.Equals(eventName, "payment.captured", StringComparison.OrdinalIgnoreCase)) return Ok();
        if (string.IsNullOrWhiteSpace(rzpOrderId)) return Ok();

        // Which bill this paid comes from Razorpay's own copy of the notes we stamped at
        // creation, never from the webhook body's free-form fields.
        RazorpayOrder rzpOrder;
        try
        {
            rzpOrder = await tenantRazorpay.GetOrderAsync(creds, rzpOrderId);
        }
        catch (RazorpayApiException)
        {
            // Worth a retry, and Razorpay redelivers on a non-2xx.
            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        if (rzpOrder.Notes is null
            || !rzpOrder.Notes.TryGetValue("orderId", out var orderIdRaw) || !int.TryParse(orderIdRaw, out var orderId)
            || !rzpOrder.Notes.TryGetValue("tenantId", out var noteTenant) || noteTenant != tenantId.ToString())
        {
            logger.LogWarning("Razorpay order {RzpOrderId} settled for tenant {TenantId} carried no matching bill note", rzpOrderId, tenantId);
            return Ok();
        }

        await SettleOnlinePaymentAsync(tenantId, orderId, rzpOrder.AmountPaid / 100m, ct);
        return Ok();
    }

    /// <summary>Closes a bill that Razorpay has confirmed as captured. Idempotent: Razorpay
    /// redelivers a webhook it didn't get a 2xx for, and the same capture arriving twice must
    /// not write a second tender or double the takings.</summary>
    private async Task SettleOnlinePaymentAsync(int tenantId, int orderId, decimal amount, CancellationToken ct)
    {
        var order = await db.Orders.IgnoreQueryFilters()
            .Include(o => o.Items).Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.TenantId == tenantId, ct);
        if (order is null || order.Cancelled) return;
        if (order.Paid) return; // already settled — the redelivery case

        order.Payments.Add(new OrderPayment
        {
            TenantId = tenantId,
            OrderId = order.Id,
            Method = "UPI",
            Amount = amount,
            LedgerIndex = order.Payments.Count,
        });

        // Settles, and nothing else. Deliberately does NOT mark the food served on the guest's
        // behalf: a table frees on Paid AND Served, and whether the food actually went out is
        // the kitchen's fact to state, not a payment's. In the ordinary case the KDS has already
        // served everything by the time a guest asks for the bill, so this settle is the last
        // thing the table was waiting on and it frees on its own.
        await OrderBuildingService.CloseOrderAsync(db, order);
        await db.SaveChangesAsync(ct);
        await realtime.NotifyOrdersChangedAsync(new HashSet<int> { tenantId });
    }

    /// <summary>This cafe's decrypted Razorpay credentials, or null when online payments are off
    /// or half-configured — callers must treat those two the same way.</summary>
    private async Task<RazorpayCredentials?> LoadRazorpayCredentialsAsync(int tenantId, CancellationToken ct)
    {
        var settings = await db.Settings.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (settings is null || !settings.OnlinePaymentEnabled) return null;

        var keySecret = secrets.TryUnprotect(settings.RazorpayKeySecretEnc);
        if (string.IsNullOrWhiteSpace(settings.RazorpayKeyId) || string.IsNullOrWhiteSpace(keySecret)) return null;

        return new RazorpayCredentials(settings.RazorpayKeyId, keySecret, secrets.TryUnprotect(settings.RazorpayWebhookSecretEnc));
    }

    /// <summary>
    /// A guest asking the floor for something from any QR that has a floor to ask — "Call Waiter"
    /// or "Request Bill". Anonymous, like every other endpoint here, and rate limited for the same
    /// reason the waitlist join is: there is no login and no device to blame a burst on.
    ///
    /// Raising a call is all this does. Requesting the bill from a TABLE also locks that table's
    /// guest session (see GuestSessionController.RequestBill) — that stays where it is, because it
    /// needs the session this endpoint deliberately doesn't have. The two are separate on purpose:
    /// a call is a message to a person, session locking is a rule about the order, and a counter
    /// guest can want the first without there being a second.
    ///
    /// Refused for the delivery QR: there is nobody to walk over, and the cafe already holds that
    /// customer's phone number, which is the actual channel for "something is wrong".
    /// </summary>
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("WaitlistJoinLimiter")]
    [HttpPost("{token}/call")]
    public async Task<IActionResult> RaiseGuestCall(string token, GuestCallRequest req, CancellationToken ct)
    {
        var decoded = qrTokens.TryDecode(token);
        if (decoded is null) throw new ApiValidationException("This link is invalid. Please re-scan the QR code.");
        var (tenantId, tableCode) = decoded.Value;

        var mode = QrTokenService.ModeFor(tableCode);
        if (mode is not ("table" or "counter"))
            throw new ApiValidationException("There's no one to call from this QR code.");

        if (!Enum.TryParse<GuestCallKind>(req.Kind, ignoreCase: true, out var kind))
            throw new ApiValidationException("Unknown request.");

        int? orderId = null;
        int? tokenNumber = null;
        if (!string.IsNullOrWhiteSpace(req.OrderToken) && receiptTokens.TryDecode(req.OrderToken) is int decodedOrderId)
        {
            var order = await db.Orders.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == decodedOrderId && o.TenantId == tenantId, ct);
            if (order is not null)
            {
                orderId = order.Id;
                tokenNumber = order.TokenNumber;
            }
        }

        // One open call per (table/order, kind). Pressing the button again while nobody has come
        // yet is a guest getting impatient, not a second table needing service — a new row each
        // time would stack the floor screen with duplicates of the one thing already on it.
        var already = await db.GuestCalls.IgnoreQueryFilters().AnyAsync(c =>
            c.TenantId == tenantId
            && c.Status == GuestCallStatus.Open
            && c.Kind == kind
            && (tableCode == "#TOKEN" ? c.OrderId == orderId : c.TableCode == tableCode), ct);

        if (!already)
        {
            db.GuestCalls.Add(new GuestCall
            {
                TenantId = tenantId,
                Kind = kind,
                TableCode = mode == "table" ? tableCode : null,
                OrderId = orderId,
                TokenNumber = tokenNumber,
            });
            await db.SaveChangesAsync(ct);
        }

        // Answered the same either way: from the guest's side "we've told them" is true whether
        // this raised the call or found one already standing.
        return NoContent();
    }

    /// <summary>
    /// Joins the waitlist from the entrance QR — no session, no order, just Name/Phone/PartySize
    /// added as a Waiting row for staff to see on TableManagementScreen's Waiting tab. Rate
    /// limited per IP (see WaitlistJoinLimiter in Program.cs) since, unlike the dine-in/delivery
    /// flows, there's no per-cafe device or session to blame a burst on.
    /// </summary>
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("WaitlistJoinLimiter")]
    [HttpPost("{token}/waitlist")]
    public async Task<IActionResult> JoinWaitlist(string token, JoinWaitlistRequest req)
    {
        var decoded = qrTokens.TryDecode(token);
        if (decoded is null) throw new ApiValidationException("This link is invalid. Please re-scan the QR code.");
        var (tenantId, tableCode) = decoded.Value;

        if (QrTokenService.ModeFor(tableCode) != "waitlist")
            throw new ApiValidationException("This QR code isn’t set up for the waitlist.");

        var name = req.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new ApiValidationException("Please enter your name.");
        if (name.Length > 60) throw new ApiValidationException("Name can be at most 60 characters.");

        var phone = new string((req.Phone ?? "").Where(char.IsDigit).ToArray());
        if (phone.Length != 10) throw new ApiValidationException("Enter a 10-digit mobile number so staff can call you.");

        if (req.PartySize < 1 || req.PartySize > 50) throw new ApiValidationException("Party size must be between 1 and 50.");

        // No ambient tenant context on an anonymous request — StampTenantIds only fills TenantId
        // in when it's still 0, so the decoded token's tenant has to be set explicitly here.
        db.WaitlistEntries.Add(new WaitlistEntry
        {
            TenantId = tenantId,
            Name = name,
            Phone = phone,
            PartySize = req.PartySize,
        });
        await db.SaveChangesAsync();

        return NoContent();
    }

    /// <summary>
    /// What the customer's own tracking screen polls, from the moment the order is placed all
    /// the way through to settlement — "where is my order now". Deliberately the bare minimum:
    /// pending/cancelled/kitchen-stage/paid and a total, nothing about who else ordered or what's
    /// in any other order. The token (not the order id) is what's public, for the same reason
    /// GetReceipt below uses one — Order.Id is a small sequential integer, and a plain
    /// /orders/{id}/status route would let anyone holding the printed delivery QR walk every
    /// order this cafe has ever taken.
    ///
    /// Status/Paid were added alongside PendingStaffConfirmation rather than replacing it: the
    /// page still needs to tell "cafe hasn't looked yet" apart from "cafe has it and is cooking"
    /// even though both precede Ready. Paid is the actual terminal signal — CustomerOrderPage
    /// stops polling on it, not on Status, since a delivery order can sit at Served for a while
    /// before the cafe settles it at the door/on handoff.
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
            Status = order.Status.ToString(),
            order.Paid,
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

    /// <summary>
    /// Serves the cafe's uploaded PDF menu for a scanned QR (see MenuPdf). This is the target
    /// PublicOrderPageController redirects a general (menu-only) QR to when the cafe has an
    /// enabled PDF — and it's also what the admin screen previews. Content-Disposition inline
    /// so a phone opens it in its built-in PDF viewer rather than force-downloading it.
    ///
    /// Deliberately re-checks Enabled here, not just at redirect time: this URL is public and
    /// stable, so a cafe that turns the PDF off must have it disappear from anyone who saved or
    /// re-scans the link, falling back to a 404 (which the general QR's live page handles).
    /// </summary>
    [HttpGet("{token}/menu-pdf")]
    public async Task<IActionResult> GetMenuPdf(string token)
    {
        var decoded = qrTokens.TryDecode(token);
        if (decoded is null) return NotFound();

        var pdf = await db.MenuPdfs.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == decoded.Value.TenantId && p.Enabled);
        if (pdf is null) return NotFound();

        Response.Headers.ContentDisposition = $"inline; filename=\"{pdf.FileName}\"";
        return File(pdf.Data, "application/pdf");
    }

    [HttpGet("{token}/menu-items")]
    public async Task<ActionResult<IEnumerable<MenuItem>>> GetMenu(string token)
    {
        var decoded = qrTokens.TryDecode(token);
        if (decoded is null) return NotFound();

        return Ok(await db.MenuItems.IgnoreQueryFilters()
            .Where(m => m.TenantId == decoded.Value.TenantId)
            .Include(m => m.Variants.Where(v => v.IsAvailable).OrderBy(v => v.SortOrder))
            .Include(m => m.Modifiers.OrderBy(mo => mo.SortOrder)).ThenInclude(mo => mo.Options.OrderBy(o => o.SortOrder))
            .OrderBy(m => m.Category).ThenBy(m => m.Name)
            .ToListAsync());
    }

    /// <summary>How many past bills a lookup ever returns — a customer wants their recent
    /// visits, and a longer list is only useful to someone who shouldn't have it.</summary>
    private const int PastBillCount = 6;

    /// <summary>
    /// A returning customer's own past bills at THIS cafe, looked up by the name and mobile
    /// number they already give while ordering.
    ///
    /// There is no OTP behind this, so the design assumes the lookup itself can be attempted
    /// by someone who isn't the customer, and makes that not worth doing:
    ///
    ///  - It sits behind the table's QrToken, like every route here. Someone has to hold a
    ///    real QR from this cafe to ask at all — this is not open to the internet.
    ///  - Name AND number must both match. A number alone gets nothing.
    ///  - Only the date and the amount come back. No items, no address, and deliberately no
    ///    receipt token: the actual bill goes to the number over WhatsApp (see SendMyBills),
    ///    where only the person actually holding that number can read it. That split is what
    ///    stands in for verification.
    ///  - BillLookupLimiter caps attempts per IP far below anything that could grind through
    ///    a number range (see Program.cs).
    ///
    /// Unpaid/cancelled orders are excluded — a bill that was never settled isn't history yet,
    /// it's a live order someone could be sitting with.
    /// </summary>
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("BillLookupLimiter")]
    [HttpPost("{token}/my-bills")]
    public async Task<ActionResult<IEnumerable<PastBillDto>>> MyBills(string token, PastBillsRequest req)
    {
        var decoded = qrTokens.TryDecode(token);
        if (decoded is null) return NotFound();
        var tenantId = decoded.Value.TenantId;

        var customer = await ResolveBillCustomerAsync(tenantId, req);
        if (customer is null) return Ok(Array.Empty<PastBillDto>());

        var bills = await db.Orders.IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId && o.CustomerId == customer.Id && o.Paid && !o.Cancelled)
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Take(PastBillCount)
            .Select(o => new { o.BillNumber, o.Id, o.CreatedAt, o.Total })
            .ToListAsync();

        // Formatted after materialising, not in the projection: OrderNumberFormat.Bill is
        // plain C# and EF has no SQL translation for it.
        return Ok(bills.Select(b => new PastBillDto(OrderNumberFormat.Bill(b.BillNumber, b.Id), b.CreatedAt, b.Total)));
    }

    /// <summary>
    /// Sends ONE of the customer's own past bills to the WhatsApp number they just typed.
    ///
    /// This is where the real bill lives — MyBills above deliberately shows only a date and an
    /// amount, because a screen can be read by whoever is holding the phone. The PDF goes to
    /// the number instead, so only someone who actually has that number receives it. That is
    /// what stands in for the OTP this flow doesn't have.
    ///
    /// Re-validates name+phone from scratch rather than trusting anything the page sends back:
    /// this endpoint is reachable directly, so the bill number alone must never be enough.
    /// Always answers 202 — "queued if there was anything to queue" — so it can't be used to
    /// probe which numbers or bill numbers exist.
    /// </summary>
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("BillLookupLimiter")]
    [HttpPost("{token}/my-bills/send")]
    public async Task<IActionResult> SendMyBill(string token, SendMyBillRequest req)
    {
        var decoded = qrTokens.TryDecode(token);
        if (decoded is null) return NotFound();
        var tenantId = decoded.Value.TenantId;

        var customer = await ResolveBillCustomerAsync(tenantId, new PastBillsRequest(req.Name, req.Phone));
        if (customer is null) return Accepted();

        // Scoped to this customer's own orders, so a guessed bill number off someone else's
        // visit resolves to nothing rather than to their bill.
        var order = await db.Orders.IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId && o.CustomerId == customer.Id && o.Paid && !o.Cancelled)
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Take(PastBillCount)
            .Select(o => new { o.Id, o.BillNumber })
            .ToListAsync();

        var match = order.FirstOrDefault(o => OrderNumberFormat.Bill(o.BillNumber, o.Id) == req.Number);
        if (match is not null) await whatsApp.NotifyBillGeneratedAsync(tenantId, match.Id);

        return Accepted();
    }

    /// <summary>
    /// The customer a name+phone pair identifies at this cafe, or null when the pair doesn't
    /// identify anyone. Shared by both bill routes so they can never drift apart — the send
    /// route being even slightly laxer than the list route is exactly how the PDF would leak.
    ///
    /// Both fields are required, and the name has to be long enough to narrow anything: a
    /// single letter would match much of a cafe's customer list and make the check decorative.
    /// </summary>
    private async Task<Customer?> ResolveBillCustomerAsync(int tenantId, PastBillsRequest req)
    {
        var phone = new string((req.Phone ?? "").Where(char.IsDigit).ToArray());
        var name = (req.Name ?? "").Trim();
        if (phone.Length != 10 || name.Length < 2) return null;

        var customer = await db.Customers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Phone == phone);
        return customer is not null && NameMatches(customer.Name, name) ? customer : null;
    }

    /// <summary>
    /// True when the typed name plausibly belongs to the stored one. Compared on the first
    /// word only, case-insensitively: a customer who was saved as "Raj Kumar" types "Raj" the
    /// next time (or the other way round), and demanding an exact match would just teach them
    /// the feature is broken. Still requires knowing the name — which is the point.
    /// </summary>
    private static bool NameMatches(string stored, string typed)
    {
        static string FirstWord(string s) => s.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var w, ..] ? w : "";
        return string.Equals(FirstWord(stored), FirstWord(typed), StringComparison.OrdinalIgnoreCase);
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
            .Include(m => m.Variants.Where(v => v.IsAvailable).OrderBy(v => v.SortOrder))
            .Include(m => m.Modifiers.OrderBy(mo => mo.SortOrder)).ThenInclude(mo => mo.Options.OrderBy(o => o.SortOrder))
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
                .Include(m => m.Variants.Where(v => v.IsAvailable).OrderBy(v => v.SortOrder))
                .Include(m => m.Modifiers.OrderBy(mo => mo.SortOrder)).ThenInclude(mo => mo.Options.OrderBy(o => o.SortOrder))
                .OrderByDescending(m => m.Popular).ThenBy(m => m.Category).ThenBy(m => m.Name)
                .Take(PublicBestSellerCount - results.Count)
                .ToListAsync();
            results.AddRange(fallback);
        }

        return Ok(results);
    }
}
