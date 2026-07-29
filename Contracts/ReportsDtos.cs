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
public record TaxGstReportDto(decimal TotalTaxableAmount, decimal TotalTaxCollected, List<TaxRateLineDto> ByRate);

// ---------- HR Reports ----------

public record DailyAttendanceReportLineDto(int StaffId, string StaffName, string Role, DateOnly Date, string Status, DateTime? PunchInAt, DateTime? PunchOutAt, int? WorkedMinutes, int LateMinutes);

public record MonthlyAttendanceReportLineDto(int StaffId, string StaffName, string Role, int PresentDays, int LateDays, int HalfDays, int AbsentDays, int LeaveDays, double TotalWorkedHours);

public record OvertimeReportLineDto(int StaffId, string StaffName, string Role, double TotalOvertimeHours, int OvertimeDays);

public record EmployeeListLineDto(
    int StaffId, string Name, string Role, string? Department, string? Designation, string? BranchName,
    DateTime JoinedAt, string Status, string SalaryType, decimal? BasicSalary, decimal? HourlyRate, bool HasLogin);
