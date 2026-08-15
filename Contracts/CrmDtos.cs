using CafePOS.Api.Domain;

namespace CafePOS.Api.Contracts;

public record CustomerSummaryDto(
    int Id, string Name, string? Email, string? Phone, string? ProfilePhotoUrl,
    int VisitCount, int VisitsLast30Days, int TotalPoints, int AvailablePoints, string Tier, decimal TotalSpent, DateTime LastVisitAt, DateTime JoinedAt)
{
    /// <summary>VisitCount is lifetime and never resets; VisitsLast30Days is computed
    /// live from real Order rows each call — pass 0 for a brand-new customer with no
    /// orders yet.</summary>
    public static CustomerSummaryDto From(Customer c, int visitsLast30Days = 0) => new(
        c.Id, c.Name, c.Email, c.Phone, c.ProfilePhotoUrl,
        c.VisitCount, visitsLast30Days, c.TotalPoints, c.AvailablePoints, TierFor(c.TotalPoints).ToString().ToUpperInvariant(), c.TotalSpent, c.LastVisitAt, c.JoinedAt);

    public static MembershipTier TierFor(int totalPoints) => totalPoints switch
    {
        >= 2000 => MembershipTier.Platinum,
        >= 1000 => MembershipTier.Gold,
        >= 300 => MembershipTier.Silver,
        _ => MembershipTier.Bronze,
    };
}

public record VisitDto(int OrderId, string OrderNumber, DateTime Date, decimal OrderTotal, int PointsEarned, List<string> Items);

public record CustomerDetailDto(
    CustomerSummaryDto Summary,
    string? AddressLine1, string? AddressCity, string? AddressPincode, DateOnly? DateOfBirth, string? Notes,
    string ReferralCode, int TotalReferrals, int SuccessfulReferrals, decimal ReferralEarned,
    List<VisitDto> RecentVisits,
    List<CouponDto> Coupons,
    List<GiftCardDto> GiftCards,
    List<FavoriteItemDto> FavoriteItems);

public record CustomerLookupDto(bool Exists, int? Id, string? Name);

public record CreateCustomerRequest(string Name, string? Email, string? Phone, string? DateOfBirth);
public record UpdateCustomerRequest(string? Name, string? Email, string? Phone, string? AddressLine1, string? AddressCity, string? AddressPincode, string? Notes);
public record AdjustPointsRequest(int Points, string? Reason);

public record CouponDto(int Id, string Code, string Title, string Description, string Type, decimal Value, decimal MinOrderValue, DateTime ExpiresAt, bool IsUsed)
{
    public static CouponDto From(Coupon c) => new(c.Id, c.Code, c.Title, c.Description, c.Type.ToString().ToUpperInvariant(), c.Value, c.MinOrderValue, c.ExpiresAt, c.IsUsed);
}
public record IssueCouponRequest(string Title, string Description, CouponType Type, decimal Value, decimal MinOrderValue, DateTime ExpiresAt, int? CustomerId);
public record ApplyCouponRequest(string Code, decimal OrderSubtotal);
public record ApplyCouponResult(bool Valid, string? Error, decimal DiscountAmount);

public record GiftCardDto(int Id, string Code, decimal Balance, decimal OriginalBalance, string Status, DateTime PurchasedAt, DateTime ExpiresAt)
{
    public static GiftCardDto From(GiftCard g) => new(g.Id, g.Code, g.Balance, g.OriginalBalance, g.Status.ToString().ToUpperInvariant(), g.PurchasedAt, g.ExpiresAt);
}
public record IssueGiftCardRequest(decimal Amount, int? CustomerId, string? PurchasedBy, int ValidDays = 365);
public record RedeemGiftCardRequest(string Code, decimal Amount);
/// <summary>Validate-only check — mirrors ApplyCouponRequest/Result. No balance is
/// touched here; the actual debit happens atomically at order-creation time in
/// OrdersController.BuildOrderAsync, same pattern as coupon's IsUsed flip.</summary>
public record CheckGiftCardRequest(string Code);
public record CheckGiftCardResult(bool Valid, string? Error, decimal Balance);

public record FavoriteItemDto(int MenuItemId, string Name, decimal Price, int OrderCount);

// ---------- Reward Catalog (cafe-owned, replaces the old hardcoded 4-item list) ----------

public record RewardDto(int Id, string Name, int PointsCost, string Icon, bool IsActive)
{
    public static RewardDto From(Reward r) => new(r.Id, r.Name, r.PointsCost, r.Icon, r.IsActive);
}
public record CreateRewardRequest(string Name, int PointsCost, string? Icon);
public record UpdateRewardRequest(string? Name, int? PointsCost, string? Icon, bool? IsActive);

// ---------- CRM Insights (real customer analytics — replaces the old hardcoded screen) ----------

public record CrmGrowthPointDto(string Day, int NewCustomers, int ReturningCustomers);
public record CrmRedemptionDto(string Title, int Issued, int Redeemed, int Pct);
public record CrmSegmentDto(string Name, string Description, int CustomerCount, decimal AvgSpent, List<string> Tags);

/// <summary>Suggestions are rule-matched against real customer/order/coupon data (see
/// CustomersController.BuildSuggestions) — no LLM involved. Up to 2, led by the single
/// highest-priority real signal; empty if there's no customer data yet.</summary>
public record CrmInsightsDto(
    double RetentionRatePct,
    decimal AvgLifetimeValue,
    List<CrmGrowthPointDto> Growth,
    List<CrmRedemptionDto> Redemption,
    List<CrmSegmentDto> Segments,
    List<string> Suggestions);
