using CafePOS.Api.Domain;

namespace CafePOS.Api.Contracts;

public record AllowanceLineDto(string Name, decimal Amount);

public record PayrollLineDto(
    int Id, int PayrollRunId, int StaffId, string StaffName, string SalaryType, decimal BasicSalary, decimal? HourlyRate,
    int PresentDays, int LateDays, int HalfDays, int AbsentDays, int PaidLeaveDays, int UnpaidLeaveDays,
    double OvertimeHours, decimal OvertimePay, List<AllowanceLineDto> Allowances, decimal AllowancesTotal, decimal GrossEarnings,
    decimal LeaveDeduction, decimal LateDeduction, decimal LoanDeduction, decimal PfDeduction, decimal EsicDeduction, decimal ProfessionalTaxDeduction,
    decimal TotalDeductions, decimal NetSalary, bool IsEdited)
{
    public static PayrollLineDto From(PayrollLine l) => new(
        l.Id, l.PayrollRunId, l.StaffId, l.StaffName, l.SalaryType.ToString().ToUpperInvariant(), l.BasicSalary, l.HourlyRate,
        l.PresentDays, l.LateDays, l.HalfDays, l.AbsentDays, l.PaidLeaveDays, l.UnpaidLeaveDays,
        l.OvertimeHours, l.OvertimePay, l.Allowances.Select(a => new AllowanceLineDto(a.Name, a.Amount)).ToList(), l.AllowancesTotal, l.GrossEarnings,
        l.LeaveDeduction, l.LateDeduction, l.LoanDeduction, l.PfDeduction, l.EsicDeduction, l.ProfessionalTaxDeduction,
        l.TotalDeductions, l.NetSalary, l.IsEdited);
}

public record PayrollRunDto(
    int Id, DateOnly PeriodStart, DateOnly PeriodEnd, string Status, DateTime GeneratedAt, string GeneratedByName,
    DateTime? LockedAt, DateTime? PaidAt, string? Notes, decimal TotalNetSalary, int StaffCount, List<PayrollLineDto>? Lines)
{
    public static PayrollRunDto From(PayrollRun r, bool includeLines) => new(
        r.Id, r.PeriodStart, r.PeriodEnd, r.Status.ToString().ToUpperInvariant(), r.GeneratedAt, r.GeneratedByName,
        r.LockedAt, r.PaidAt, r.Notes, r.Lines.Sum(l => l.NetSalary), r.Lines.Count,
        includeLines ? r.Lines.OrderBy(l => l.StaffName).Select(PayrollLineDto.From).ToList() : null);
}

public record GeneratePayrollRunRequest(DateOnly PeriodStart, DateOnly PeriodEnd);
public record UpdatePayrollLineRequest(List<AllowanceLineDto>? Allowances, decimal? LateDeduction, decimal? PfDeduction, decimal? EsicDeduction, decimal? ProfessionalTaxDeduction);
