using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>
/// Books third-party riders for DELIVERY orders. Every call to Borzo goes through here rather
/// than from the app, for one reason that shapes the whole file: the Borzo token is a spendable
/// credential. Shipped to the RN client it would sit in a JS bundle anyone can read, and whoever
/// read it could book rides on the cafe's account. So the app asks this controller, and this
/// controller — already authenticated, already tenant-scoped — is what holds the token.
///
/// Booking is never automatic. Quote is free and creates nothing; Book spends the cafe's money
/// and puts a real person on a real road, so it only ever happens on an explicit press.
/// </summary>
[ApiController]
[Route("api/delivery")]
[Authorize]
public class DeliveryController(
    CafePosDbContext db,
    BorzoClient borzo,
    IAuditService audit,
    ILogger<DeliveryController> logger) : ControllerBase
{
    /// <summary>Longest prep time the kitchen can claim. Not a Borzo limit — a sanity bound, so a
    /// fat-fingered "300" doesn't schedule a rider for five hours' time and quietly strand an
    /// order that everyone assumes is on its way.</summary>
    private const int MaxPrepMinutes = 120;

    // ---------- Settings ----------

    [HttpGet("settings")]
    [Authorize(Policy = Policies.OwnerOrManager)]
    public async Task<BorzoSettingsDto> GetSettings()
    {
        var s = await db.Settings.AsNoTracking().FirstAsync();
        return Describe(s, CallbackBaseUrl());
    }

    [HttpPut("settings")]
    [Authorize(Policy = Policies.OwnerOrManager)]
    public async Task<BorzoSettingsDto> UpdateSettings(UpdateBorzoSettingsRequest req)
    {
        var s = await db.Settings.FirstAsync();

        if (req.Enabled is bool enabled) s.BorzoEnabled = enabled;
        if (req.UseTestEnvironment is bool useTest) s.BorzoUseTestEnvironment = useTest;
        if (req.PassFeeToCustomer is bool passFee) s.BorzoPassFeeToCustomer = passFee;
        if (req.CollectCod is bool collectCod) s.BorzoCollectCodOnDelivery = collectCod;
        if (req.PickupAddress is not null) s.PickupAddress = req.PickupAddress.Trim();
        if (req.PickupLatitude is decimal lat) s.PickupLatitude = lat;
        if (req.PickupLongitude is decimal lng) s.PickupLongitude = lng;

        // An empty string is how the settings screen clears a saved token; null means "not
        // editing the token in this save", which is what lets the other fields be updated
        // without the client having to hold the secret just to send it back.
        if (req.AuthToken is not null)
            s.BorzoAuthToken = string.IsNullOrWhiteSpace(req.AuthToken) ? null : req.AuthToken.Trim();
        if (req.CallbackToken is not null)
            s.BorzoCallbackToken = string.IsNullOrWhiteSpace(req.CallbackToken) ? null : req.CallbackToken.Trim();

        await db.SaveChangesAsync();
        // The token's value is never logged — only that it changed.
        await audit.LogAsync(AuditAction.SettingsChange, AuditResource.Settings, null,
            $"Delivery partner settings updated (enabled={s.BorzoEnabled}, sandbox={s.BorzoUseTestEnvironment})",
            AuditSeverity.Medium);

        return Describe(s, CallbackBaseUrl());
    }

    /// <summary>This server's own origin, read off the current request rather than hardcoded, so
    /// the composed callback URL is correct in every environment (production, a Render preview,
    /// local dev) without a config value to keep in sync.</summary>
    private string CallbackBaseUrl() => $"{Request.Scheme}://{Request.Host}";

    // ---------- Quote ----------

    /// <summary>
    /// What a rider would cost for this order, without booking one. Safe to call as often as the
    /// UI likes — Borzo's calculate-order neither creates an order nor charges anything.
    /// </summary>
    [HttpGet("orders/{orderId:int}/quote")]
    public async Task<DeliveryQuoteDto> Quote(int orderId, CancellationToken ct)
    {
        var settings = await db.Settings.AsNoTracking().FirstAsync();
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw new ApiValidationException("Order not found.");

        if (Blocker(settings, order) is string blocker)
            return new DeliveryQuoteDto(false, null, settings.BorzoPassFeeToCustomer, blocker);

        var result = await borzo.CalculateAsync(BuildBooking(settings, order, prepMinutes: null), ct);
        return new DeliveryQuoteDto(result.Ok, result.Fee, settings.BorzoPassFeeToCustomer, result.Message);
    }

    // ---------- Book ----------

    /// <summary>
    /// Puts a real rider on this order. Spends the cafe's Borzo balance, so it's Owner/Manager
    /// only and refuses to run twice for the same order — a double press would book, and bill
    /// for, two riders.
    /// </summary>
    [HttpPost("orders/{orderId:int}/book")]
    [Authorize(Policy = Policies.OwnerOrManager)]
    public async Task<DeliveryStatusDto> Book(int orderId, BookRiderRequest req, CancellationToken ct)
    {
        if (req.PrepMinutes < 0 || req.PrepMinutes > MaxPrepMinutes)
            throw new ApiValidationException($"Prep time must be between 0 and {MaxPrepMinutes} minutes.");

        var settings = await db.Settings.AsNoTracking().FirstAsync();
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw new ApiValidationException("Order not found.");

        if (order.CourierOrderId is not null)
            throw new ApiValidationException("A rider is already booked for this order.");
        if (Blocker(settings, order) is string blocker)
            throw new ApiValidationException(blocker);

        var result = await borzo.CreateAsync(BuildBooking(settings, order, req.PrepMinutes), ct);
        if (!result.Ok || result.Order?.OrderId is null)
            throw new ApiValidationException(result.Message ?? "Couldn’t book a rider for this order.");

        order.CourierProvider = "borzo";
        order.CourierOrderId = result.Order.OrderId.Value.ToString();
        order.CourierStatus = result.Order.Status;
        order.CourierTrackingUrl = result.TrackingUrl;
        order.CourierFeeAmount = result.Fee;
        order.CourierRiderName = result.Order.Courier?.FullName;
        order.CourierRiderPhone = result.Order.Courier?.Phone;
        order.CourierBookedAt = DateTime.UtcNow;

        // Only the customer-facing charge is conditional. What the courier costs is recorded
        // either way (CourierFeeAmount above) — a cafe absorbing the fee still needs to see it
        // when it works out what delivery is costing it.
        if (settings.BorzoPassFeeToCustomer && result.Fee is decimal fee)
            order.DeliveryChargeAmount = fee;

        await db.SaveChangesAsync(ct);
        await audit.LogAsync(AuditAction.Create, AuditResource.Order, order.Id.ToString(),
            $"Rider booked via Borzo (courier order {order.CourierOrderId}, ₹{result.Fee:0.00}, prep {req.PrepMinutes} min)",
            AuditSeverity.Medium);

        return Describe(order);
    }

    // ---------- Status / cancel ----------

    [HttpGet("orders/{orderId:int}")]
    public async Task<DeliveryStatusDto> Status(int orderId, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw new ApiValidationException("Order not found.");
        return Describe(order);
    }

    [HttpPost("orders/{orderId:int}/cancel")]
    [Authorize(Policy = Policies.OwnerOrManager)]
    public async Task<DeliveryStatusDto> Cancel(int orderId, CancellationToken ct)
    {
        var settings = await db.Settings.AsNoTracking().FirstAsync();
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw new ApiValidationException("Order not found.");
        if (order.CourierOrderId is null)
            throw new ApiValidationException("No rider is booked for this order.");

        var result = await borzo.CancelAsync(settings.BorzoAuthToken, settings.BorzoUseTestEnvironment, order.CourierOrderId, ct);
        if (!result.Ok) throw new ApiValidationException(result.Message ?? "Couldn’t cancel the rider.");

        order.CourierStatus = result.Order?.Status ?? "canceled";
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(AuditAction.Create, AuditResource.Order, order.Id.ToString(),
            $"Rider booking cancelled (courier order {order.CourierOrderId})", AuditSeverity.Medium);

        return Describe(order);
    }

    // ---------- Callback ----------

    /// <summary>
    /// Where Borzo posts order/delivery status changes. Anonymous by necessity — Borzo has no
    /// PrabandhOS login — so it is deliberately built to be worthless to an attacker: it matches
    /// on the courier order id we already stored, and writes nothing but a status string. It can
    /// neither create an order nor move money, and an unrecognised id is answered 200 and dropped
    /// (Borzo retries for 24 hours on any non-2xx, and there is nothing to retry here).
    ///
    /// Verified against CafeSettings.BorzoCallbackToken — see that property's doc comment for
    /// why this checks a URL-embedded secret rather than a Borzo-issued signature: no documented
    /// header or hash scheme for their callbacks could be found, and guessing one wrong would be
    /// worse than not verifying (false confidence, not real protection). Soft-disabled for a
    /// tenant that hasn't saved a callback token yet, so this stays backward compatible with
    /// whatever's already registered in Borzo's cabinet.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("callback")]
    public async Task<IActionResult> Callback(CancellationToken ct)
    {
        // Body read by hand rather than via [FromBody] for one reason: System.Text.Json silently
        // drops JSON properties a DTO doesn't declare, so if Borzo carries its callback token as a
        // field the BorzoCallbackPayload type doesn't know about, model binding would throw it away
        // before this method ever saw it. Reading the raw text keeps everything Borzo actually sent.
        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync(ct);

        // TEMPORARY DIAGNOSTIC — remove once the real callback shape is confirmed. Borzo's own docs
        // don't document how their per-URL callback token is delivered (header? body field?), so
        // this logs the whole incoming request once so the first real callback can settle it, and
        // proper verification can then be wired to the actual format instead of a guess. It logs
        // Borzo's own outbound payload only — no PrabandhOS user secret passes through here.
        logger.LogInformation(
            "[BORZO CALLBACK DIAGNOSTIC] headers={Headers} query={Query} body={Body}",
            string.Join(" | ", Request.Headers.Select(h => $"{h.Key}: {h.Value}")),
            Request.QueryString.Value,
            rawBody);

        BorzoCallbackPayload? payload;
        try
        {
            // BorzoCallbackPayload/BorzoOrder carry explicit [JsonPropertyName] attributes, so no
            // naming policy is needed for their declared fields to bind.
            payload = System.Text.Json.JsonSerializer.Deserialize<BorzoCallbackPayload>(rawBody);
        }
        catch (System.Text.Json.JsonException)
        {
            return Ok(); // Unparseable body — nothing to act on, and no point making Borzo retry it.
        }

        var courierOrderId = payload?.Order?.OrderId?.ToString();
        if (string.IsNullOrWhiteSpace(courierOrderId)) return Ok();

        // IgnoreQueryFilters: a callback arrives with no tenant context at all, and the courier
        // order id is globally unique to Borzo, so this is the one lookup in the app that has to
        // reach across tenants. It reads and writes exactly one status field on one matched row.
        var order = await db.Orders.IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.CourierOrderId == courierOrderId, ct);
        if (order is null)
        {
            logger.LogInformation("Borzo callback for unknown courier order {CourierOrderId}", courierOrderId);
            return Ok();
        }

        // Only now — having matched an order — is a tenant known, and only now can its own
        // callback token be checked. Rejecting up front (before the lookup) would leak, via
        // response timing, whether a guessed order id exists at all; this rejects exactly like
        // the unknown-order case above once a mismatch is found.
        var expectedToken = await db.Settings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == order.TenantId)
            .Select(s => s.BorzoCallbackToken)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrEmpty(expectedToken) && !CallbackTokenMatches(expectedToken))
        {
            logger.LogWarning("Borzo callback for order {OrderId} had a missing/incorrect callback token", order.Id);
            return Ok();
        }

        // payload is non-null here: courierOrderId came off payload?.Order?.OrderId and was
        // checked non-empty above, which a null payload could never have produced.
        var borzoOrder = payload!.Order;
        order.CourierStatus = borzoOrder?.Status ?? order.CourierStatus;
        if (borzoOrder?.Courier is BorzoCourier courier)
        {
            order.CourierRiderName = courier.FullName ?? order.CourierRiderName;
            order.CourierRiderPhone = courier.Phone ?? order.CourierRiderPhone;
        }
        await db.SaveChangesAsync(ct);
        return Ok();
    }

    /// <summary>Constant-time comparison against this request's `?token=` query parameter — a
    /// naive string.Equals short-circuits on the first mismatched character, which leaks how
    /// many leading characters a guess got right through response-time differences (same
    /// reasoning as ReceiptTokenService's own signature compare).</summary>
    private bool CallbackTokenMatches(string expectedToken)
    {
        var supplied = Request.Query["token"].ToString();
        if (supplied.Length != expectedToken.Length) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expectedToken), System.Text.Encoding.UTF8.GetBytes(supplied));
    }

    // ---------- Helpers ----------

    /// <summary>Why this order can't go to a courier right now, or null if it can. One place, so
    /// Quote and Book can never disagree about what "ready" means.</summary>
    private static string? Blocker(CafeSettings s, Order order)
    {
        if (!s.BorzoEnabled) return "Delivery partner is switched off. Turn it on in Integrations → Delivery Partner.";
        if (string.IsNullOrWhiteSpace(s.BorzoAuthToken)) return "No Borzo token saved yet.";
        if (s.PickupLatitude is null || s.PickupLongitude is null)
            return "The cafe’s pickup location isn’t pinned yet — set it in Integrations → Delivery Partner.";
        // Borzo requires an address string and a contact phone on every point, not just
        // coordinates (its own calculate-order returns "address: required" / "phone: required"
        // otherwise) — the pickup ones come from cafe settings and are easy to leave blank after
        // only pinning the map, which is exactly what makes a quote fail with no obvious reason.
        if (string.IsNullOrWhiteSpace(s.PickupAddress) && string.IsNullOrWhiteSpace(s.Address))
            return "The cafe’s pickup address isn’t set — add it in Integrations → Delivery Partner.";
        if (string.IsNullOrWhiteSpace(s.Phone))
            return "The cafe’s own phone number isn’t set — add it in Cafe Profile so the rider has a pickup contact.";
        if (order.OrderType != "DELIVERY") return "Only delivery orders can be sent to a rider.";
        if (order.DeliveryLatitude is null || order.DeliveryLongitude is null)
            return "This order has no delivery location — the customer didn’t share it when ordering.";
        if (string.IsNullOrWhiteSpace(order.GuestPhone))
            return "This order has no customer phone number, which the rider needs.";
        return null;
    }

    private static BorzoBooking BuildBooking(CafeSettings s, Order order, int? prepMinutes) => new()
    {
        AuthToken = s.BorzoAuthToken,
        UseTestEnvironment = s.BorzoUseTestEnvironment,

        PickupAddress = string.IsNullOrWhiteSpace(s.PickupAddress) ? s.Address : s.PickupAddress,
        PickupLatitude = s.PickupLatitude,
        PickupLongitude = s.PickupLongitude,
        PickupContactName = s.BusinessName,
        PickupContactPhone = s.Phone,
        // ToOffset off the real UTC instant, NOT new DateTimeOffset(IstClock.NowIst, Offset):
        // IstClock.NowIst is a Kind=Utc DateTime holding IST wall-clock, and the DateTimeOffset
        // constructor throws when a Utc-kind value is paired with a non-zero offset — which is
        // what 500'd every Book (Quote passes null here, so it never hit this path).
        PickupReadyAt = prepMinutes is int minutes
            ? DateTimeOffset.UtcNow.ToOffset(IstClock.Offset).AddMinutes(minutes)
            : null,

        DropoffAddress = order.DeliveryAddress,
        DropoffLatitude = order.DeliveryLatitude,
        DropoffLongitude = order.DeliveryLongitude,
        DropoffContactName = order.GuestName,
        DropoffContactPhone = order.GuestPhone,

        ClientOrderId = order.Id.ToString(),
        // COD only when the cafe has explicitly turned it on AND the order isn't already paid.
        // Default-off matters: an account without Borzo's COD agreement rejects the entire order
        // with cod_agreement_required the moment is_cod_cash_voucher_required is set, so this
        // never sends it uninvited. A prepaid order never collects either — that'd double-charge.
        CashToCollect = s.BorzoCollectCodOnDelivery && !order.Paid ? order.Total : null,
    };

    private static BorzoSettingsDto Describe(CafeSettings s, string callbackBaseUrl) => new(
        s.BorzoEnabled,
        !string.IsNullOrWhiteSpace(s.BorzoAuthToken),
        s.BorzoUseTestEnvironment,
        s.BorzoPassFeeToCustomer,
        s.BorzoCollectCodOnDelivery,
        string.IsNullOrWhiteSpace(s.PickupAddress) ? s.Address : s.PickupAddress,
        s.PickupLatitude,
        s.PickupLongitude,
        s.BorzoEnabled
            && !string.IsNullOrWhiteSpace(s.BorzoAuthToken)
            && s.PickupLatitude is not null
            && s.PickupLongitude is not null
            // Same required-on-every-point fields Borzo enforces (see Blocker) — a cafe that
            // pinned the map but left its address or phone blank is not actually book-ready.
            && (!string.IsNullOrWhiteSpace(s.PickupAddress) || !string.IsNullOrWhiteSpace(s.Address))
            && !string.IsNullOrWhiteSpace(s.Phone),
        string.IsNullOrWhiteSpace(s.BorzoCallbackToken)
            ? null
            : $"{callbackBaseUrl}/api/delivery/callback?token={Uri.EscapeDataString(s.BorzoCallbackToken)}");

    private static DeliveryStatusDto Describe(Order o) => new(
        o.Id,
        o.DeliveryAddress,
        o.DeliveryLatitude is not null && o.DeliveryLongitude is not null,
        o.CourierProvider, o.CourierOrderId, o.CourierStatus, o.CourierTrackingUrl,
        o.CourierFeeAmount, o.CourierRiderName, o.CourierRiderPhone, o.CourierBookedAt);
}

/// <summary>Borzo's callback envelope — only the fields this app acts on. Their order and delivery
/// callbacks differ in shape; both carry the order block this reads.</summary>
public record BorzoCallbackPayload
{
    [System.Text.Json.Serialization.JsonPropertyName("order")]
    public BorzoOrder? Order { get; init; }
}
