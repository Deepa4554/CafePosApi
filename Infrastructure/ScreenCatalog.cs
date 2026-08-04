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
        ["AutoChargesSettings"] = PlanCategory.Normal,
        ["ReceiptBuilder"] = PlanCategory.Normal,
        ["CafeProfileDetail"] = PlanCategory.Normal,
        ["Help"] = PlanCategory.Normal,
        ["HelpArticle"] = PlanCategory.Normal,
        ["SupportTicket"] = PlanCategory.Normal,
    };

    public static bool IsValidKey(string key) => MinPlan.ContainsKey(key);

    /// <summary>True if `key` exists and the tenant's current plan meets its minimum —
    /// i.e. it's actually assignable right now, not just a recognized key.</summary>
    public static bool IsAssignableAt(string key, PlanCategory tenantPlan) =>
        MinPlan.TryGetValue(key, out var min) && tenantPlan >= min;
}
