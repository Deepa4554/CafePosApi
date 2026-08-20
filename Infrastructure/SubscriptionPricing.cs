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

    /// <summary>The free trial's fixed length. It isn't sold on a cycle, so it doesn't get
    /// one — StartTerm forces Monthly on it purely so the stored value is never garbage.</summary>
    public const int TrialDays = 14;

    /// <summary>
    /// Puts a tenant on ONE fresh term of a plan, starting now — the "I confirmed they paid
    /// out-of-band" grant behind both change-plan endpoints (the tenant's own and the
    /// platform admin's). Shared so the two can't drift on the cycle length, which is exactly
    /// how yearly used to be quietly granted as a month: both endpoints hardcoded AddMonths(1).
    ///
    /// Deliberately NOT what a Razorpay renewal does — PaymentsController extends from the
    /// existing expiry so paying early doesn't burn the days already bought. This one resets
    /// from today, because a manual grant is a correction, not a purchase.
    /// </summary>
    public static void StartTerm(Subscription sub, SubscriptionTier plan, BillingCycle cycle, DateTime now)
    {
        var isTrial = plan == SubscriptionTier.FreeTrial;
        sub.Plan = plan;
        sub.Cycle = isTrial ? BillingCycle.Monthly : cycle;
        sub.PlanStartedAt = now;
        sub.PlanExpiresAt = isTrial ? now.AddDays(TrialDays) : ExpiryFrom(now, cycle);
        sub.UpdatedAt = now;
    }

    /// <summary>"1 month" / "1 year" — for audit lines and the Razorpay order description.</summary>
    public static string CycleLabel(BillingCycle cycle) => cycle == BillingCycle.Yearly ? "1 year" : "1 month";
}
