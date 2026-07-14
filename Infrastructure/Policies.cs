namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Mirrors the RN app's src/core/auth/permissions.ts exactly, so the API enforces
/// the same rules the client already hides screens for (defense in depth).
/// </summary>
public static class Policies
{
    public const string OwnerOnly = "OwnerOnly";
    public const string OwnerOrManager = "OwnerOrManager";
    /// <summary>Everyone except Waiter/Chef/KitchenStaff — matches FLOOR_STAFF_HIDDEN_ROUTES
    /// for read access (Inventory, CRM, Dashboard, ...). Chef/KitchenStaff are treated
    /// identically to Waiter everywhere in the app, not just here.</summary>
    public const string CanReadInventory = "CanReadInventory";
    /// <summary>The platform operator only — NOT satisfied by AppRole.Owner. See AppUser.IsPlatformAdmin.</summary>
    public const string PlatformAdminOnly = "PlatformAdminOnly";
    /// <summary>Tenant's subscription must be Plus or Premium — see RequirePlanHandler.</summary>
    public const string RequirePlus = "RequirePlus";
    /// <summary>Tenant's subscription must be Premium — see RequirePlanHandler.</summary>
    public const string RequirePremium = "RequirePremium";
}
