using CafePOS.Api.Domain;

namespace CafePOS.Api.Contracts;

/// <summary>
/// What the browser asks to buy — a plan and a cycle, never an amount. The price is looked
/// up server-side (SubscriptionPricing) on purpose: an amount taken from the client would
/// let anyone open devtools and buy the ₹799 plan for ₹1.
/// </summary>
public record CreateSubscriptionOrderRequest(SubscriptionTier Plan, BillingCycle Cycle);

/// <summary>
/// Everything checkout.js needs to open its modal. KeyId rides along rather than being
/// baked into the web bundle — see RazorpayOptions.KeyId.
/// </summary>
public record CreateOrderResponse(string OrderId, long Amount, string Currency, string KeyId, string Description);

public record VerifyPaymentRequest(string RazorpayOrderId, string RazorpayPaymentId, string RazorpaySignature);

/// <summary>Carries the refreshed subscription back so the app can show the new plan
/// straight from the verification response instead of racing a refetch against it.</summary>
public record VerifyPaymentResponse(bool Verified, SubscriptionDto Subscription);
