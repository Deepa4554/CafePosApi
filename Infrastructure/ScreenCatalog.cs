namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Every screen an Owner can grant/revoke via per-staff Custom screen access. Mirrors
/// the RN app's src/core/auth/screenCatalog.ts key-for-key and plan-for-plan — this is
/// the server-side half of that source of truth, used to validate AppUser.AllowedScreens
/// on write (StaffController.UpdateScreenAccess) so a Basic-plan tenant can never persist
/// a Plus/Premium screen key into a staff member's allow-list, no matter what the client
/// sends. SuperAdmin is deliberately absent: it's gated by AppUser.IsPlatformAdmin, not
/// role or screen access, and is never staff-assignable.
/// </summary>
public static class ScreenCatalog
{
    public static readonly IReadOnlyDictionary<string, PlanCategory> MinPlan = new Dictionary<string, PlanCategory>
    {
        ["POS"] = PlanCategory.Normal,
        ["Tables"] = PlanCategory.Normal,
        ["KDS"] = PlanCategory.Normal,
        ["AI"] = PlanCategory.Plus,
        ["TokenDashboard"] = PlanCategory.Normal,
        ["TakeawayDelivery"] = PlanCategory.Normal,
        ["QRMenu"] = PlanCategory.Normal,
        ["Billing"] = PlanCategory.Normal,
        ["CRM"] = PlanCategory.Plus,
        // Normal, deliberately below the CRM screens it shares a Customer record with —
        // running a khata for regulars is what the smallest shops do most, not least.
        ["Khatabook"] = PlanCategory.Normal,
        // Normal, like the khata it sits beside — a tiffin/thali round is exactly the standing
        // credit-and-plate business the smallest shops run, not a premium add-on.
        ["Tiffin"] = PlanCategory.Normal,
        ["TeamPortal"] = PlanCategory.Normal,
        ["Attendance"] = PlanCategory.Plus,
        ["Leave"] = PlanCategory.Plus,
        ["Payroll"] = PlanCategory.Plus,
        ["Loans"] = PlanCategory.Plus,
        ["HRReports"] = PlanCategory.Plus,
        ["Menu"] = PlanCategory.Normal,
        ["RecipeBuilder"] = PlanCategory.Normal,
        ["Inventory"] = PlanCategory.Plus,
        ["InventoryLedger"] = PlanCategory.Plus,
        ["PurchaseOrders"] = PlanCategory.Plus,
        ["Vendors"] = PlanCategory.Plus,
        ["StockTakes"] = PlanCategory.Plus,
        ["VarianceReport"] = PlanCategory.Plus,
        ["FoodCostReport"] = PlanCategory.Plus,
        ["ExpiringBatches"] = PlanCategory.Plus,
        ["Dashboard"] = PlanCategory.Normal,
        ["AIChat"] = PlanCategory.Plus,
        ["Notifications"] = PlanCategory.Normal,
        ["Approvals"] = PlanCategory.Plus,
        ["Tasks"] = PlanCategory.Plus,
        ["Integrations"] = PlanCategory.Premium,
        // Plus, unlike the Integrations hub above it — see the matching note in
        // CafePOS/src/core/auth/screenCatalog.ts.
        ["WhatsAppSetup"] = PlanCategory.Plus,
        ["DeliveryPartner"] = PlanCategory.Plus,
        ["Branches"] = PlanCategory.Plus,
        ["Expenses"] = PlanCategory.Normal,
        ["Reports"] = PlanCategory.Normal,
        ["StockReport"] = PlanCategory.Plus,
        ["PurchaseReport"] = PlanCategory.Plus,
        ["RevenueReport"] = PlanCategory.Normal,
        ["ProfitReport"] = PlanCategory.Plus,
        ["SalesReport"] = PlanCategory.Plus,
        ["TaxGstReport"] = PlanCategory.Plus,
        ["ExpenseReport"] = PlanCategory.Plus,
        ["CrmReport"] = PlanCategory.Plus,
        ["OrderDetailReport"] = PlanCategory.Plus,
        ["SaaS"] = PlanCategory.Normal,
        ["Profile"] = PlanCategory.Normal,
        ["PrinterSettings"] = PlanCategory.Normal,
        ["KitchenFlowSettings"] = PlanCategory.Normal,
        ["StationManagement"] = PlanCategory.Normal,
        ["TaxSlabManagement"] = PlanCategory.Normal,
        ["OrderTypesSettings"] = PlanCategory.Normal,
        ["ShiftSettings"] = PlanCategory.Normal,
        ["AutoChargesSettings"] = PlanCategory.Normal,
        ["Offers"] = PlanCategory.Normal,
        ["ReceiptBuilder"] = PlanCategory.Normal,
        ["CafeProfileDetail"] = PlanCategory.Normal,
        ["Help"] = PlanCategory.Normal,
        ["HelpArticle"] = PlanCategory.Normal,
        ["SupportTicket"] = PlanCategory.Normal,
    };

    /// <summary>Which screen a drill-down key is normally reached from — mirrors the RN app's
    /// screenCatalog.ts `parent` field key-for-key. A key absent here is a top-level screen.
    /// Used to validate that an allow-list (tenant-level or per-staff) never contains a child
    /// whose parent isn't also present — see Normalize below.</summary>
    public static readonly IReadOnlyDictionary<string, string> Parent = new Dictionary<string, string>
    {
        ["Attendance"] = "TeamPortal",
        ["Leave"] = "TeamPortal",
        ["Payroll"] = "TeamPortal",
        ["Loans"] = "TeamPortal",
        ["HRReports"] = "Reports",
        ["RecipeBuilder"] = "Menu",
        ["InventoryLedger"] = "Inventory",
        ["PurchaseOrders"] = "Inventory",
        ["Vendors"] = "Inventory",
        ["StockTakes"] = "Inventory",
        ["VarianceReport"] = "Reports",
        ["FoodCostReport"] = "Reports",
        ["ExpiringBatches"] = "Inventory",
        ["AIChat"] = "Dashboard",
        ["StockReport"] = "Reports",
        ["PurchaseReport"] = "Reports",
        ["RevenueReport"] = "Reports",
        ["ProfitReport"] = "Reports",
        ["SalesReport"] = "Reports",
        ["TaxGstReport"] = "Reports",
        ["ExpenseReport"] = "Reports",
        ["CrmReport"] = "Reports",
        ["OrderDetailReport"] = "Reports",
        ["WhatsAppSetup"] = "Integrations",
        ["DeliveryPartner"] = "Integrations",
        ["PrinterSettings"] = "Profile",
        ["KitchenFlowSettings"] = "Profile",
        ["StationManagement"] = "Profile",
        ["TaxSlabManagement"] = "Profile",
        ["OrderTypesSettings"] = "Profile",
        ["ShiftSettings"] = "Profile",
        ["AutoChargesSettings"] = "Profile",
        ["Offers"] = "Profile",
        ["ReceiptBuilder"] = "Profile",
        ["CafeProfileDetail"] = "Profile",
        ["HelpArticle"] = "Help",
        ["SupportTicket"] = "Help",
    };

    public static bool IsValidKey(string key) => MinPlan.ContainsKey(key);

    /// <summary>True if `key` exists and the tenant's current plan meets its minimum —
    /// i.e. it's actually assignable right now, not just a recognized key.</summary>
    public static bool IsAssignableAt(string key, PlanCategory tenantPlan) =>
        MinPlan.TryGetValue(key, out var min) && tenantPlan >= min;

    /// <summary>Walks `key`'s parent chain up to the top-level screen, nearest first. Empty
    /// for a key with no parent.</summary>
    public static IEnumerable<string> AncestorsOf(string key)
    {
        var current = Parent.GetValueOrDefault(key);
        while (current is not null)
        {
            yield return current;
            current = Parent.GetValueOrDefault(current);
        }
    }

    /// <summary>Drops any key from `requested` whose parent chain isn't entirely present in
    /// the same set — e.g. requesting PurchaseOrders without Inventory silently drops
    /// PurchaseOrders rather than leaving a child reachable with its parent switched off.
    /// Applied to both Tenant.EnabledScreens and AppUser.AllowedScreens on every write (see
    /// SuperAdminController.UpdateTenantScreenAccess and StaffController.UpdateScreenAccess)
    /// so stored data can never disagree with the cascade rule canAccessRoute/the client's
    /// ScreenAccessTree enforce at render time.</summary>
    public static List<string> Normalize(IEnumerable<string> requested)
    {
        var set = requested.ToHashSet();
        return set.Where(k => AncestorsOf(k).All(set.Contains)).ToList();
    }
}
