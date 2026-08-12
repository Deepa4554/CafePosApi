namespace CafePOS.Api.Domain;

/// <summary>
/// How a tiffin subscriber is served day to day. The two differ only in their default for a
/// day nobody has touched — which is the whole design of the roster (see TiffinMark):
/// <list type="bullet">
/// <item><see cref="Daily"/> — the plate goes out every day by default; a customer only speaks
/// up on the days they DON'T want it (opt-out).</item>
/// <item><see cref="Occasional"/> — nothing goes out by default; a plate is sent only on the
/// days someone explicitly asks for it (opt-in).</item>
/// </list>
/// </summary>
public enum TiffinType { Daily, Occasional }

/// <summary>What's in the plate — drives the kitchen's veg/non-veg headcount on the roster.
/// <see cref="Custom"/> is the escape hatch for a plan that's neither (e.g. a Jain or a
/// combo), counted separately rather than forced into one of the two.</summary>
public enum TiffinMealType { Veg, NonVeg, Custom }

/// <summary>What a <see cref="TiffinMark"/> row records for a subscriber on a given date.
/// A mark is an OVERRIDE of the type's default, never the default itself — so most days have
/// no row at all (see TiffinMark's own summary).</summary>
public enum TiffinMarkStatus
{
    /// <summary>A plate went out this day. Only ever stored for an Occasional subscriber (whose
    /// default is "nothing"), or for a Daily subscriber whose quantity differed from their
    /// usual — a plain "yes, as usual" Daily day is left unmarked.</summary>
    Delivered,

    /// <summary>No plate this day. Only ever stored for a Daily subscriber (whose default is
    /// "deliver") — the customer's "aaj band". Meaningless for an Occasional subscriber, whose
    /// default is already "nothing", so it's simply never written for one.</summary>
    Skipped,
}

/// <summary>Where a month's tiffin bill stands. Mirrors how a khata reads — see KhataEntry —
/// except a tiffin bill is a single computed invoice for a period rather than a running ledger,
/// so the state lives on the invoice itself instead of being summed from rows.</summary>
public enum TiffinInvoiceStatus { Unpaid, PartiallyPaid, Paid }

/// <summary>How a subscriber settles what they owe.
/// <list type="bullet">
/// <item><see cref="Postpaid"/> — the existing flow: plates go out on trust, a
/// <see cref="TiffinInvoice"/> is raised (usually monthly) for what was served, paid back after
/// the fact.</item>
/// <item><see cref="Prepaid"/> — the customer tops up a running balance ahead of time (see
/// <see cref="TiffinWalletTransaction"/>); each delivered day's plate cost is deducted from it as
/// the day is recorded, instead of accumulating into an invoice. Never gated on having enough —
/// a Prepaid subscriber whose balance runs out keeps getting plates, same as any other khata-style
/// running balance, just shown as negative until they top up again.</item>
/// </list></summary>
public enum TiffinPaymentMode { Postpaid, Prepaid }

/// <summary>Recharge (customer topping up ahead of time) or Deduction (a delivered day's cost
/// coming back out) — see <see cref="TiffinWalletTransaction"/>.</summary>
public enum TiffinWalletTxnType { Recharge, Deduction }

/// <summary>
/// One customer on the cafe's tiffin/thali plan. Hangs off the same <see cref="Customer"/>
/// record the rest of the app uses (orders, khata, loyalty) rather than a parallel address
/// book — a subscriber IS a customer, they've just also agreed a standing plate.
///
/// The plan itself (plate name, veg/non-veg, per-plate rate, usual quantity) is stored right
/// here rather than pointed at a menu item on purpose: a thali service prices a standing plate
/// as its own thing — a flat monthly rate divided into a per-day figure — and rarely wants it
/// tangled with the à-la-carte menu's pricing, tax slab, or stock. Rate is per plate; the
/// month's bill is (days served × quantity × rate), computed in TiffinController, never stored
/// on the subscriber.
///
/// Tenant-scoped like everything else; the balance a customer owes is never cached here, always
/// derived from delivered days and settled invoices, for the same reason KhataEntry never
/// caches a balance.
/// </summary>
public class TiffinSubscriber : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    /// <summary>Whose plan this is. Resolved from the name + mobile the create form makes
    /// compulsory, so the subscription sits on the same CRM record as the customer's orders
    /// and khata rather than in a directory of its own.</summary>
    public int CustomerId { get; set; }
    /// <summary>Set this (not CustomerId directly) when the customer was created in the same
    /// SaveChanges — see Order.Customer for why the raw int would otherwise still be 0.</summary>
    public Customer? Customer { get; set; }

    public TiffinType Type { get; set; } = TiffinType.Daily;

    /// <summary>Postpaid (default — every existing subscriber stays exactly as it was) or
    /// Prepaid. Changing this doesn't rewrite anything already served or invoiced; it only
    /// changes how days from here on are settled — see <see cref="TiffinPaymentMode"/>.</summary>
    public TiffinPaymentMode PaymentMode { get; set; } = TiffinPaymentMode.Postpaid;

    /// <summary>What the customer calls their plate — "Veg Thali", "Special", "Half Meal".
    /// Free text, shown on the roster and the bill; not a menu reference (see class summary).</summary>
    public string PlanName { get; set; } = "";

    public TiffinMealType MealType { get; set; } = TiffinMealType.Veg;

    /// <summary>Price of one plate. The month's bill multiplies this by days served and the
    /// per-day quantity — nothing about the total is stored, so correcting a rate only changes
    /// bills generated after the correction, never ones already raised.</summary>
    public decimal Rate { get; set; }

    /// <summary>How many plates a normal day sends (a family often takes two). A per-day
    /// override on a single date lives on that day's <see cref="TiffinMark"/> instead.</summary>
    public int DefaultQty { get; set; } = 1;

    /// <summary>Where the plate goes. Free text — a tiffin round is a delivery boy reading an
    /// address off a list, not a geocoded courier drop, so this deliberately isn't the CRM's
    /// structured address fields.</summary>
    public string? DeliveryAddress { get; set; }

    /// <summary>The first day this plan is live. A Daily subscriber isn't billed for, and
    /// doesn't appear on the roster of, any date before this — so onboarding someone
    /// mid-month doesn't retroactively invent a fortnight of plates they never got.</summary>
    public DateOnly StartDate { get; set; }

    /// <summary>A paused/ended subscription. Kept, never deleted, so its past deliveries and
    /// bills stay auditable; an inactive subscriber drops off the daily roster and stops
    /// accruing new billable days, but its history reads exactly as it did.</summary>
    public bool IsActive { get; set; } = true;

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One deliberate override of a subscriber's default for one date — the physical row behind the
/// roster's toggle. The design turns on this being an OVERRIDE table, not a per-day attendance
/// log: a Daily subscriber served their usual plate leaves no row at all, and so does an
/// Occasional subscriber who didn't order. That keeps the table to only the days someone
/// actually changed something, and makes the billing rule a subtraction/addition against the
/// type's default rather than a full day-by-day scan.
///
/// So a row means:
/// <list type="bullet">
/// <item>Daily + <see cref="TiffinMarkStatus.Skipped"/> — "aaj band", the common case.</item>
/// <item>Daily + <see cref="TiffinMarkStatus.Delivered"/> — served, but at a non-default
/// quantity carried in <see cref="Qty"/> (a plain default-quantity day stays unmarked).</item>
/// <item>Occasional + <see cref="TiffinMarkStatus.Delivered"/> — a plate was sent on a day that
/// otherwise wouldn't have one.</item>
/// </list>
/// At most one row per (subscriber, date) — the roster upserts it, so toggling a day twice
/// overwrites rather than piles up.
/// </summary>
public class TiffinMark : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    public int SubscriberId { get; set; }
    public TiffinSubscriber? Subscriber { get; set; }

    /// <summary>The cafe-local (IST) calendar date this override applies to — a date the owner
    /// picked, not a UTC instant. Stored as a DateOnly so a day never drifts across the 5:30 AM
    /// IST/UTC boundary the way a DateTime would.</summary>
    public DateOnly Date { get; set; }

    public TiffinMarkStatus Status { get; set; }

    /// <summary>Plates on this specific day. Only meaningful on a Delivered row; a Skipped row
    /// carries the subscriber's default here purely so the column is never a confusing zero.</summary>
    public int Qty { get; set; } = 1;

    public int? RecordedByUserId { get; set; }
    public string RecordedByName { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A month's (or any period's) tiffin bill for one subscriber, frozen at the moment it's
/// generated. Everything that feeds the total — days served, quantity, the rate in force — is
/// snapshotted onto the invoice so a later rate change or a corrected skip never silently
/// rewrites a bill the customer has already been handed. Repayments come back as
/// <see cref="TiffinPayment"/> rows, in as many instalments as it takes, exactly like a khata.
/// </summary>
public class TiffinInvoice : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    public int SubscriberId { get; set; }
    public TiffinSubscriber? Subscriber { get; set; }

    /// <summary>Denormalised from the subscriber at generation time so the bill survives the
    /// subscriber row being edited, and so the list can group by customer without a join.</summary>
    public int CustomerId { get; set; }

    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }

    public int DeliveredDays { get; set; }
    public int TotalQty { get; set; }
    /// <summary>The per-plate rate as it stood when this bill was raised.</summary>
    public decimal Rate { get; set; }
    public decimal TotalAmount { get; set; }

    /// <summary>Sum of this invoice's payments — the only mutable money field, and only ever
    /// moved by recording a <see cref="TiffinPayment"/> under a row lock (see
    /// TiffinController.Settle), never edited directly.</summary>
    public decimal AmountPaid { get; set; }

    public TiffinInvoiceStatus Status { get; set; } = TiffinInvoiceStatus.Unpaid;

    public string? Notes { get; set; }

    public int? GeneratedByUserId { get; set; }
    public string GeneratedByName { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<TiffinPayment> Payments { get; set; } = [];
}

/// <summary>Money collected against a <see cref="TiffinInvoice"/> — the whole amount or any part
/// of it, as many times as the customer pays. Method is the real tender it arrived as (Cash /
/// Card / UPI); there's no "Due" tender here the way the POS has, because a tiffin bill is
/// already the credit — this is only ever money coming back in.</summary>
public class TiffinPayment : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    public int InvoiceId { get; set; }
    public TiffinInvoice? Invoice { get; set; }

    /// <summary>Always positive.</summary>
    public decimal Amount { get; set; }

    /// <summary>Cash / Card / UPI — validated against TiffinController's set on write.</summary>
    public string Method { get; set; } = "Cash";

    public string? Note { get; set; }

    public int? RecordedByUserId { get; set; }
    public string RecordedByName { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One entry in a Prepaid subscriber's running balance — a Recharge (customer topped up)
/// or a Deduction (one delivered day's plate cost coming back out). The balance itself is never
/// cached anywhere, always the sum of these rows, same reasoning as KhataEntry and TiffinInvoice
/// never caching a balance either.
///
/// A Deduction row is frozen at the rate in force when that day was recorded — a later rate
/// change never rewrites a day that's already been charged, same guarantee TiffinInvoice gives a
/// Postpaid bill. At most one Deduction row per (subscriber, date): TiffinController upserts it
/// (create / correct / remove) every time that day's delivering/qty is (re)computed, whether from
/// an explicit roster toggle or from simply viewing the roster for a Daily subscriber's default
/// (unmarked) day — see TiffinController.SyncWalletDeductionAsync. A Recharge row is never
/// touched again once written.</summary>
public class TiffinWalletTransaction : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    public int SubscriberId { get; set; }
    public TiffinSubscriber? Subscriber { get; set; }

    public TiffinWalletTxnType Type { get; set; }

    /// <summary>Positive for a Recharge, negative for a Deduction. Never zero — a day that
    /// settles to nothing owed (not delivering) has its Deduction row removed instead.</summary>
    public decimal Amount { get; set; }

    /// <summary>Deduction only — the roster date this row charges for. Null on a Recharge, which
    /// isn't about any one day.</summary>
    public DateOnly? ForDate { get; set; }

    /// <summary>Recharge only — Cash / Card / UPI, the real tender the top-up arrived as.</summary>
    public string? Method { get; set; }

    public string? Note { get; set; }

    /// <summary>Null and "System (roster)" on a Deduction written by the roster sync rather than
    /// a person acting directly.</summary>
    public int? RecordedByUserId { get; set; }
    public string RecordedByName { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
