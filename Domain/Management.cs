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
// OrderPendingConfirmation is deliberately its own category (not OrderPlaced) — kitchen
// roles (Chef/KitchenStaff) are filtered to only NotificationCategory.OrderPlaced
// (NotificationsController.List), and they can't act on a confirmation prompt anyway.
// Persisted as its own name, not an ordinal (see CafePosDbContext.OnModelCreating's
// HasConversion<string>), so new members are safe to append without rewriting existing rows.
public enum NotificationCategory { Order, OrderPlaced, OrderPendingConfirmation, Inventory, Billing, Staff, System, Marketing, AiInsight, Task, Approval }
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
    /// <summary>Null = tenant-wide (existing behavior: every non-kitchen user for most
    /// categories, see FcmPushNotificationSender/NotificationsController.List). Set = this
    /// notification is meant for exactly one AppUser (e.g. "task assigned to you") — it's
    /// hidden from every other user's Notification Center and only that user's devices get
    /// the push, regardless of category.</summary>
    public int? TargetUserId { get; set; }
    /// <summary>Null = every user the category already reaches (existing tenant-wide behavior).
    /// Set = comma-separated AppRole names, and only users holding one of them see this in their
    /// Notification Center or get the push — the middle ground between tenant-wide and
    /// TargetUserId, for categories that are management's business but not one named person's
    /// (Billing, Approval, Payroll). Matching is always done on comma-wrapped values so that
    /// "Staff" can never match "KitchenStaff" — see FcmPushNotificationSender.ParseRoles and
    /// NotificationsController.List.</summary>
    public string? TargetRolesCsv { get; set; }
}

/// <summary>
/// One staff member's personal mute for a notification category — the per-USER tier, sitting
/// under the per-TENANT one in NotificationPreferences (a category the Owner has switched off
/// for the cafe never reaches anybody, regardless of these rows).
///
/// Absence means enabled: a row only ever exists once someone has deliberately turned a
/// category off (or back on again), so "default on" needs no backfill for existing users and no
/// row at all for the overwhelmingly common case of a user who never touches these settings.
///
/// Only ever consulted for BROADCAST notifications — anything addressed to one person via
/// AppNotification.TargetUserId (a task assigned to you, your own approval's outcome) is always
/// delivered, since that's direct correspondence rather than a feed someone can opt out of.
/// See FcmPushNotificationSender for the push side and NotificationsController.List for in-app.
/// </summary>
public class UserNotificationPreference : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int UserId { get; set; }
    public NotificationCategory Category { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>Named audiences for AppNotification.TargetRolesCsv, built from the AppRole members
/// themselves rather than hand-typed strings so a role rename can't silently turn into a CSV
/// entry that matches nobody (an audience that quietly reaches zero people is invisible until
/// someone notices a notification never arrived).</summary>
public static class NotificationAudience
{
    /// <summary>Notices only whoever runs the cafe can act on — low stock, shift reports.
    /// A waiter has no screen to do anything about either, so broadcasting them tenant-wide
    /// just trains everyone to ignore the bell.</summary>
    public static readonly string Management = string.Join(',', new[] { AppRole.Owner, AppRole.Manager });
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
    /// <summary>Id of the entity this approval acts ON, for types where one already exists
    /// at request time — Order.Id for Refund/Discount, LeaveRequest.Id for Leave. Null for
    /// Expense, which doesn't exist yet until approved (see PayloadJson).</summary>
    public int? LinkedEntityId { get; set; }
    /// <summary>Serialized CreateCafeExpenseRequest for Expense-type requests — nothing to
    /// link to yet at request time, so the fields needed to actually create the CafeExpense
    /// on approval are carried here instead. Unused by every other type.</summary>
    public string? PayloadJson { get; set; }
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
    TableShift, TableMerge,
}

public enum AuditResource { Order, Customer, Staff, Inventory, Menu, Invoice, Subscription, Auth, Settings, Branch, Table, Attendance, Payroll, Loan }
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

// ---------- API Failure Log ----------

/// <summary>
/// Every failed request the backend actually saw — not just 500s. Written once,
/// centrally, from GlobalExceptionHandler, so nothing has to remember to log a
/// failure itself. Deliberately NOT ITenantScoped: a failed login or a request with
/// a bad/missing token happens before any tenant is known, and this table needs to
/// capture those too. TenantId/UserId are just best-effort context pulled from the
/// JWT claims when they're present, not a hard requirement.
/// </summary>
public class ApiFailureLog
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public required string Method { get; set; }
    public required string Path { get; set; }
    public int StatusCode { get; set; }
    public required string ExceptionType { get; set; }
    public required string Reason { get; set; }
    /// <summary>Only kept for 500s — routine 400/401/404/409s don't need a stack
    /// trace, and skipping it keeps this table from ballooning on ordinary traffic.</summary>
    public string? StackTrace { get; set; }
    public int? TenantId { get; set; }
    public int? UserId { get; set; }
    public string? UserName { get; set; }
}

// ---------- Staff / Team ----------

public enum StaffStatus { Active, Suspended, OnLeave, Terminated }

public enum SalaryType { Monthly, Daily, Hourly }

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

    // ---------- HR / Payroll fields ----------
    // Free text, matching Role's existing precedent — a lookup table buys nothing at
    // single-cafe scale (no department budgets/hierarchy in scope).
    public string? Department { get; set; }
    public string? Designation { get; set; }
    public SalaryType SalaryType { get; set; } = SalaryType.Monthly;
    public decimal? BasicSalary { get; set; }
    /// <summary>Bank/Aadhaar/PAN are deliberately excluded from StaffDto (any
    /// authenticated role can list staff) — only readable via
    /// StaffController.GetFinancialDetails, which masks by default and audit-logs a
    /// full reveal.</summary>
    public string? BankAccountNumber { get; set; }
    public string? BankIfsc { get; set; }
    public string? BankName { get; set; }
    public string? Aadhaar { get; set; }
    public string? Pan { get; set; }
}

public class Shift : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int StaffId { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public string? Notes { get; set; }
    /// <summary>Set only when this shift came from a one-tap ShiftType quick-assign
    /// (StaffController.QuickAssignShift) — null for shifts built from the custom
    /// Add Shift form. Plain scalar, no navigation/FK, matching StaffId's convention.</summary>
    public int? ShiftTypeId { get; set; }
}

/// <summary>A reusable, cafe-wide named shift pattern (e.g. "Morning" 9am-1pm) — created
/// once in Team Portal, then used to one-tap assign staff to a day via
/// StaffController.QuickAssignShift instead of filling the full Add Shift form each time.</summary>
public class ShiftType : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public required string Name { get; set; }
    public TimeSpan StartTime { get; set; }
    /// <summary>Earlier than or equal to StartTime means the shift crosses midnight
    /// (e.g. a night shift 22:00-06:00) — QuickAssignShift adds a day in that case.</summary>
    public TimeSpan EndTime { get; set; }
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

    /// <summary>Set when this row came from the Daily purchase-list tab instead of the one-off
    /// Add Expense form. Saving a day replaces that day's list-sourced rows, so this column
    /// (plus SpentAt's IST date) is what lets staff re-open a day and correct an amount rather
    /// than entering it twice — see ExpensesController.SaveDailySheet.</summary>
    public int? PurchaseListItemId { get; set; }

    /// <summary>Cash / UPI / Card / Due — free text validated against ExpensesController's
    /// ValidPaymentModes, the same "HashSet, not enum" convention KhatabookController and
    /// TiffinController already use for a settle method. "Due" records that the cafe bought on
    /// udhaar and still owes the vendor; it changes nothing about the expense itself, which is
    /// booked at full amount on the day it was incurred either way.
    ///
    /// Null only on rows from before this column existed, and on Add Expense entries saved by a
    /// client too old to send one — both entry paths ask for it now. Those rows report under
    /// "Not set" rather than being counted as Cash.</summary>
    public string? PaymentMode { get; set; }
}

/// <summary>One line of a cafe's own daily purchase list — the fixed set of vendors and
/// expense heads it buys against every day (Mutton, Gas, Das Kaka, Cook Salary, ...).
///
/// Deliberately data and not more ExpenseCategory values: every cafe writes its own list and
/// edits it over time, while ExpenseCategory is a compiled enum shared by all tenants. The two
/// do different jobs — this is the detail staff pick from, ExpenseCategory stays a small fixed
/// roll-up that reports group by (see CafeExpense.Category / DefaultCategory below).
///
/// This is only the *template*. A day's actual spend lives in CafeExpense, and only for the
/// items that had an amount that day — a list of 31 with 10 filled in writes 10 rows, not 31.</summary>
public class PurchaseListItem : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public required string Name { get; set; }

    /// <summary>Position on the paper sheet this list was copied from. The Daily tab renders in
    /// this order so the screen reads the same top-to-bottom as the register staff already fill
    /// in by hand — an alphabetical or insertion order would make them hunt for every row.</summary>
    public int SortOrder { get; set; }

    /// <summary>Pre-selected on the CafeExpense rows this item generates, which is what keeps
    /// the daily entry amount-only.</summary>
    public ExpenseCategory DefaultCategory { get; set; } = ExpenseCategory.Supplies;

    /// <summary>Retired rows drop off the Daily tab but are kept, not deleted, so the
    /// CafeExpense rows they already produced still resolve back to a name.</summary>
    public bool IsActive { get; set; } = true;

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

/// <summary>
/// How long one paid term runs. Lives in the domain rather than in Contracts because the
/// Subscription row itself now records which cycle the tenant is on — SubscriptionPricing
/// and the storefront DTOs read the same enum.
/// </summary>
public enum BillingCycle { Monthly, Yearly }

public class Subscription : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public SubscriptionTier Plan { get; set; } = SubscriptionTier.FreeTrial;
    /// <summary>
    /// Whether the current term was sold by the month or by the year. Persisted rather than
    /// inferred from the Started/Expires gap, because that gap isn't reliable: an early
    /// renewal stacks a term onto the leftover days of the old one, so the dates alone can't
    /// tell a yearly plan from a monthly one. FreeTrial is always Monthly — the trial isn't
    /// sold on a cycle, and its length comes from SubscriptionPricing.TrialDays instead.
    /// </summary>
    public BillingCycle Cycle { get; set; } = BillingCycle.Monthly;
    /// <summary>
    /// When the CURRENT term began — "now" for a manual grant or a lapsed renewal, and the
    /// OLD expiry for an early renewal (which starts where the paid-for days ran out, not
    /// today). Null only on rows that predate this column.
    /// </summary>
    public DateTime? PlanStartedAt { get; set; }
    /// <summary>
    /// When the CURRENT plan cycle ends — FreeTrial gets 14 days, a paid tier gets 1 month
    /// or 1 year depending on Cycle, reset on every /subscription/change-plan call (including
    /// re-selecting the same plan, which is the stand-in for "renew" when a platform admin
    /// applies an out-of-band payment by hand). Checked by SubscriptionExpiryMiddleware to
    /// lock the tenant out once it passes.
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
