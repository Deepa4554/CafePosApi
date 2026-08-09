using CafePOS.Api.Domain;

namespace CafePOS.Api.Contracts;

/// <summary>One customer's line in the Khatabook list — who owes, how much, and how long
/// it's been. TotalDue/TotalPaid are lifetime figures across the whole ledger, not just the
/// unsettled part, so a shop owner can see at a glance whether an outstanding ₹500 belongs
/// to someone who's paid back ₹9,000 over the year or to someone who's never paid at all.</summary>
public record KhataCustomerDto(
    int CustomerId,
    string Name,
    string? Phone,
    string? ProfilePhotoUrl,
    decimal Outstanding,
    decimal TotalDue,
    decimal TotalPaid,
    int EntryCount,
    // The most recent movement of any kind on this khata — credit taken or money paid back.
    DateTime LastActivityAt,
    // When credit was last extended. Null only on a khata whose rows are all repayments,
    // which can't happen in practice (a Payment row can't exist before a Due row).
    DateTime? LastDueAt);

/// <summary>One row of a customer's ledger. OrderNumber is filled for Due rows (the bill the
/// credit came from, in the same "#1000+id" form the rest of the app shows) and null for
/// repayments, which settle the balance as a whole rather than one specific old bill.</summary>
public record KhataEntryDto(
    int Id,
    // "Due" (credit taken) or "Payment" (money back) — see KhataEntryType.
    string Type,
    // Always positive; Type carries the direction.
    decimal Amount,
    int? OrderId,
    string? OrderNumber,
    // The real tender a repayment arrived as (Cash / Card / UPI). Null on Due rows, which by
    // definition moved no money.
    string? Method,
    string? Note,
    string RecordedByName,
    DateTime CreatedAt,
    // What the customer still owed immediately AFTER this row was recorded. Computed
    // server-side because it depends on the ledger as a whole, oldest row forward — which is
    // the opposite order to how the list is displayed, and not something a client holding
    // only part of the ledger could work out for itself.
    decimal RunningOutstanding)
{
    public static KhataEntryDto From(KhataEntry e, decimal runningOutstanding) => new(
        e.Id,
        e.Type.ToString(),
        e.Amount,
        e.OrderId,
        e.OrderId is int id ? $"#{1000 + id}" : null,
        e.Method,
        e.Note,
        e.RecordedByName,
        e.CreatedAt,
        runningOutstanding);
}

/// <summary>A customer's full khata — the same summary line the list shows, plus the ledger
/// behind it, newest first. Deliberately unpaged: the running balance needs every row anyway,
/// and a khata is a handful of rows per customer, not an order history.</summary>
public record KhataDetailDto(KhataCustomerDto Customer, List<KhataEntryDto> Entries);

/// <summary>Money coming back in against a khata. Partial is the normal case — that's what a
/// khata is for — so any amount up to the outstanding balance is accepted. Method is the real
/// tender it arrived as (Cash / Card / UPI); "Due" is rejected, since paying off credit with
/// more credit is just no payment at all.</summary>
public record SettleKhataRequest(decimal Amount, string Method, string? Note = null);

/// <summary>Tenant-wide totals shown above the Khatabook list.</summary>
public record KhataSummaryDto(decimal TotalOutstanding, int CustomersWithDue, decimal CollectedThisMonth);

/// <summary>The Khatabook list — the customers themselves plus the roll-up above them, in one
/// round trip. Two calls would be two 200-500ms round trips to Tokyo for one screen (see the
/// note on OrdersController.List), and the summary is derived from the same ledger scan.</summary>
public record KhatabookDto(KhataSummaryDto Summary, List<KhataCustomerDto> Customers);
