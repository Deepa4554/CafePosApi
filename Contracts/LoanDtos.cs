using CafePOS.Api.Domain;

namespace CafePOS.Api.Contracts;

public record StaffLoanDto(
    int Id, int StaffId, string StaffName, string Type, decimal PrincipalAmount, decimal MonthlyDeduction,
    DateOnly StartDate, string Status, decimal OutstandingBalance, string? Reason, string ApprovedByName, DateTime CreatedAt, DateTime? ClosedAt)
{
    public static StaffLoanDto From(StaffLoan l) => new(
        l.Id, l.StaffId, l.StaffName, l.Type.ToString().ToUpperInvariant(), l.PrincipalAmount, l.MonthlyDeduction,
        l.StartDate, l.Status.ToString().ToUpperInvariant(), l.OutstandingBalance, l.Reason, l.ApprovedByName, l.CreatedAt, l.ClosedAt);
}

public record CreateStaffLoanRequest(int StaffId, LoanType Type, decimal PrincipalAmount, decimal MonthlyDeduction, DateOnly? StartDate, string? Reason);
public record UpdateStaffLoanRequest(decimal? MonthlyDeduction, string? Reason);
