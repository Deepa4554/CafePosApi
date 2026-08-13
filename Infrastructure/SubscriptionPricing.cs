using CafePOS.Api.Contracts;
using CafePOS.Api.Domain;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// The authoritative price list. Its twin is GRID_PLANS in the app's SubscriptionScreen.tsx —
/// that one is the storefront (what the cafe owner reads on the card), this one is what
/// actually gets charged. Change one, change the other, exactly like UpiPaymentLink and
/// upi.ts. If they ever disagree, this side wins and the owner sees a different figure in
/// the Razorpay modal than on the card, so treat a mismatch as a bug.
///
/// Yearly is ~2 months free against the monthly rate, matching the "Save 17%" badge on the
/// cycle toggle (499*12 = 5988 -> 4999, 799*12 = 9588 -> 7999).
/// </summary>
public static class SubscriptionPricing
{
    /// <summary>Rupee prices, in paise — Razorpay's only unit. Absent combinations are
    /// deliberately not purchasable: FreeTrial isn't sold, and Enterprise is quoted by
    /// hand ("Custom Pricing" on the card).</summary>
    private static readonly Dictionary<(SubscriptionTier Plan, BillingCycle Cycle), long> Paise = new()
    {
        [(SubscriptionTier.Starter, BillingCycle.Monthly)] = 49_900,
        [(SubscriptionTier.Starter, BillingCycle.Yearly)] = 499_900,
        [(SubscriptionTier.Professional, BillingCycle.Monthly)] = 79_900,
        [(SubscriptionTier.Professional, BillingCycle.Yearly)] = 799_900,
    };

    /// <summary>Storefront names — the tiers are branded Basic/Plus to the customer, so the
    /// Razorpay modal and the receipt e-mail have to say that, not "Professional".</summary>
    private static readonly Dictionary<SubscriptionTier, string> DisplayNames = new()
    {
        [SubscriptionTier.Starter] = "Basic",
        [SubscriptionTier.Professional] = "Plus",
    };

    /// <summary>Null for anything that isn't sold self-service — callers turn that into a
    /// 400 rather than inventing a price.</summary>
    public static long? PaiseFor(SubscriptionTier plan, BillingCycle cycle) =>
        Paise.TryGetValue((plan, cycle), out var paise) ? paise : null;

    public static string DisplayName(SubscriptionTier plan) =>
        DisplayNames.GetValueOrDefault(plan, plan.ToString());

    /// <summary>How far the paid cycle pushes PlanExpiresAt out. Kept here next to the price
    /// so a cycle can never be charged for a year and granted for a month.</summary>
    public static DateTime ExpiryFrom(DateTime start, BillingCycle cycle) =>
        cycle == BillingCycle.Yearly ? start.AddYears(1) : start.AddMonths(1);
}
