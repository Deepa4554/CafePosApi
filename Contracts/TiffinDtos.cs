using CafePOS.Api.Domain;

namespace CafePOS.Api.Contracts;

// ─────────────────────────────  Subscribers  ─────────────────────────────

/// <summary>One customer on the tiffin plan, as the Subscribers tab lists them. Name/Phone are
/// carried from the linked Customer so the list needs no second lookup.</summary>
public record TiffinSubscriberDto(
    int Id,
    int CustomerId,
    string Name,
    string? Phone,
    string? ProfilePhotoUrl,
    string Type,
    string PlanName,
    string MealType,
    decimal Rate,
    int DefaultQty,
    string? DeliveryAddress,
    DateOnly StartDate,
    bool IsActive,
    string? Notes,
    string PaymentMode,
    /// <summary>Null for a Postpaid subscriber. For Prepaid, the running balance — sum of every
    /// TiffinWalletTransaction, so it's negative once deliveries have outrun top-ups.</summary>
    decimal? WalletBalance)
{
    public static TiffinSubscriberDto From(TiffinSubscriber s, Customer c, decimal? walletBalance = null) => new(
        s.Id, s.CustomerId, c.Name, c.Phone, c.ProfilePhotoUrl,
        s.Type.ToString(), s.PlanName, s.MealType.ToString(), s.Rate, s.DefaultQty,
        s.DeliveryAddress, s.StartDate, s.IsActive, s.Notes,
        s.PaymentMode.ToString(), s.PaymentMode == TiffinPaymentMode.Prepaid ? (walletBalance ?? 0m) : null);
}

/// <summary>Create a subscriber. Name + Phone identify (and, if new, create) the underlying
/// Customer — a phone already on file is reused rather than duplicated, so a walk-in who later
/// takes a tiffin keeps one CRM record. StartDate defaults to today (IST) when omitted.</summary>
public record CreateTiffinSubscriberRequest(
    string Name,
    string? Phone,
    string Type,
    string PlanName,
    string MealType,
    decimal Rate,
    int DefaultQty,
    string? DeliveryAddress = null,
    DateOnly? StartDate = null,
    string? Notes = null,
    /// <summary>"Postpaid" (default) or "Prepaid" — see TiffinPaymentMode.</summary>
    string PaymentMode = "Postpaid");

/// <summary>Edit a subscriber's plan. Only the plan side is editable here — the customer's name
/// and phone belong to the CRM record and are changed there. Flipping Type (Occasional↔Daily)
/// or IsActive is the main reason this exists: it changes what the roster defaults to from that
/// day on, never what already happened.</summary>
public record UpdateTiffinSubscriberRequest(
    string Type,
    string PlanName,
    string MealType,
    decimal Rate,
    int DefaultQty,
    string? DeliveryAddress,
    bool IsActive,
    string? Notes,
    string PaymentMode = "Postpaid");

// ─────────────────────────────  Roster  ─────────────────────────────

/// <summary>One subscriber's line on a given day's roster. <c>Delivering</c> is the effective
/// answer after the day's mark (if any) is applied to the type's default, and <c>Qty</c> the
/// effective plate count — the client just renders and toggles these, the rule lives on the
/// server (see TiffinController.Roster).</summary>
public record TiffinRosterEntryDto(
    int SubscriberId,
    int CustomerId,
    string Name,
    string? Phone,
    string Type,
    string PlanName,
    string MealType,
    string? DeliveryAddress,
    int DefaultQty,
    bool Delivering,
    int Qty,
    /// <summary>The stored override for this date, if any — "Delivered" / "Skipped" / null. Lets
    /// the client show "changed from usual" vs an untouched default day.</summary>
    string? MarkStatus,
    string PaymentMode,
    /// <summary>Null for Postpaid. For Prepaid, the balance after this date's deduction has been
    /// synced — negative means the roster should flag it (see TiffinController.Roster).</summary>
    decimal? WalletBalance);

/// <summary>The kitchen's headline numbers for the day — what to actually cook.</summary>
public record TiffinRosterSummaryDto(
    int TotalDelivering,
    int TotalPlates,
    int VegPlates,
    int NonVegPlates,
    int CustomPlates,
    int SkippedCount);

public record TiffinRosterDto(DateOnly Date, TiffinRosterSummaryDto Summary, List<TiffinRosterEntryDto> Entries);

/// <summary>Toggle one subscriber's day. <c>Deliver</c> is the target state; the server writes,
/// updates, or deletes the day's mark to reach it given the subscriber's type (a Daily "yes" is
/// stored as the absence of a skip, an Occasional "yes" as a Delivered row, and so on). <c>Qty</c>
/// overrides the plate count for the day; null keeps the subscriber's default.</summary>
public record MarkTiffinRequest(int SubscriberId, DateOnly Date, bool Deliver, int? Qty = null);

// ─────────────────────────────  Billing  ─────────────────────────────

/// <summary>One subscriber's computed bill for the period, plus a pointer to an invoice already
/// raised for it (if any) so the screen can show "already billed" instead of offering to bill a
/// second time.</summary>
public record TiffinBillingLineDto(
    int SubscriberId,
    int CustomerId,
    string Name,
    string? Phone,
    string Type,
    string PlanName,
    decimal Rate,
    int DeliveredDays,
    int TotalQty,
    decimal Amount,
    int? InvoiceId,
    string? InvoiceStatus,
    decimal InvoiceAmountPaid);

public record TiffinBillingSummaryDto(
    int SubscriberCount,
    int TotalPlates,
    decimal TotalAmount,
    decimal AlreadyInvoiced,
    decimal Collected,
    decimal Outstanding);

/// <summary>The Billing tab for a chosen month — every subscriber's computed figure for the
/// period alongside the summary roll-up, in one round trip.</summary>
public record TiffinBillingDto(
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    TiffinBillingSummaryDto Summary,
    List<TiffinBillingLineDto> Lines);

/// <summary>Raise invoices for a month. Omit <c>SubscriberId</c> to bill everyone with a
/// non-zero, not-yet-invoiced figure for the period; pass it to bill exactly one. The period is
/// the IST calendar month, capped at today — you can't bill days that haven't happened.</summary>
public record GenerateTiffinInvoicesRequest(string Month, int? SubscriberId = null);

/// <summary>A raised tiffin bill. Outstanding is TotalAmount − AmountPaid, computed rather than
/// stored.</summary>
public record TiffinInvoiceDto(
    int Id,
    int SubscriberId,
    int CustomerId,
    string Name,
    string? Phone,
    string PlanName,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    int DeliveredDays,
    int TotalQty,
    decimal Rate,
    decimal TotalAmount,
    decimal AmountPaid,
    decimal Outstanding,
    string Status,
    string GeneratedByName,
    DateTime CreatedAt)
{
    public static TiffinInvoiceDto From(TiffinInvoice i, Customer c) => new(
        i.Id, i.SubscriberId, i.CustomerId, c.Name, c.Phone,
        i.Subscriber?.PlanName ?? "",
        i.PeriodStart, i.PeriodEnd, i.DeliveredDays, i.TotalQty, i.Rate,
        i.TotalAmount, i.AmountPaid, i.TotalAmount - i.AmountPaid,
        i.Status.ToString(), i.GeneratedByName, i.CreatedAt);
}

public record TiffinPaymentDto(
    int Id,
    decimal Amount,
    string Method,
    string? Note,
    string RecordedByName,
    DateTime CreatedAt)
{
    public static TiffinPaymentDto From(TiffinPayment p) =>
        new(p.Id, p.Amount, p.Method, p.Note, p.RecordedByName, p.CreatedAt);
}

/// <summary>An invoice with its repayments, newest first.</summary>
public record TiffinInvoiceDetailDto(TiffinInvoiceDto Invoice, List<TiffinPaymentDto> Payments);

/// <summary>Tenant-wide totals shown above the raised-invoices list.</summary>
public record TiffinInvoiceListSummaryDto(decimal TotalOutstanding, int InvoicesWithDue, decimal CollectedThisMonth);

public record TiffinInvoiceListDto(TiffinInvoiceListSummaryDto Summary, List<TiffinInvoiceDto> Invoices);

/// <summary>Money coming back in against a tiffin bill. Partial is fine — any amount up to the
/// outstanding balance. Method is the real tender (Cash / Card / UPI).</summary>
public record SettleTiffinInvoiceRequest(decimal Amount, string Method, string? Note = null);

// ─────────────────────────────  Prepaid wallet  ─────────────────────────────

/// <summary>One row of a Prepaid subscriber's wallet — a top-up or a day's deduction.</summary>
public record TiffinWalletTransactionDto(
    int Id,
    string Type,
    decimal Amount,
    DateOnly? ForDate,
    string? Method,
    string? Note,
    string RecordedByName,
    DateTime CreatedAt)
{
    public static TiffinWalletTransactionDto From(TiffinWalletTransaction t) =>
        new(t.Id, t.Type.ToString(), t.Amount, t.ForDate, t.Method, t.Note, t.RecordedByName, t.CreatedAt);
}

/// <summary>A subscriber's wallet: the current balance plus its full history, newest first — the
/// "why is the balance what it is" view behind the Subscribers tab's recharge sheet.</summary>
public record TiffinWalletDto(decimal Balance, List<TiffinWalletTransactionDto> Transactions);

/// <summary>Top up a Prepaid subscriber's balance. Method is the real tender it arrived as
/// (Cash / Card / UPI) — same set a tiffin bill or a khata payment is settled with.</summary>
public record RechargeTiffinWalletRequest(decimal Amount, string Method, string? Note = null);
