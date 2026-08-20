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
/// Razorpay Standard Checkout for subscription plans — the self-service half of what
/// SubscriptionController.ChangePlan does by hand. That endpoint stayed PlatformAdminOnly
/// because, with no gateway wired up, letting an Owner call it would have been a free
/// upgrade button; here the upgrade is only applied after Razorpay's own signature proves
/// the money moved, so the Owner can drive it themselves.
///
/// Three things are deliberately never taken from the browser: the price (looked up in
/// SubscriptionPricing), which plan a payment was for (stamped into the Razorpay order's
/// notes at creation and read back from Razorpay on verification), and whether the payment
/// succeeded (the HMAC signature plus the order's own status decide that).
/// </summary>
[ApiController]
[Route("api/payments")]
[Authorize(Policy = Policies.OwnerOnly)]
public class PaymentsController(
    CafePosDbContext db,
    IRazorpayClient razorpay,
    IAuditService audit,
    ITenantContext tenantContext,
    ISubscriptionCache subscriptions,
    ILogger<PaymentsController> logger) : ControllerBase
{
    /// <summary>Notes keys we stamp on the Razorpay order and trust on the way back —
    /// they come from Razorpay, not the client, so they're as good as our own state.</summary>
    private const string TenantNote = "tenantId";
    private const string PlanNote = "plan";
    private const string CycleNote = "cycle";

    [HttpPost("create-order")]
    public async Task<ActionResult<CreateOrderResponse>> CreateOrder(CreateSubscriptionOrderRequest req)
    {
        if (!razorpay.IsConfigured)
            return GatewayUnavailable("Online payments aren't set up yet — contact your PrabandhOS provider to upgrade.");

        var amountPaise = SubscriptionPricing.PaiseFor(req.Plan, req.Cycle);
        if (amountPaise is null)
            throw new ApiValidationException($"The {SubscriptionPricing.DisplayName(req.Plan)} plan can't be bought online — contact your PrabandhOS provider.");

        var tenantId = tenantContext.TenantIdOrDefault;
        // Razorpay caps receipt at 40 characters and only uses it as YOUR reference on the
        // dashboard — tenant + timestamp is enough to find the cafe a payment belongs to
        // without opening the notes.
        var receipt = $"sub-{tenantId}-{DateTime.UtcNow:yyMMddHHmmss}";

        RazorpayOrder order;
        try
        {
            order = await razorpay.CreateOrderAsync(amountPaise.Value, "INR", receipt, new Dictionary<string, string>
            {
                [TenantNote] = tenantId.ToString(),
                [PlanNote] = req.Plan.ToString(),
                [CycleNote] = req.Cycle.ToString(),
            });
        }
        catch (RazorpayApiException ex)
        {
            return GatewayUnavailable(ex.Message);
        }

        return new CreateOrderResponse(
            order.Id, order.Amount, order.Currency, razorpay.KeyId,
            $"PrabandhOS {SubscriptionPricing.DisplayName(req.Plan)} — {SubscriptionPricing.CycleLabel(req.Cycle)}");
    }

    [HttpPost("verify")]
    public async Task<ActionResult<VerifyPaymentResponse>> Verify(VerifyPaymentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.RazorpayOrderId) || string.IsNullOrWhiteSpace(req.RazorpayPaymentId)
            || string.IsNullOrWhiteSpace(req.RazorpaySignature))
            throw new ApiValidationException("Incomplete payment details — nothing has been charged to your plan.");

        if (!razorpay.IsValidPaymentSignature(req.RazorpayOrderId, req.RazorpayPaymentId, req.RazorpaySignature))
        {
            // Never a normal failure mode: a real Razorpay callback always signs correctly.
            // Loud in the log, and emphatically no plan change.
            logger.LogWarning("Rejected a Razorpay payment with a bad signature: order {OrderId}, payment {PaymentId}, tenant {TenantId}",
                req.RazorpayOrderId, req.RazorpayPaymentId, tenantContext.TenantIdOrDefault);
            throw new ApiValidationException("We couldn't verify this payment. If money left your account, contact support before trying again.");
        }

        // A valid signature only proves "this payment belongs to this order". What that order
        // was FOR, and whether it's actually been paid, comes from Razorpay itself.
        RazorpayOrder order;
        try
        {
            order = await razorpay.GetOrderAsync(req.RazorpayOrderId);
        }
        catch (RazorpayApiException ex)
        {
            return GatewayUnavailable(ex.Message);
        }

        var tenantId = tenantContext.TenantIdOrDefault;
        var notes = order.Notes ?? [];
        if (!notes.TryGetValue(TenantNote, out var orderTenant) || orderTenant != tenantId.ToString())
        {
            logger.LogWarning("Tenant {TenantId} tried to redeem Razorpay order {OrderId}, which belongs to {OrderTenant}",
                tenantId, order.Id, orderTenant ?? "(none)");
            throw new ApiValidationException("This payment doesn't belong to your cafe.");
        }

        if (!TryReadPlanNotes(order, out var plan, out var cycle))
            throw new ApiValidationException("We couldn't tell which plan this payment was for — contact support.");

        // "paid" is Razorpay's own word for "the full order amount has been captured". An
        // order sitting at "attempted" means the money was authorised but not captured (an
        // account with auto-capture switched off, or a payment still being processed) — the
        // signature is genuine either way, so this is a wait, not a fraud case. The webhook
        // below is the other half of this: when it does clear, payment.captured applies the
        // plan without the owner having to come back and reopen the screen.
        if (!string.Equals(order.Status, "paid", StringComparison.OrdinalIgnoreCase) || order.AmountPaid < order.Amount)
            throw new ApiValidationException("This payment hasn't been captured yet. Give it a minute, then reopen this screen — your plan updates on its own once it clears.");

        var sub = await ApplyPaidOrderAsync(order, req.RazorpayPaymentId, tenantId, plan, cycle);
        return await BuildResponseAsync(sub);
    }

    /// <summary>
    /// Razorpay's server-to-server notification that a payment was captured. This exists
    /// because Verify above only runs if the browser is still there to call it: an owner who
    /// pays and then closes the tab (or loses signal, or is on a UPI app that takes its time)
    /// has been charged and would otherwise never get the plan they paid for.
    ///
    /// AllowAnonymous is required — Razorpay has no login here — so the HMAC over the raw body
    /// IS the authentication. Everything the endpoint acts on afterwards is re-fetched from
    /// Razorpay by order id rather than read out of the request: the body only says WHICH
    /// order to look at, exactly like Verify only trusts the ids in its callback.
    /// </summary>
    [HttpPost("webhook")]
    [AllowAnonymous]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> Webhook()
    {
        // Read before anything can consume the stream — the HMAC has to cover Razorpay's exact
        // bytes, so this action deliberately takes no [FromBody] parameter.
        string rawBody;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            rawBody = await reader.ReadToEndAsync();

        if (!razorpay.IsValidWebhookSignature(rawBody, Request.Headers["X-Razorpay-Signature"].FirstOrDefault()))
        {
            // Also the "not configured yet" path (blank WebhookSecret can't validate anything),
            // which is why this says nothing specific back — an unauthenticated caller learns
            // only that it was rejected.
            logger.LogWarning("Rejected a Razorpay webhook with a bad or missing signature (webhook configured: {Configured})",
                razorpay.IsWebhookConfigured);
            return Unauthorized();
        }

        string? eventName, orderId, paymentId;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            eventName = doc.RootElement.TryGetProperty("event", out var e) ? e.GetString() : null;
            var entity = doc.RootElement.GetProperty("payload").GetProperty("payment").GetProperty("entity");
            paymentId = entity.TryGetProperty("id", out var p) ? p.GetString() : null;
            orderId = entity.TryGetProperty("order_id", out var o) ? o.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Signature was valid, so this really is Razorpay — an event shape we don't handle.
            // 200 on purpose: a retry would deliver the same body and fail the same way.
            logger.LogWarning(ex, "Razorpay webhook body wasn't in the expected payment shape; ignoring");
            return Ok();
        }

        // Only the one event matters. Everything else (payment.failed, order.paid, the dozen
        // others that may be switched on in the dashboard) is acknowledged and dropped —
        // answering non-2xx would make Razorpay retry an event we are never going to act on.
        if (!string.Equals(eventName, "payment.captured", StringComparison.OrdinalIgnoreCase))
            return Ok();

        if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(paymentId))
        {
            logger.LogWarning("Razorpay payment.captured arrived without an order id or payment id; ignoring");
            return Ok();
        }

        // 500 rather than 200 if Razorpay's API is unreachable: that IS worth a retry, and
        // Razorpay redelivers on a non-2xx.
        RazorpayOrder order;
        try
        {
            order = await razorpay.GetOrderAsync(orderId);
        }
        catch (RazorpayApiException ex)
        {
            logger.LogError(ex, "Couldn't fetch Razorpay order {OrderId} while handling payment.captured", orderId);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        // The tenant comes from the order's own notes, never from the request — and with no
        // authenticated principal there is no ambient tenant to fall back on. See
        // ApplyPaidOrderAsync for why every query it runs has to name this id explicitly.
        var notes = order.Notes ?? [];
        if (!int.TryParse(notes.GetValueOrDefault(TenantNote), out var tenantId)
            || !TryReadPlanNotes(order, out var plan, out var cycle))
        {
            // A payment on this Razorpay account that wasn't started by our checkout — nothing
            // to apply it to. Acknowledged so it stops being redelivered.
            logger.LogWarning("Razorpay order {OrderId} has no usable tenant/plan/cycle notes; ignoring payment {PaymentId}",
                orderId, paymentId);
            return Ok();
        }

        if (!string.Equals(order.Status, "paid", StringComparison.OrdinalIgnoreCase) || order.AmountPaid < order.Amount)
        {
            // payment.captured fired but the order isn't fully paid — a partial payment, or
            // Razorpay's order status lagging its payment status by a moment. Worth a retry.
            logger.LogWarning("Razorpay order {OrderId} is {Status} ({Paid}/{Total} paise) on payment.captured; asking for a redelivery",
                orderId, order.Status, order.AmountPaid, order.Amount);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        await ApplyPaidOrderAsync(order, paymentId, tenantId, plan, cycle);
        return Ok();
    }

    private static bool TryReadPlanNotes(RazorpayOrder order, out SubscriptionTier plan, out BillingCycle cycle)
    {
        var notes = order.Notes ?? [];
        return Enum.TryParse(notes.GetValueOrDefault(PlanNote), out plan)
            & Enum.TryParse(notes.GetValueOrDefault(CycleNote), out cycle);
    }

    /// <summary>
    /// Moves the tenant onto the plan they paid for. Shared by Verify and Webhook, which race
    /// each other on every successful payment — whichever arrives first does the work and the
    /// other one no-ops on the audit-log check below.
    ///
    /// Every query here IgnoreQueryFilters()s and matches TenantId by hand. The webhook has no
    /// authenticated principal, so the global tenant filter would fall back to
    /// Tenant.DefaultTenantId and upgrade the WRONG CAFE — the same trap
    /// WhatsAppInternalController documents. Verify passes its own tenant id in, so the
    /// explicit filter is exactly equivalent to the ambient one there.
    /// </summary>
    private async Task<Subscription> ApplyPaidOrderAsync(
        RazorpayOrder order, string paymentId, int tenantId, SubscriptionTier plan, BillingCycle cycle)
    {
        var sub = await db.Subscriptions.IgnoreQueryFilters().FirstAsync(s => s.TenantId == tenantId);

        // Idempotency without a payments table: every successful activation writes an audit
        // entry keyed on the Razorpay payment id, so a repeat (double-click, a dropped response
        // the app retried, a webhook redelivery, or the webhook and the browser both landing)
        // leaves the plan alone instead of stacking another month onto it.
        var alreadyApplied = await db.AuditLog.IgnoreQueryFilters().AnyAsync(a =>
            a.TenantId == tenantId && a.Resource == AuditResource.Subscription && a.ResourceId == paymentId);
        if (alreadyApplied) return sub;

        var oldPlan = sub.Plan;
        // Renewing a plan that hasn't lapsed yet extends from its existing expiry rather than
        // from today — paying early shouldn't cost the owner the days they already bought.
        // (ChangePlan resets from "now" instead; it's a manual correction, not a purchase.)
        var startFrom = sub.Plan == plan && sub.PlanExpiresAt > DateTime.UtcNow ? sub.PlanExpiresAt.Value : DateTime.UtcNow;
        sub.Plan = plan;
        // The cycle they actually paid for, kept on the row so the Subscription screen can say
        // "Billed yearly" instead of the app having to guess it back out of the dates.
        sub.Cycle = cycle;
        // Not "now" on an early renewal: the term they just bought starts where the old one
        // ran out, which is the same instant ExpiryFrom counts from.
        sub.PlanStartedAt = startFrom;
        sub.PlanExpiresAt = SubscriptionPricing.ExpiryFrom(startFrom, cycle);
        sub.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Same reason as ChangePlan: the plan gates read through a short-TTL cache, so drop
        // the entry or the screens the owner just paid for stay locked for another minute.
        subscriptions.Invalidate(tenantId);

        await audit.LogAsync(AuditAction.SubscriptionChange, AuditResource.Subscription, paymentId,
            $"Razorpay payment {paymentId} (order {order.Id}, ₹{order.AmountPaid / 100m:0.00}) " +
            $"moved plan from {oldPlan} to {plan} for {SubscriptionPricing.CycleLabel(cycle)}.",
            AuditSeverity.High, tenantId: tenantId);

        return sub;
    }

    private async Task<VerifyPaymentResponse> BuildResponseAsync(Subscription sub)
    {
        var startOfMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return new VerifyPaymentResponse(true, SubscriptionDto.From(
            sub,
            await db.Branches.CountAsync(),
            await db.Staff.CountAsync(),
            await db.Orders.CountAsync(o => o.CreatedAt >= startOfMonth)));
    }

    /// <summary>
    /// Gateway trouble is a 500, not a 401 — even when Razorpay's own answer was 401. The app
    /// treats any 401 as "my session died" and will run a token refresh and log the owner out
    /// (see api.ts's interceptor), which is a spectacularly confusing thing to do to someone
    /// whose only mistake was pressing Upgrade while our API keys were misconfigured.
    /// </summary>
    private ObjectResult GatewayUnavailable(string message) =>
        StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = message,
            Instance = HttpContext.Request.Path,
        });
}
