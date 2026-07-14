namespace CafePOS.Api.Domain;

// ---------- Tasks ----------

public enum TaskPriority { Low, Medium, High, Urgent }
public enum StaffTaskStatus { Todo, InProgress, Done, Blocked }

public class StaffTask : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public int? AssignedToId { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public StaffTaskStatus Status { get; set; } = StaffTaskStatus.Todo;
    public DateTime DueDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? TagsCsv { get; set; }
}

// ---------- Notifications ----------

public enum NotificationChannel { Push, Email, Sms, WhatsApp, InApp }
public enum NotificationCategory { Order, OrderPlaced, Inventory, Billing, Staff, System, Marketing, AiInsight }
public enum DeliveryStatus { Pending, Sent, Delivered, Failed, Retrying }

public class AppNotification : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public NotificationCategory Category { get; set; }
    public NotificationChannel Channel { get; set; } = NotificationChannel.InApp;
    public bool IsRead { get; set; }
    public bool IsArchived { get; set; }
    public DeliveryStatus DeliveryStatus { get; set; } = DeliveryStatus.Delivered;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ScheduledAt { get; set; }
    public string? ActionUrl { get; set; }
}

// ---------- Approvals ----------

public enum ApprovalType { Refund, Discount, Expense, Salary, InventoryAdjustment, StockTransfer, Leave }
public enum ApprovalStatus { Pending, Approved, Rejected, Escalated }

public class ApprovalRequest : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public ApprovalType Type { get; set; }
    public int RequestedById { get; set; }
    public int AssignedToId { get; set; }
    public required string Title { get; set; }
    public string Description { get; set; } = "";
    public decimal? Amount { get; set; }
    public string Currency { get; set; } = "INR";
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public int Level { get; set; } = 1; // 1=Manager, 2=Owner, 3=Super
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
    public int? ResolvedById { get; set; }
    public string? Notes { get; set; }
}

// ---------- Audit Log ----------

public enum AuditAction
{
    Login, Logout, FailedLogin, PasswordChange,
    Create, Update, Delete,
    Refund, Discount, Coupon,
    InventoryChange, StockTransfer,
    RoleChange, PermissionChange,
    BillingChange, SubscriptionChange,
    SettingsChange, Export,
    ApprovalGranted, ApprovalDenied,
}

public enum AuditResource { Order, Customer, Staff, Inventory, Menu, Invoice, Subscription, Auth, Settings, Branch }
public enum AuditSeverity { Low, Medium, High, Critical }

public class AuditLogEntry : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public int? UserId { get; set; }
    public string UserName { get; set; } = "System";
    public AuditAction Action { get; set; }
    public AuditResource Resource { get; set; }
    public string? ResourceId { get; set; }
    public required string Details { get; set; }
    public string? IpAddress { get; set; }
    public AuditSeverity Severity { get; set; } = AuditSeverity.Low;
}

// ---------- Staff / Team ----------

public enum StaffStatus { Active, Suspended, OnLeave, Terminated }

public class StaffMember : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>Linked login account, if this staff member has app access.</summary>
    public int? UserId { get; set; }
    public required string Name { get; set; }
    public required string Role { get; set; } // display role, e.g. "Barista", distinct from AppRole
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public StaffStatus Status { get; set; } = StaffStatus.Active;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    public decimal? HourlyRate { get; set; }
    public int? BranchId { get; set; }
    /// <summary>Data URI or external URL — no blob storage service exists yet, so the
    /// image itself (for a picked photo) is stored inline as a base64 data URI, same
    /// pattern as AppUser.ProfilePhoto/Customer.ProfilePhotoUrl.</summary>
    public string? PhotoUrl { get; set; }
}

public class Shift : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int StaffId { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public string? Notes { get; set; }
}

public enum LeaveType { Sick, Casual, Paid, Unpaid }
public enum LeaveRequestStatus { Pending, Approved, Rejected }

/// <summary>A staff member's request for time off. Approving one immediately flips
/// the linked StaffMember.Status to OnLeave (this app has no background job runner
/// to auto-transition on the start date, so it's an explicit staff/manager action
/// instead — "Return to Work" flips it back).</summary>
public class LeaveRequest : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int StaffId { get; set; }
    public required string StaffName { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public LeaveType Type { get; set; }
    public string? Reason { get; set; }
    public LeaveRequestStatus Status { get; set; } = LeaveRequestStatus.Pending;
    public int? ReviewedByUserId { get; set; }
    public string? ReviewedByName { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ---------- Cafe operating expenses ----------

public enum ExpenseCategory { Rent, Salaries, Utilities, Maintenance, Supplies, Marketing, Other }

/// <summary>A cafe's own day-to-day running costs (rent, salaries, electricity, repairs,
/// ...) — tenant-scoped, distinct from PlatformExpense which is CafePOS-the-startup's
/// own books and has nothing to do with any cafe.</summary>
public class CafeExpense : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public decimal Amount { get; set; }
    public ExpenseCategory Category { get; set; }
    public required string Purpose { get; set; }
    public required string SpentBy { get; set; }
    public DateTime SpentAt { get; set; } = DateTime.UtcNow;
    public int RecordedByUserId { get; set; }
    public string RecordedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ---------- Branches / Tenant / Subscription ----------

public class Branch : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public required string Name { get; set; }
    public required string Address { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum SubscriptionTier { FreeTrial, Starter, Professional, Enterprise }

public class Subscription : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public SubscriptionTier Plan { get; set; } = SubscriptionTier.FreeTrial;
    /// <summary>
    /// When the CURRENT plan cycle ends — FreeTrial gets 7 days, every paid tier gets
    /// 1 month, reset on every /subscription/change-plan call (including re-selecting
    /// the same plan, which is today's stand-in for "renew" since there's no real
    /// payment gateway yet — see docs/multi-tenant-saas-plan.md). Checked by
    /// SubscriptionExpiryMiddleware to lock the tenant out once it passes.
    /// </summary>
    public DateTime? PlanExpiresAt { get; set; }
    public int MonthlyOrdersUsed { get; set; }
    public string? ActiveCouponCode { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// ---------- Integrations ----------

public enum IntegrationStatus { Connected, Disconnected, Error }

public class Integration : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public required string Name { get; set; } // e.g. "Zomato", "WhatsApp Business", "Tally"
    public required string Category { get; set; } // Delivery / Payments / Accounting / Marketing
    public IntegrationStatus Status { get; set; } = IntegrationStatus.Disconnected;
    public DateTime? ConnectedAt { get; set; }
}
