using CafePOS.Api.Domain;

namespace CafePOS.Api.Contracts;

public record TenantSummaryDto(
    int Id, string Name, string Slug, string Status, DateTime CreatedAt,
    string Plan, DateTime? PlanExpiresAt, int StaffCount, int BranchCount)
{
    public static TenantSummaryDto From(Tenant t, Subscription? sub, int staffCount, int branchCount) => new(
        t.Id, t.Name, t.Slug, t.Status.ToString().ToUpperInvariant(), t.CreatedAt,
        sub?.Plan.ToString().ToUpperInvariant() ?? "NONE", sub?.PlanExpiresAt, staffCount, branchCount);
}

public record AdminChangePlanRequest(SubscriptionTier Plan);

public record DailySalesDto(string Date, decimal Revenue, int OrderCount);
public record MonthlySalesDto(string Month, decimal Revenue, int OrderCount);

public record TenantSalesDto(
    int TenantId,
    string TenantName,
    decimal TodayRevenue,
    decimal ThisMonthRevenue,
    decimal AllTimeRevenue,
    int AllTimeOrderCount,
    List<DailySalesDto> Daily,
    List<MonthlySalesDto> Monthly);

// ---------- Platform Expenses ----------

public record PlatformExpenseDto(int Id, decimal Amount, string SpentBy, string Purpose, DateTime SpentAt, string RecordedByName, DateTime CreatedAt)
{
    public static PlatformExpenseDto From(PlatformExpense e) => new(e.Id, e.Amount, e.SpentBy, e.Purpose, e.SpentAt, e.RecordedByName, e.CreatedAt);
}

public record CreatePlatformExpenseRequest(decimal Amount, string SpentBy, string Purpose, DateTime? SpentAt);

public record PlatformExpenseSummaryDto(decimal TotalAllTime, decimal TotalThisMonth, List<PlatformExpenseDto> Recent);

// ---------- API Failure Log ----------

public record ApiFailureLogDto(
    int Id, DateTime Timestamp, string Method, string Path, int StatusCode,
    string ExceptionType, string Reason, int? TenantId, int? UserId, string? UserName)
{
    public static ApiFailureLogDto From(ApiFailureLog f) => new(
        f.Id, f.Timestamp, f.Method, f.Path, f.StatusCode, f.ExceptionType, f.Reason, f.TenantId, f.UserId, f.UserName);
}
