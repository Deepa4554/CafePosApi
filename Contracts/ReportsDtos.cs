namespace CafePOS.Api.Contracts;

// ---------- HR Reports ----------

public record DailyAttendanceReportLineDto(int StaffId, string StaffName, string Role, DateOnly Date, string Status, DateTime? PunchInAt, DateTime? PunchOutAt, int? WorkedMinutes, int LateMinutes);

public record MonthlyAttendanceReportLineDto(int StaffId, string StaffName, string Role, int PresentDays, int LateDays, int HalfDays, int AbsentDays, int LeaveDays, double TotalWorkedHours);

public record OvertimeReportLineDto(int StaffId, string StaffName, string Role, double TotalOvertimeHours, int OvertimeDays);

public record EmployeeListLineDto(
    int StaffId, string Name, string Role, string? Department, string? Designation, string? BranchName,
    DateTime JoinedAt, string Status, string SalaryType, decimal? BasicSalary, decimal? HourlyRate, bool HasLogin);
