using System.Text.Json;
using CafePOS.Api.Domain;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Central answer to "is NotificationCategory X enabled for this tenant?", covering two tiers:
///
/// 1. <see cref="NamedGates"/> — categories that already have their own dedicated CafeSettings
///    boolean column and a matching UpdateSettingsRequest field / Settings-screen toggle
///    (OrderPlaced, OrderPendingConfirmation, Order, Inventory, Staff, Approval). Adding a
///    category here still costs a migration + a settings field, same as always — that's the
///    deliberate price of giving it its own named, discoverable toggle in the Settings UI
///    instead of a generic list entry.
///
/// 2. Everything else — read from CafeSettings.NotificationCategoryOverridesJson, one JSON
///    dict column (category name -> bool) shared by every category NOT in NamedGates. A brand
///    new NotificationCategory therefore gets a working per-tenant enable/disable toggle,
///    defaulting to enabled, the moment its first AppNotification is created (see IsEnabled,
///    called from CafePosDbContext.SuppressDisabledNotificationsAsync) — no migration and no
///    code change here at all. Owners manage it through SettingsController's GET/PUT
///    notification-preferences endpoints, which enumerate "every category not in NamedGates"
///    from the enum itself rather than a hardcoded list, so a new category shows up there
///    automatically too.
///
/// This is why the fix isn't "make everything generic" (that would mean re-plumbing six
/// already-shipped Settings-screen toggles for no behavioural gain) — it's "make the default
/// path for anything not already promoted to a named toggle configurable without a deploy."
/// </summary>
public static class NotificationPreferences
{
    public static readonly IReadOnlyDictionary<NotificationCategory, Func<CafeSettings, bool>> NamedGates = new Dictionary<NotificationCategory, Func<CafeSettings, bool>>
    {
        [NotificationCategory.OrderPlaced] = s => s.OrderPlacedAlertsEnabled,
        [NotificationCategory.OrderPendingConfirmation] = s => s.OrderPendingConfirmationAlertsEnabled,
        [NotificationCategory.Order] = s => s.OrderReadyAlertsEnabled,
        [NotificationCategory.Inventory] = s => s.InventoryAlertsEnabled,
        [NotificationCategory.Staff] = s => s.ShiftReportsEnabled,
        [NotificationCategory.Approval] = s => s.ApprovalAlertsEnabled,
    };

    public static bool IsEnabled(CafeSettings settings, NotificationCategory category) =>
        NamedGates.TryGetValue(category, out var gate) ? gate(settings) : GetOverride(settings, category) ?? true;

    /// <summary>Null = tenant has never touched this category's toggle (defaults enabled, see
    /// IsEnabled) — distinct from "explicitly re-enabled" so a fresh tenant needs zero JSON
    /// entries for the common case of every generic category left at its default.</summary>
    public static bool? GetOverride(CafeSettings settings, NotificationCategory category) =>
        ReadOverrides(settings).TryGetValue(category.ToString(), out var enabled) ? enabled : null;

    /// <summary>Throws for a NamedGates category rather than silently no-op-ing: a caller
    /// reaching this instead of the dedicated settings field almost certainly doesn't know
    /// that field exists, and letting both paths write would leave two sources of truth for
    /// the same toggle.</summary>
    public static void SetOverride(CafeSettings settings, NotificationCategory category, bool enabled)
    {
        if (NamedGates.ContainsKey(category))
            throw new InvalidOperationException($"{category} has a dedicated settings field — update that instead of the generic override map.");

        var overrides = ReadOverrides(settings);
        overrides[category.ToString()] = enabled;
        settings.NotificationCategoryOverridesJson = JsonSerializer.Serialize(overrides);
    }

    /// <summary>Every category NOT in NamedGates, with its currently effective enabled state —
    /// what SettingsController's GET exposes so the Settings UI can list "everything still
    /// toggled generically" by walking the enum, not a list duplicated on both sides.</summary>
    public static IReadOnlyDictionary<NotificationCategory, bool> GenericCategories(CafeSettings settings)
    {
        var overrides = ReadOverrides(settings);
        return Enum.GetValues<NotificationCategory>()
            .Where(c => !NamedGates.ContainsKey(c))
            .ToDictionary(c => c, c => overrides.TryGetValue(c.ToString(), out var enabled) ? enabled : true);
    }

    private static Dictionary<string, bool> ReadOverrides(CafeSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.NotificationCategoryOverridesJson)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, bool>>(settings.NotificationCategoryOverridesJson) ?? [];
        }
        catch (JsonException)
        {
            // Malformed JSON must never take down SaveChangesAsync, which checks this on
            // every single notification save — fail open to "no overrides" instead.
            return [];
        }
    }
}
