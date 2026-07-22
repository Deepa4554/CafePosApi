namespace CafePOS.Api.Domain;

// ---------- Staff Loans / Advances ----------

public enum LoanType { Advance, Loan }
public enum LoanStatus { Active, Closed }

/// <summary>OutstandingBalance is touched only when a PayrollRun containing this
/// loan's deduction is marked Paid (see PayrollController.MarkPaid) — never at Draft
/// generation, so regenerating a Draft run multiple times before finalizing can never
/// double-deduct.</summary>
public class StaffLoan : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int StaffId { get; set; }
    public required string StaffName { get; set; }
    public LoanType Type { get; set; }
    public decimal PrincipalAmount { get; set; }
    public decimal MonthlyDeduction { get; set; }
    public DateOnly StartDate { get; set; }
    public LoanStatus Status { get; set; } = LoanStatus.Active;
    public decimal OutstandingBalance { get; set; }
    public string? Reason { get; set; }
    public int ApprovedByUserId { get; set; }
    public string ApprovedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
}

// ---------- Payroll ----------

public enum PayrollRunStatus { Draft, Locked, Paid }

/// <summary>One run per tenant per period (not per staff) — a single Owner action
/// locks/pays the whole month as one unit, which is what makes a tenant-wide Salary
/// Register report and Bank Transfer Export well-defined.</summary>
public class PayrollRun : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public PayrollRunStatus Status { get; set; } = PayrollRunStatus.Draft;
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public int GeneratedByUserId { get; set; }
    public string GeneratedByName { get; set; } = "";
    public DateTime? LockedAt { get; set; }
    public int? LockedByUserId { get; set; }
    public DateTime? PaidAt { get; set; }
    public int? PaidByUserId { get; set; }
    public string? Notes { get; set; }
    public List<PayrollLine> Lines { get; set; } = [];
}

public record AllowanceLine(string Name, decimal Amount);

/// <summary>
/// Every input this line's numbers depend on is captured HERE at generation time —
/// Attendance/Leave/Loan data can change afterwards (e.g. a manager correcting a punch
/// time) without this row ever drifting. This is deliberately the only lock mechanism
/// in the whole feature: nothing in AttendanceController/StaffLoansController checks
/// "is this period locked" before writing — a locked payroll number is just an
/// immutable snapshot, not a live computation, so there's no code path that could
/// forget to check a lock and leak a drift. To fix numbers after generation: delete a
/// Draft run and regenerate, or Owner-only reopen a Locked run back to Draft (never
/// from Paid — money already moved by then).
/// </summary>
public class PayrollLine : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int PayrollRunId { get; set; }
    public int StaffId { get; set; }
    public string StaffName { get; set; } = "";
    public SalaryType SalaryType { get; set; }
    public decimal BasicSalary { get; set; }
    public decimal? HourlyRate { get; set; }
    public int PresentDays { get; set; }
    public int LateDays { get; set; }
    public int HalfDays { get; set; }
    public int AbsentDays { get; set; }
    public int PaidLeaveDays { get; set; }
    public int UnpaidLeaveDays { get; set; }
    public double OvertimeHours { get; set; }
    public decimal OvertimePay { get; set; }
    public List<AllowanceLine> Allowances { get; set; } = [];
    public decimal AllowancesTotal { get; set; }
    public decimal GrossEarnings { get; set; }
    public decimal LeaveDeduction { get; set; }
    public decimal LateDeduction { get; set; }
    public decimal LoanDeduction { get; set; }
    public decimal PfDeduction { get; set; }
    public decimal EsicDeduction { get; set; }
    public decimal ProfessionalTaxDeduction { get; set; }
    public decimal TotalDeductions { get; set; }
    public decimal NetSalary { get; set; }
    /// <summary>Snapshotted at generation time so a later bank-detail edit on
    /// StaffMember never changes an already-generated (let alone locked) line.</summary>
    public string? BankAccountNumberSnapshot { get; set; }
    public string? BankIfscSnapshot { get; set; }
    public bool IsEdited { get; set; }
    public int? EditedByUserId { get; set; }
}
