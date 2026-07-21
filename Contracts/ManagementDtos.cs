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
public record CreateNotificationRequest(string Title, string Body, NotificationCategory Category, NotificationChannel Channel = NotificationChannel.InApp, string? ActionUrl = null);

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
public record StaffDto(int Id, string Name, string Role, string? Email, string? Phone, string Status, DateTime JoinedAt, decimal? HourlyRate, int? BranchId, bool HasLogin, string? PhotoUrl)
{
    public static StaffDto From(StaffMember s) => new(s.Id, s.Name, s.Role, s.Email, s.Phone, s.Status.ToString().ToUpperInvariant(), s.JoinedAt, s.HourlyRate, s.BranchId, s.UserId is not null, s.PhotoUrl);
}

/// <summary>
/// Password/LoginRole are optional: leave both null to create a plain HR roster
/// entry with no app access (matches StaffMember.UserId being nullable). Supply all
/// three of Email/Password/LoginRole together to also provision a real login account
/// for this tenant — Email becomes required in that case.
/// </summary>
public record CreateStaffRequest(string Name, string Role, string? Email, string? Phone, decimal? HourlyRate, int? BranchId, string? Password, AppRole? LoginRole);
public record UpdateStaffStatusRequest(StaffStatus Status);

/// <summary>Partial update for the Staff Profile edit flow — only non-null fields are
/// applied, matching MenuController.Update's pattern.</summary>
public record UpdateStaffRequest(string? Name, string? Role, string? Email, string? Phone, decimal? HourlyRate, int? BranchId, string? PhotoUrl);
public record ResetStaffPasswordRequest(string NewPassword);

// ---------- Staff screen access ----------
public record StaffScreenAccessDto(int StaffId, string AccessMode, List<string> AllowedScreens)
{
    public static StaffScreenAccessDto From(int staffId, AppUser user) =>
        new(staffId, user.AccessMode.ToString(), user.AllowedScreens ?? []);
}
public record UpdateStaffScreenAccessRequest(StaffAccessMode AccessMode, List<string>? AllowedScreens);

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

public record PayrollLineDto(int StaffId, string StaffName, decimal HourlyRate, double HoursWorked, decimal GrossPay);

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
