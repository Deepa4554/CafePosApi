namespace CafePOS.Api.Domain;

/// <summary>Which direction a <see cref="KhataEntry"/> moves a customer's outstanding
/// balance. Deliberately only two: credit taken, and money paid back. There's no manual
/// "add due out of nowhere" — every rupee a customer owes traces back to a real settled
/// bill, which is the whole point of the khata being auditable.</summary>
public enum KhataEntryType
{
    /// <summary>Credit extended — a bill settled with the "Due" tender. Increases what's owed.</summary>
    Due,

    /// <summary>Money collected against the outstanding balance, in full or in part.
    /// Decreases what's owed.</summary>
    Payment,
}

/// <summary>
/// One line in a customer's khatabook (udhaar ledger). A cafe that lets a regular walk out
/// without paying settles the bill with the "Due" tender (see OrdersController.Pay) — the
/// order itself closes then and there, the table frees up, and the uncollected amount lands
/// here as a Due row against that customer instead of being counted as money received.
/// Later, whatever they hand over comes back as one or more Payment rows (partial is the
/// normal case — that's what a khata is for).
///
/// The customer's outstanding balance is never stored, always summed from these rows
/// (Due minus Payment — see KhatabookController.OutstandingFor). A cached balance column
/// would be one bad concurrent write away from silently disagreeing with the ledger a shop
/// owner is reading off the screen, and there's no volume here that makes the sum expensive.
///
/// Tenant-scoped like everything else; also branch-stamped so a multi-branch cafe can tell
/// where the credit was taken, even though the balance itself is deliberately tenant-wide
/// (a customer owes the business, not a building).
/// </summary>
public class KhataEntry : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    /// <summary>Whose khata this is. Resolved from the name + mobile number that the Due
    /// tender makes compulsory (OrderBuildingService.FindOrCreateCustomerAsync), so the
    /// ledger sits on the same CRM record as the customer's orders and loyalty points
    /// rather than in a parallel address book of its own.</summary>
    public int CustomerId { get; set; }
    /// <summary>Set this (not CustomerId directly) when the customer record was created in
    /// the same SaveChanges — see Order.Customer for why the raw int would still be 0.</summary>
    public Customer? Customer { get; set; }

    public KhataEntryType Type { get; set; }

    /// <summary>Always positive; <see cref="Type"/> carries the direction. Storing a signed
    /// amount instead would make every "how much has this customer actually paid back"
    /// query need to know the sign convention.</summary>
    public decimal Amount { get; set; }

    /// <summary>The bill this credit came from — set on every Due row, null on Payment rows
    /// (a repayment settles the balance as a whole, not one specific old bill; that's the
    /// difference between a khata and per-invoice tracking).</summary>
    public int? OrderId { get; set; }

    /// <summary>How the repayment actually came in — Cash / Card / UPI. Null on Due rows,
    /// which by definition moved no money.</summary>
    public string? Method { get; set; }

    /// <summary>Where the credit was taken / the repayment collected. Null for cafes that
    /// haven't set up branches.</summary>
    public int? BranchId { get; set; }

    public string? Note { get; set; }

    public int? RecordedByUserId { get; set; }
    public string RecordedByName { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
