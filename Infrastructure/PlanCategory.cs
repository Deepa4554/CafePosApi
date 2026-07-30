using CafePOS.Api.Domain;

namespace CafePOS.Api.Infrastructure;

/// <summary>Mirrors src/core/plan/planCategory.ts on the frontend exactly — the
/// frontend hides screens below a tier as UX; this is what actually enforces it.</summary>
public enum PlanCategory { Normal, Plus, Premium }

public static class PlanCategoryMapper
{
    private static readonly Dictionary<SubscriptionTier, PlanCategory> TierToCategory = new()
    {
        // The 7-day free trial defaults to Plus (not the full Premium/Integrations
        // tier) — enough for a new cafe to explore CRM/Dashboard/Staff/Inventory plus
        // WhatsApp Business, but the Integrations hub itself stays a paid-only Premium
        // feature even during the trial.
        [SubscriptionTier.FreeTrial] = PlanCategory.Plus,
        [SubscriptionTier.Starter] = PlanCategory.Normal,
        [SubscriptionTier.Professional] = PlanCategory.Plus,
        [SubscriptionTier.Enterprise] = PlanCategory.Premium,
    };

    public static PlanCategory ToCategory(this SubscriptionTier tier) =>
        TierToCategory.GetValueOrDefault(tier, PlanCategory.Normal);
}
