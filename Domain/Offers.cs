namespace CafePOS.Api.Domain;

/// <summary>What an offer actually takes off the bill. Deliberately only three: every discount
/// shape a cafe asks for is one of these crossed with a <see cref="OfferScope"/> and the
/// condition/validity fields on <see cref="Offer"/> — a "20% off beverages, weekends only,
/// above ₹500" is Percentage + Category + DaysOfWeek + MinOrderValue, not its own type. Adding
/// a fourth type should be a last resort; prefer a new scope or condition.</summary>
public enum OfferType
{
    /// <summary>A percentage off the qualifying lines, optionally capped by MaxDiscountAmount.</summary>
    Percentage,

    /// <summary>A fixed rupee amount off. Never exceeds the qualifying lines' own value, so a
    /// ₹100 offer on a ₹60 plate of fries takes ₹60, not ₹100.</summary>
    Flat,

    /// <summary>Buy <see cref="Offer.BuyQty"/>, get <see cref="Offer.GetQty"/> free — the
    /// cheapest units in each complete set are the free ones (the universal retail rule; giving
    /// away the dearest would let a customer pair one expensive item with cheap ones and walk
    /// off with the expensive one).</summary>
    BuyXGetY,
}

/// <summary>Which of the bill's lines an offer is allowed to touch. This is what makes
/// "category discount" and "item-level discount" fall out of the same engine rather than
/// needing separate features.</summary>
public enum OfferScope
{
    /// <summary>Every line on the bill.</summary>
    EntireBill,

    /// <summary>Only lines whose menu item sits in <see cref="Offer.CategoryName"/>.</summary>
    Category,

    /// <summary>Only lines whose menu item is listed in <see cref="Offer.Items"/>.</summary>
    SpecificItems,
}

/// <summary>A cafe-defined promotion evaluated against the cart on every recompute, as opposed
/// to a <see cref="Coupon"/>, which is a one-per-customer code someone types in. The split
/// matters: a coupon is a value ("₹50 off, code SAVE50, single use"), an offer is a rule
/// ("every 3rd coffee free, 4–7pm") — trying to express the rule as a coupon value is why
/// CouponType.Bogo never priced anything.
///
/// All money the engine derives from this lands in Order.OfferDiscountAmount, which joins the
/// existing discount pool in OrderBuildingService.RecomputeTotals. Nothing here computes tax:
/// the pool already drives the per-line taxable split.</summary>
public class Offer : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    /// <summary>Shown on the POS banner and printed on the bill, so write it the way a customer
    /// should read it ("Buy 2 Get 1 — Coffee"), not as an internal code.</summary>
    public required string Title { get; set; }

    public OfferType Type { get; set; }
    public OfferScope Scope { get; set; }

    // ---------- Scope targets ----------

    /// <summary>Set only when <see cref="Scope"/> is Category. Matched against MenuItem.Category,
    /// which the whole codebase keys by name rather than an int id (there is no CategoryId FK —
    /// see MenuCategory, itself name-identified). Goes stale if the category is later renamed,
    /// exactly like every other place an item references its category.</summary>
    public string? CategoryName { get; set; }

    /// <summary>Set only when <see cref="Scope"/> is SpecificItems.</summary>
    public List<OfferMenuItem> Items { get; set; } = [];

    // ---------- Percentage / Flat ----------

    /// <summary>Percent (1–100) for Percentage, rupees for Flat. Unused by BuyXGetY.</summary>
    public decimal Value { get; set; }

    /// <summary>Ceiling on what a Percentage offer may take off, the "20% off up to ₹150" shape.
    /// 0 = uncapped. Ignored for the other types (Flat is its own ceiling).</summary>
    public decimal MaxDiscountAmount { get; set; }

    // ---------- BuyXGetY ----------

    /// <summary>How many units must be paid for before the free ones unlock.</summary>
    public int BuyQty { get; set; }

    /// <summary>How many units come free once BuyQty is met. "Buy 1 Get 1" is 1/1, "Buy 2 Get 1"
    /// is 2/1 — so a complete set is BuyQty + GetQty units.</summary>
    public int GetQty { get; set; }

    // ---------- Conditions ----------

    /// <summary>Minimum bill subtotal before the offer unlocks — checked against the WHOLE
    /// subtotal, not just the qualifying lines, because "15% off beverages above ₹600" means the
    /// bill crosses ₹600, not the beverages. 0 = no minimum.</summary>
    public decimal MinOrderValue { get; set; }

    /// <summary>Caps how many times a BuyXGetY set may repeat on one bill. 0 = unlimited, which
    /// is the default because that is what a customer reads "Buy 1 Get 1" to mean when they put
    /// four pizzas on the counter — a cap that silently stops at one free pizza is an argument at
    /// the till. Owners who want the cap can set it. Meaningless for Percentage/Flat, which
    /// apply once by nature.</summary>
    public int MaxApplicationsPerBill { get; set; }

    /// <summary>Whether this offer may combine with other offers on one bill. Default false —
    /// the safe direction, since two stacking discounts on the same line is the failure mode an
    /// owner notices only at month end. When several non-stackable offers all qualify, the
    /// engine keeps the single best one for the customer.</summary>
    public bool Stackable { get; set; }

    // ---------- Validity ----------

    /// <summary>Stored UTC like every other timestamp. Null = no bound on that side.</summary>
    public DateTime? StartsAtUtc { get; set; }
    public DateTime? EndsAtUtc { get; set; }

    /// <summary>CSV of System.DayOfWeek numbers the offer runs on, e.g. "5,6,0" for Fri/Sat/Sun.
    /// Null or empty = every day. Matched against the cafe's IST weekday, not UTC's — before
    /// 05:30 IST the two disagree, and a "weekend offer" that dies at 5:29am Saturday is a
    /// support call.</summary>
    public string? DaysOfWeek { get; set; }

    /// <summary>Happy-hour window on the cafe's wall clock (IST). Both null = all day. A window
    /// whose end is before its start is read as crossing midnight (22:00–02:00).</summary>
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }

    // ---------- State ----------

    /// <summary>False = the cashier must pick it deliberately; true = it lands on any qualifying
    /// bill by itself. True by default: an offer nobody remembers to apply is the whole problem
    /// this feature exists to fix.</summary>
    public bool AutoApply { get; set; } = true;

    /// <summary>Soft switch so an offer can be paused and re-run next season without losing the
    /// setup — same reasoning as Reward.IsActive.</summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>Join row for <see cref="OfferScope.SpecificItems"/>. A plain many-to-many rather
/// than a CSV of ids so the item picker can be a real query and a deleted menu item can be
/// cascaded away.</summary>
public class OfferMenuItem : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int OfferId { get; set; }
    public int MenuItemId { get; set; }
}
