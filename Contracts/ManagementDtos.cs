using CafePOS.Api.Domain;

namespace CafePOS.Api.Contracts;

// ---------- Tasks ----------
public record TaskDto(int Id, string Title, string? Description, int? AssignedToId, string Priority, string Status, DateTime DueDate, DateTime CreatedAt, List<string> Tags)
{
    public static TaskDto From(StaffTask t) => new(
        t.Id, t.Title, t.Description, t.AssignedToId, t.Priority.ToString().ToUpperInvariant(), t.Status.ToString().ToUpperInvariant(),
        t.DueDate, t.CreatedAt, string.IsNullOrEmpty(t.TagsCsv) ? [] : t.TagsCsv.Split(',').ToList());
}
public record CreateTaskRequest(string Title, string? Description, int? AssignedToId, TaskPriority Priority, DateTime DueDate, List<string>? Tags);
public record UpdateTaskStatusRequest(StaffTaskStatus Status);

// ---------- Notifications ----------
public record NotificationDto(int Id, string Title, string Body, string Category, string Channel, bool IsRead, bool IsArchived, string DeliveryStatus, DateTime CreatedAt, string? ActionUrl)
{
    public static NotificationDto From(AppNotification n) => new(
        n.Id, n.Title, n.Body, n.Category.ToString().ToUpperInvariant(), n.Channel.ToString().ToUpperInvariant(),
        n.IsRead, n.IsArchived, n.DeliveryStatus.ToString().ToUpperInvariant(), n.CreatedAt, n.ActionUrl);
}
/// <summary>TargetRoles/TargetUserId mirror AppNotification's own TargetUserId/TargetRolesCsv
/// (see that entity's doc comments) — null on both means today's existing tenant-wide
/// behavior; set TargetRoles to reach only e.g. ["Owner","Manager"] instead of everyone,
/// or TargetUserId to reach one specific staff member's devices. See
/// NotificationsController.Create for the validation each one gets.</summary>
public record CreateNotificationRequest(string Title, string Body, NotificationCategory Category, NotificationChannel Channel = NotificationChannel.InApp, string? ActionUrl = null, List<AppRole>? TargetRoles = null, int? TargetUserId = null);

// ---------- Notification Category Preferences (generic, non-NamedGates categories) ----------
public record NotificationCategoryPreferenceDto(NotificationCategory Category, bool Enabled);
public record UpdateNotificationCategoryPreferenceRequest(NotificationCategory Category, bool Enabled);

/// <summary><paramref name="EnabledForCafe"/> is the tenant-level switch (Owner/Manager only,
/// see SettingsController) shown alongside the user's own so the UI can explain a category the
/// cafe has switched off entirely — muting it personally would otherwise look like it did
/// nothing. <paramref name="Enabled"/> is this user's own choice, defaulting to true.</summary>
public record MyNotificationPreferenceDto(NotificationCategory Category, bool Enabled, bool EnabledForCafe);
public record UpdateMyNotificationPreferenceRequest(NotificationCategory Category, bool Enabled);

public record RegisterDeviceTokenRequest(string Token, DevicePlatform Platform);
public record UnregisterDeviceTokenRequest(string Token);

// ---------- Approvals ----------
public record ApprovalDto(int Id, string Type, int RequestedById, int AssignedToId, string Title, string Description, decimal? Amount, string Currency, string Status, int Level, DateTime CreatedAt, DateTime? ResolvedAt, string? Notes)
{
    public static ApprovalDto From(ApprovalRequest a) => new(
        a.Id, a.Type.ToString().ToUpperInvariant(), a.RequestedById, a.AssignedToId, a.Title, a.Description,
        a.Amount, a.Currency, a.Status.ToString().ToUpperInvariant(), a.Level, a.CreatedAt, a.ResolvedAt, a.Notes);
}
public record SubmitApprovalRequest(ApprovalType Type, int AssignedToId, string Title, string Description, decimal? Amount, string Currency = "INR", int Level = 1);
public record ResolveApprovalRequest(string? Notes);

// ---------- Audit ----------
public record AuditEntryDto(int Id, DateTime Timestamp, int? UserId, string UserName, string Action, string Resource, string? ResourceId, string Details, string Severity)
{
    public static AuditEntryDto From(AuditLogEntry a) => new(a.Id, a.Timestamp, a.UserId, a.UserName, a.Action.ToString().ToUpperInvariant(), a.Resource.ToString().ToUpperInvariant(), a.ResourceId, a.Details, a.Severity.ToString().ToUpperInvariant());
}

// ---------- Staff ----------
// Bank/Aadhaar/PAN are deliberately absent here — those live only behind
// StaffFinancialDetailsDto below. Compensation fields (HourlyRate/BasicSalary) and
// Department/Designation ARE part of this shape, but StaffController.List/Get only ever
// populate them when the caller is Owner/Manager (includeCompensation: false otherwise) —
// GET /staff and GET /staff/{id} are reachable by every authenticated role, and a
// Waiter/Cashier has no business seeing a coworker's pay (SECURITY_AUDIT_2026-07-30
// finding #4).
public record StaffDto(
    int Id, string Name, string Role, string? Email, string? Phone, string Status, DateTime JoinedAt,
    decimal? HourlyRate, int? BranchId, bool HasLogin, string? PhotoUrl,
    string? Department, string? Designation, string SalaryType, decimal? BasicSalary)
{
    public static StaffDto From(StaffMember s, bool includeCompensation = true) => new(
        s.Id, s.Name, s.Role, s.Email, s.Phone, s.Status.ToString().ToUpperInvariant(), s.JoinedAt,
        includeCompensation ? s.HourlyRate : null, s.BranchId, s.UserId is not null, s.PhotoUrl,
        includeCompensation ? s.Department : null, includeCompensation ? s.Designation : null,
        s.SalaryType.ToString().ToUpperInvariant(), includeCompensation ? s.BasicSalary : null);
}

/// <summary>
/// Password/LoginRole are optional: leave both null to create a plain HR roster
/// entry with no app access (matches StaffMember.UserId being nullable). Supply all
/// three of Email/Password/LoginRole together to also provision a real login account
/// for this tenant — Email becomes required in that case.
/// </summary>
public record CreateStaffRequest(
    string Name, string Role, string? Email, string? Phone, decimal? HourlyRate, int? BranchId, string? Password, AppRole? LoginRole,
    string? Department = null, string? Designation = null, SalaryType SalaryType = SalaryType.Monthly, decimal? BasicSalary = null,
    string? BankAccountNumber = null, string? BankIfsc = null, string? BankName = null, string? Aadhaar = null, string? Pan = null);
public record UpdateStaffStatusRequest(StaffStatus Status);

/// <summary>Partial update for the Staff Profile edit flow — only non-null fields are
/// applied, matching MenuController.Update's pattern.</summary>
public record UpdateStaffRequest(
    string? Name, string? Role, string? Email, string? Phone, decimal? HourlyRate, int? BranchId, string? PhotoUrl,
    string? Department = null, string? Designation = null, SalaryType? SalaryType = null, decimal? BasicSalary = null,
    string? BankAccountNumber = null, string? BankIfsc = null, string? BankName = null, string? Aadhaar = null, string? Pan = null);
public record ResetStaffPasswordRequest(string NewPassword);

/// <summary>Masked by default (e.g. "XXXX XXXX 1234") — StaffController.GetFinancialDetails
/// only returns unmasked values when called with ?reveal=true, which also writes an audit
/// entry. Null fields mean "not set", not "masked".</summary>
public record StaffFinancialDetailsDto(int StaffId, string? BankAccountNumber, string? BankIfsc, string? BankName, string? Aadhaar, string? Pan, bool Revealed);

// ---------- Staff screen access ----------
public record StaffScreenAccessDto(int StaffId, string AccessMode, List<string> AllowedScreens)
{
    public static StaffScreenAccessDto From(int staffId, AppUser user) =>
        new(staffId, user.AccessMode.ToString(), user.AllowedScreens ?? []);
}
public record UpdateStaffScreenAccessRequest(StaffAccessMode AccessMode, List<string>? AllowedScreens);

// ---------- Staff kitchen assignment (KDS station pinning) ----------
public record StaffKitchenAssignmentDto(int StaffId, int? AssignedStationId)
{
    public static StaffKitchenAssignmentDto From(int staffId, AppUser user) => new(staffId, user.AssignedStationId);
}
public record UpdateStaffKitchenAssignmentRequest(int? AssignedStationId);

public record ShiftDto(int Id, int StaffId, DateTime StartsAt, DateTime EndsAt, string? Notes)
{
    public static ShiftDto From(Shift s) => new(s.Id, s.StaffId, s.StartsAt, s.EndsAt, s.Notes);
}
public record CreateShiftRequest(int StaffId, DateTime StartsAt, DateTime EndsAt, string? Notes);

/// <summary>Powers the Team Schedule day view — every shift across every staff member
/// on a given day, with the staff's name/role denormalized in so the screen doesn't
/// need a second round trip per row.</summary>
public record ShiftWithStaffDto(int Id, int StaffId, string StaffName, string StaffRole, DateTime StartsAt, DateTime EndsAt, string? Notes);

/// <summary>Powers the Performance Reports leaderboard — every staff member's reviews
/// rolled up into one row, ranked by revenue server-side so the screen just renders
/// top to bottom. AttendanceRatePct is null when the staff member has no completed
/// shifts yet — there's nothing to measure attendance against.</summary>
public record StaffPerformanceSummaryDto(int StaffId, string StaffName, string StaffRole, int TotalOrders, decimal TotalRevenue, double? AttendanceRatePct);

public record LeaveRequestDto(
    int Id, int StaffId, string StaffName, DateOnly StartDate, DateOnly EndDate, string Type,
    string? Reason, string Status, string? ReviewedByName, DateTime? ReviewedAt, DateTime CreatedAt)
{
    public static LeaveRequestDto From(LeaveRequest l) => new(
        l.Id, l.StaffId, l.StaffName, l.StartDate, l.EndDate, l.Type.ToString(),
        l.Reason, l.Status.ToString(), l.ReviewedByName, l.ReviewedAt, l.CreatedAt);
}
public record CreateLeaveRequest(int StaffId, DateOnly StartDate, DateOnly EndDate, LeaveType Type, string? Reason);
public record ReviewLeaveRequest(string? Note);

public record CafeExpenseDto(int Id, decimal Amount, string Category, string Purpose, string SpentBy, DateTime SpentAt, string RecordedByName, DateTime CreatedAt)
{
    public static CafeExpenseDto From(CafeExpense e) => new(e.Id, e.Amount, e.Category.ToString(), e.Purpose, e.SpentBy, e.SpentAt, e.RecordedByName, e.CreatedAt);
}
public record CreateCafeExpenseRequest(decimal Amount, ExpenseCategory Category, string Purpose, string SpentBy, DateTime? SpentAt);
public record CategoryTotalDto(string Category, decimal Total);
public record CafeExpenseSummaryDto(decimal TotalAllTime, decimal TotalThisMonth, List<CategoryTotalDto> ByCategoryThisMonth, List<CafeExpenseDto> Recent);
/// <summary>Not branch-scoped — CafeExpense has no BranchId column, so this is always a
/// whole-tenant total regardless of any branch filter elsewhere in the Reports hub.</summary>
public record CafeExpenseReportDto(decimal Total, List<CategoryTotalDto> ByCategory, List<CafeExpenseDto> Lines);

// ---------- Branches ----------
public record BranchDto(int Id, string Name, string Address, bool IsActive)
{
    public static BranchDto From(Branch b) => new(b.Id, b.Name, b.Address, b.IsActive);
}
public record CreateBranchRequest(string Name, string Address);

// ---------- Subscription ----------
public record SubscriptionDto(string Plan, DateTime? PlanExpiresAt, int MonthlyOrdersUsed, int MaxOrdersPerMonth, int MaxBranches, int MaxStaff)
{
    public static SubscriptionDto From(Subscription s, int branchCount, int staffCount, int monthlyOrdersUsed)
    {
        var limits = PlanLimits[s.Plan];
        return new SubscriptionDto(s.Plan.ToString().ToUpperInvariant(), s.PlanExpiresAt, monthlyOrdersUsed, limits.orders, limits.branches, limits.staff);
    }

    public static readonly Dictionary<SubscriptionTier, (int orders, int branches, int staff)> PlanLimits = new()
    {
        [SubscriptionTier.FreeTrial] = (200, 1, 3),
        [SubscriptionTier.Starter] = (1000, 1, 5),
        [SubscriptionTier.Professional] = (10000, 3, 25),
        [SubscriptionTier.Enterprise] = (int.MaxValue, int.MaxValue, int.MaxValue),
    };
}
public record ChangePlanRequest(SubscriptionTier Plan, string? CouponCode);

// ---------- Integrations ----------
public record IntegrationDto(int Id, string Name, string Category, string Status, DateTime? ConnectedAt)
{
    public static IntegrationDto From(Integration i) => new(i.Id, i.Name, i.Category, i.Status.ToString().ToUpperInvariant(), i.ConnectedAt);
}
