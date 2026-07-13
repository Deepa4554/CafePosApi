namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Mirrors the RN app's src/core/auth/permissions.ts exactly, so the API enforces
/// the same rules the client already hides screens for (defense in depth).
/// </summary>
public static class Policies
{
    public const string OwnerOnly = "OwnerOnly";
    public const string OwnerOrManager = "OwnerOrManager";
    /// <summary>Everyone except Waiter — matches WAITER_HIDDEN_ROUTES for read access (Inventory, CRM, Dashboard, ...).</summary>
    public const string NotWaiter = "NotWaiter";
    /// <summary>The platform operator only — NOT satisfied by AppRole.Owner. See AppUser.IsPlatformAdmin.</summary>
    public const string PlatformAdminOnly = "PlatformAdminOnly";
    /// <summary>Tenant's subscription must be Plus or Premium — see RequirePlanHandler.</summary>
    public const string RequirePlus = "RequirePlus";
    /// <summary>Tenant's subscription must be Premium — see RequirePlanHandler.</summary>
    public const string RequirePremium = "RequirePremium";
}
