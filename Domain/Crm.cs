namespace CafePOS.Api.Domain;

public enum MembershipTier
{
    Bronze,
    Silver,
    Gold,
    Platinum,
}

public enum CouponType
{
    Percent,
    Flat,
    Bogo,
    Birthday,
    Referral,
}

public enum GiftCardStatus
{
    Active,
    Used,
    Expired,
}

public class Customer : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public required string Name { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? ProfilePhotoUrl { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressCity { get; set; }
    public string? AddressPincode { get; set; }
    public string? Notes { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastVisitAt { get; set; } = DateTime.UtcNow;

    // Loyalty — ₹1 spent = 1 point earned (matches the RN app's recordVisit rule).
    public int TotalPoints { get; set; }
    public int RedeemedPoints { get; set; }
    public int AvailablePoints => TotalPoints - RedeemedPoints;
    public int VisitCount { get; set; }
    public decimal TotalSpent { get; set; }

    public string ReferralCode { get; set; } = "";
    public int TotalReferrals { get; set; }
    public int SuccessfulReferrals { get; set; }
    public decimal ReferralEarned { get; set; }

    /// <summary>Highest LoyaltyMilestone.ThresholdPoints already claimed as a bill-time
    /// discount (see OrdersController.ApplyBillMilestone) — checked against TotalPoints, not
    /// AvailablePoints, so redeeming points later can't undo a milestone already reached. A
    /// milestone only fires again once TotalPoints crosses one HIGHER than this.</summary>
    public int MilestoneClaimedThreshold { get; set; }

    public List<Coupon> Coupons { get; set; } = [];
    public List<GiftCard> GiftCards { get; set; } = [];
    public List<FavoriteItem> FavoriteItems { get; set; } = [];
    public List<Order> Orders { get; set; } = [];
}

public class Coupon : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>Null = a promotional coupon available to any customer (e.g. from a Campaign).</summary>
    public int? CustomerId { get; set; }
    public required string Code { get; set; }
    public required string Title { get; set; }
    public string Description { get; set; } = "";
    public CouponType Type { get; set; }
    public decimal Value { get; set; }
    public decimal MinOrderValue { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsUsed { get; set; }
}

public class GiftCard : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int? CustomerId { get; set; }
    public required string Code { get; set; }
    public decimal Balance { get; set; }
    public decimal OriginalBalance { get; set; }
    public GiftCardStatus Status { get; set; } = GiftCardStatus.Active;
    public DateTime PurchasedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public string? PurchasedBy { get; set; }
}

/// <summary>A cafe-defined item its own loyalty points can be redeemed for (the Points
/// screen's "Reward Catalog") — was a hardcoded 4-item constant before, now owned per-tenant
/// like Menu items/Coupons/everything else a cafe customizes.</summary>
public class Reward : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public required string Name { get; set; }
    public int PointsCost { get; set; }
    /// <summary>MaterialCommunityIcons name — matches the icon-driven card style already
    /// used for the reward grid, no image upload needed for something this small.</summary>
    public string Icon { get; set; } = "gift-outline";
    /// <summary>Soft-hide instead of delete, so a reward tied to loyalty history/old
    /// receipts isn't yanked out from under past redemptions.</summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>A cafe-defined lifetime-points milestone — an Owner picks a ThresholdPoints and a
/// DiscountPct; once a customer's Customer.TotalPoints crosses it for the first time, their
/// very next bill can claim DiscountPct off (see OrdersController.ApplyBillMilestone), then
/// it's marked claimed (Customer.MilestoneClaimedThreshold) so it fires exactly once per
/// milestone rather than on every order past the threshold.</summary>
public class LoyaltyMilestone : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int ThresholdPoints { get; set; }
    public decimal DiscountPct { get; set; }
    /// <summary>Soft-hide instead of delete, matching Reward — a past order's
    /// MilestoneThresholdApplied stays meaningful even after the tier is retired.</summary>
    public bool IsActive { get; set; } = true;
}

public class FavoriteItem : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int CustomerId { get; set; }
    /// <summary>Set this (not CustomerId directly) when linking a brand-new, not-yet-saved Customer — see Order.Customer for why.</summary>
    public Customer? Customer { get; set; }
    public int MenuItemId { get; set; }
    public int OrderCount { get; set; }
}
