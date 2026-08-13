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

        var cycleLabel = req.Cycle == BillingCycle.Yearly ? "1 year" : "1 month";
        return new CreateOrderResponse(
            order.Id, order.Amount, order.Currency, razorpay.KeyId,
            $"PrabandhOS {SubscriptionPricing.DisplayName(req.Plan)} — {cycleLabel}");
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

        if (!Enum.TryParse<SubscriptionTier>(notes.GetValueOrDefault(PlanNote), out var plan)
            || !Enum.TryParse<BillingCycle>(notes.GetValueOrDefault(CycleNote), out var cycle))
        {
            logger.LogError("Razorpay order {OrderId} came back without usable plan/cycle notes", order.Id);
            throw new ApiValidationException("We couldn't tell which plan this payment was for — contact support.");
        }

        // "paid" is Razorpay's own word for "the full order amount has been captured". An
        // order sitting at "attempted" means the money was authorised but not captured (an
        // account with auto-capture switched off, or a payment still being processed) — the
        // signature is genuine either way, so this is a wait, not a fraud case.
        if (!string.Equals(order.Status, "paid", StringComparison.OrdinalIgnoreCase) || order.AmountPaid < order.Amount)
            throw new ApiValidationException("This payment hasn't been captured yet. Give it a minute, then reopen this screen — your plan updates on its own once it clears.");

        var sub = await db.Subscriptions.FirstAsync();

        // Idempotency without a payments table: every successful activation writes an audit
        // entry keyed on the Razorpay payment id, so a retried request (double-click, a
        // dropped response the app retried, a replayed callback) returns the same answer
        // instead of stacking another month onto the plan. The audit log is tenant-filtered
        // by CafePosDbContext, so this can't collide across cafes.
        var alreadyApplied = await db.AuditLog.AnyAsync(a =>
            a.Resource == AuditResource.Subscription && a.ResourceId == req.RazorpayPaymentId);
        if (alreadyApplied)
            return await BuildResponseAsync(sub);

        var oldPlan = sub.Plan;
        // Renewing a plan that hasn't lapsed yet extends from its existing expiry rather than
        // from today — paying early shouldn't cost the owner the days they already bought.
        // (ChangePlan resets from "now" instead; it's a manual correction, not a purchase.)
        var startFrom = sub.Plan == plan && sub.PlanExpiresAt > DateTime.UtcNow ? sub.PlanExpiresAt.Value : DateTime.UtcNow;
        sub.Plan = plan;
        sub.PlanExpiresAt = SubscriptionPricing.ExpiryFrom(startFrom, cycle);
        sub.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Same reason as ChangePlan: the plan gates read through a short-TTL cache, so drop
        // the entry or the screens the owner just paid for stay locked for another minute.
        subscriptions.Invalidate(tenantId);

        await audit.LogAsync(AuditAction.SubscriptionChange, AuditResource.Subscription, req.RazorpayPaymentId,
            $"Razorpay payment {req.RazorpayPaymentId} (order {order.Id}, ₹{order.AmountPaid / 100m:0.00}) " +
            $"moved plan from {oldPlan} to {plan} for 1 {(cycle == BillingCycle.Yearly ? "year" : "month")}.",
            AuditSeverity.High);

        return await BuildResponseAsync(sub);
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
