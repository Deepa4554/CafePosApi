namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Above these amounts, a non-Owner-initiated Refund/Discount/Expense is held as a pending
/// ApprovalRequest instead of executing immediately — the Owner is the only one who can
/// resolve it (see ApprovalsController.Approve/Reject). Owner-initiated actions always go
/// straight through regardless of amount, since the Owner IS the approver. Flat constants
/// rather than a configurable Settings field — no UI/product ask yet for cafes to tune these
/// themselves; bump the numbers here if that changes.
/// </summary>
public static class ApprovalThresholds
{
    public const decimal RefundAmount = 2000m;
    public const decimal DiscountAmount = 500m;
    public const decimal ExpenseAmount = 10000m;
}
