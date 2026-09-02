using CafePOS.Api.Domain;

namespace CafePOS.Api.Infrastructure;

/// <summary>Decides whether a bill carries tax at all, given how it's being settled — the one
/// place the "GST only on UPI" style of billing is expressed, so OrdersController (which
/// applies it) and SettingsController (which validates the configuration) can't drift apart.
///
/// Off by default and per-tenant: while CafeSettings.TaxByPaymentModeEnabled is false this
/// always answers "yes, taxed", which is exactly how every bill behaved before the setting
/// existed. It is deliberately opt-in rather than a default, because charging tax based on the
/// customer's choice of tender is not what GST law says — the tax is due on the supply. A cafe
/// switching it on is adopting a billing policy of its own; the app only stops assuming
/// otherwise.
///
/// ONE BILL, ONE ANSWER. A split settle (₹100 cash + ₹50 UPI) can't be half-taxed: tax sits on
/// each LINE at that line's own slab (OrderItem.TaxRatePct), so apportioning it across tenders
/// would mean splitting every slab of every line by tender ratio — arithmetic no bill could
/// print legibly and no report could add back up. So the rule is: if ANY tender on the order is
/// a taxable one, the whole bill is taxed. That also makes the answer monotonic across a
/// partial settle that gets topped up later — a Cash advance followed by a UPI leg turns tax ON
/// (the balance still to collect grows), and nothing that arrives later can take it back off
/// money already collected.</summary>
public static class PaymentModeTax
{
    /// <summary>Whether tax applies to a bill settled with `tenders` — every payment method on
    /// the order, including any already recorded by an earlier partial settle. An empty list
    /// means the tender isn't known yet (nothing has been collected), which answers "taxed":
    /// an order sitting open on a table shows and prints its tax like it always did, and only
    /// the settle itself can take it off.</summary>
    public static bool AppliesTo(CafeSettings settings, IEnumerable<string?> tenders)
    {
        if (!settings.TaxByPaymentModeEnabled) return true;
        var taxable = Parse(settings.TaxablePaymentModes);
        if (taxable.Count == 0) return false;
        var named = tenders.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (named.Count == 0) return true;
        return named.Any(t => taxable.Contains(t!.Trim()));
    }

    /// <summary>Reads the stored comma-separated list. Case-insensitive on purpose — the same
    /// reason OrdersController.CanonicalMethod exists: a client sending "upi" must not land in
    /// a different bucket from one sending "UPI".</summary>
    public static HashSet<string> Parse(string? csv) =>
        new((csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Canonicalises a submitted list against the tender catalog and returns it in the
    /// catalog's own casing and order, so what's stored is always something Parse can match and
    /// the settings screen can tick back. Throws on a tender the POS doesn't offer rather than
    /// storing a name that would silently never match anything.</summary>
    public static string Normalize(IEnumerable<string> modes, IReadOnlyCollection<string> catalog)
    {
        var requested = new HashSet<string>(modes.Select(m => m.Trim()), StringComparer.OrdinalIgnoreCase);
        var unknown = requested.FirstOrDefault(m => !catalog.Contains(m, StringComparer.OrdinalIgnoreCase));
        if (unknown is not null)
            throw new ApiValidationException($"\"{unknown}\" isn't a payment method this POS takes — use {string.Join(", ", catalog)}.");
        return string.Join(",", catalog.Where(c => requested.Contains(c)));
    }
}
