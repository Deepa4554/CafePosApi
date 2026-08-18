namespace CafePOS.Api.Contracts;

// ---------- Owner Daily-Audit Reports ----------

/// <summary>Current valuation is always "as of now" — it does NOT shift with a past `to`
/// date, since Current/UnitCost are live fields, not historical snapshots. The movement
/// columns (Opening..Closing) are only populated when a date range was requested.</summary>
public record StockReportLineDto(
    int InventoryItemId, string Name, string Category, string Unit, double CurrentQty, decimal UnitCost, decimal CurrentValue,
    double? OpeningBalance, double? Purchased, double? Sold, double? Wasted, double? Other, double? ClosingBalance);

/// <summary>Revenue/COGS can be branch-scoped (Order.BranchId); Expenses cannot
/// (CafeExpense has no BranchId column) — always whole-tenant regardless of the
/// `branchId` filter. OrdersWithoutRecipeCost surfaces the FoodCost() blind spot: a menu
/// item sold with no Recipe on file contributes zero COGS, silently understating cost —
/// this count links the owner to the existing Missing Recipes alert list as the fix.</summary>
public record ProfitReportDto(decimal Revenue, decimal Cogs, decimal GrossProfit, decimal Expenses, decimal NetProfit, int OrdersWithoutRecipeCost, List<ProfitDayLineDto> Daily);
public record ProfitDayLineDto(string Day, decimal Revenue, decimal Cogs, decimal Expenses);

public record SalesItemLineDto(int MenuItemId, string Name, int QtySold, decimal NetSales);
public record SalesCategoryLineDto(string Category, int QtySold, decimal NetSales);
/// <summary>Sourced from Order.Payments (one row per tender), not the summary
/// Order.PaymentMethod string — the only correct source once split payments exist.</summary>
public record SalesPaymentLineDto(string Method, decimal Amount, int TxnCount);
public record SalesReportDto(decimal GrossSales, decimal TotalDiscounts, decimal NetSales, decimal RefundsTotal, int OrderCount, List<SalesItemLineDto> ItemWise, List<SalesCategoryLineDto> CategoryWise, List<SalesPaymentLineDto> PaymentModeWise);

public record TaxRateLineDto(decimal RatePct, decimal TaxableAmount, decimal TaxAmount, int LineCount);
/// <summary>Bill-level tax detail for filing — one row per order, so the rate-wise totals
/// above can be traced back to individual invoices.</summary>
public record TaxBillLineDto(int OrderId, string OrderNumber, string Title, DateTime CreatedAt, decimal TaxableAmount, decimal TaxAmount);
public record TaxGstReportDto(decimal TotalTaxableAmount, decimal TotalTaxCollected, List<TaxRateLineDto> ByRate, List<TaxBillLineDto> Bills);

// ---------- Bill-wise Order Detail ----------

/// <summary>Voided lines are returned rather than filtered out — an owner reading a bill
/// needs to see what was struck off it. They contribute nothing to the order's totals.</summary>
public record OrderDetailItemDto(string Name, string? VariantName, int Qty, decimal Price, decimal LineTotal, decimal TaxAmount, bool Voided);

/// <summary>DiscountTotal folds together all five separate reductions the schema tracks
/// (order-time, bill-time, coupon, gift card, loyalty) — the bill register wants one
/// "what came off this bill" number, not five columns of mostly zeroes.</summary>
public record OrderDetailLineDto(
    // OrderNumber, not OrderId, is what the guest's copy of this bill says — the register is
    // only auditable against physical bills if the two agree. See OrderNumberFormat.
    int OrderId, string OrderNumber, string Title, DateTime CreatedAt, string OrderType, string? TableCode, int? TokenNumber,
    string? CustomerName, string? CustomerPhone,
    decimal Subtotal, decimal DiscountTotal, decimal Tax, decimal Total,
    string? PaymentMethod, bool Paid, bool Refunded, decimal? RefundedAmount,
    int ItemCount, List<OrderDetailItemDto> Items);

/// <summary>Capped at <see cref="OrdersReportDto.MaxRows"/> orders (newest first) — a busy
/// cafe can put tens of thousands of bills in a 30-day window, and materialising all of them
/// with their line items would blow out both the response and the export. Truncated tells the
/// UI to say so out loud rather than silently showing a partial register.</summary>
public record OrdersReportDto(
    int OrderCount, decimal GrossTotal, decimal DiscountTotal, decimal TaxTotal, decimal NetTotal, decimal RefundTotal,
    bool Truncated, List<OrderDetailLineDto> Orders)
{
    public const int MaxRows = 2000;
}

/// <summary>One identified customer's activity. Period figures (VisitsInPeriod/SpentInPeriod)
/// come from Order rows in range and DO respect the branch filter; lifetime figures come off
/// the Customer row itself and are whole-tenant — a customer isn't owned by a branch.</summary>
public record CrmReportCustomerLineDto(
    int CustomerId, string Name, string? Phone, string Tier,
    int VisitsInPeriod, decimal SpentInPeriod, decimal AvgOrderValueInPeriod,
    int LifetimeVisits, decimal LifetimeSpent, int AvailablePoints,
    DateTime LastVisitAt, DateTime JoinedAt, bool IsNewInPeriod);

/// <summary>Owner-facing CRM summary + per-customer detail. "Identified" means the order was
/// linked to a Customer; everything else is a walk-in. That split is the headline number —
/// it tells the owner how much of the business they can actually market to.
/// PointsOutstanding is a real liability figure (sum of every customer's unredeemed balance,
/// lifetime and whole-tenant), NOT a period number — unlike PointsRedeemedInPeriod, which is
/// summed from Order.LoyaltyPointsRedeemed inside the range.</summary>
public record CrmReportDto(
    int ActiveCustomers, int NewCustomers, int ReturningCustomers, double RepeatRatePct,
    int LapsedCustomers,
    decimal RevenueFromCustomers, decimal RevenueFromWalkIns, double IdentifiedRevenuePct,
    decimal AvgSpendPerCustomer, double AvgVisitsPerCustomer,
    int PointsRedeemedInPeriod, int PointsOutstanding,
    List<CrmReportCustomerLineDto> Customers);

// ---------- HR Reports ----------

public record DailyAttendanceReportLineDto(int StaffId, string StaffName, string Role, DateOnly Date, string ShiftKind, string Status, DateTime? PunchInAt, DateTime? PunchOutAt, int? WorkedMinutes, int LateMinutes);

public record MonthlyAttendanceReportLineDto(int StaffId, string StaffName, string Role, int PresentDays, int LateDays, int HalfDays, int AbsentDays, int LeaveDays, double TotalWorkedHours);

public record OvertimeReportLineDto(int StaffId, string StaffName, string Role, double TotalOvertimeHours, int OvertimeDays);

public record EmployeeListLineDto(
    int StaffId, string Name, string Role, string? Department, string? Designation, string? BranchName,
    DateTime JoinedAt, string Status, string SalaryType, decimal? BasicSalary, decimal? HourlyRate, bool HasLogin);
