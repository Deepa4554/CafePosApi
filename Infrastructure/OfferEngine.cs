using CafePOS.Api.Domain;

namespace CafePOS.Api.Infrastructure;

/// <summary>One cart line as the engine sees it. Deliberately not an OrderItem: the POS needs
/// to price offers for a cart that has no order row yet (the live banner while the cashier is
/// still adding items), and the server needs it for a saved order — both map into this, so both
/// get identical numbers. <paramref name="LineKey"/> is whatever the caller can map back:
/// OrderItem.Id server-side, the cart index in the POS preview.</summary>
public sealed record OfferCartLine(
    int LineKey,
    int MenuItemId,
    string? CategoryName,
    string Name,
    decimal UnitPrice,
    int Qty)
{
    public decimal LineTotal => UnitPrice * Qty;
}

/// <summary>An offer that actually took money off, with the per-line split it took it from.
/// The split is the point: attributing an item-scoped discount proportionally across the whole
/// bill would mis-state taxable value the moment a cafe bills two GST rates (5% food, 18%
/// packaged drinks), so the caller reduces exactly these lines.</summary>
public sealed record AppliedOffer(
    int OfferId,
    string Title,
    decimal DiscountAmount,
    string Detail,
    IReadOnlyDictionary<int, decimal> PerLineDiscount);

/// <summary>An offer the bill is close to unlocking. Surfacing these is an upsell, not a
/// courtesy — "add one more to get one free" is the cheapest extra line item a POS can
/// suggest, and the cashier never has to remember the offer exists.</summary>
public sealed record OfferNearMiss(int OfferId, string Title, string Nudge);

public sealed record OfferEvaluation(
    IReadOnlyList<AppliedOffer> Applied,
    decimal TotalDiscount,
    IReadOnlyDictionary<int, decimal> PerLineDiscount,
    IReadOnlyList<OfferNearMiss> NearMisses)
{
    public static OfferEvaluation Empty { get; } =
        new([], 0m, new Dictionary<int, decimal>(), []);
}

/// <summary>Prices every active <see cref="Offer"/> against a cart. Pure by design — no DbContext,
/// no DateTime.UtcNow — so the POS preview, order creation and any recompute all run the same
/// function and can never disagree about what a bill should cost, and so the money rules can be
/// exercised without a database.
///
/// The engine only ever produces a rupee figure plus the lines it came off. It does not touch
/// tax: the caller adds the total to the order's discount pool, which already drives the
/// per-line taxable split in RecomputeTotals.</summary>
public static class OfferEngine
{
    /// <summary>How close to the minimum a bill must be before nudging. Below this the
    /// suggestion is noise — nobody adds ₹400 of food because a banner asked.</summary>
    private const decimal NearMissFloor = 0.6m;

    public static OfferEvaluation Evaluate(
        IReadOnlyList<OfferCartLine> lines,
        IReadOnlyList<Offer> offers,
        DateTime nowUtc)
    {
        if (lines.Count == 0 || offers.Count == 0) return OfferEvaluation.Empty;

        var subtotal = lines.Sum(l => l.LineTotal);
        if (subtotal <= 0) return OfferEvaluation.Empty;

        var nowIst = IstClock.ToIst(nowUtc);
        var live = offers.Where(o => IsLive(o, nowUtc, nowIst)).ToList();

        // Price every live offer independently, then decide which survive together. Evaluating
        // first is what lets "best non-stackable wins" pick by actual rupees rather than by the
        // owner's ordering.
        var priced = new List<(Offer Offer, AppliedOffer Result)>();
        foreach (var offer in live)
        {
            if (subtotal < offer.MinOrderValue) continue;
            var result = Price(offer, lines);
            if (result is not null) priced.Add((offer, result));
        }

        var chosen = new List<AppliedOffer>();
        chosen.AddRange(priced.Where(p => p.Offer.Stackable).Select(p => p.Result));

        var bestExclusive = priced
            .Where(p => !p.Offer.Stackable)
            .OrderByDescending(p => p.Result.DiscountAmount)
            .ThenBy(p => p.Offer.Id)
            .Select(p => p.Result)
            .FirstOrDefault();
        if (bestExclusive is not null) chosen.Add(bestExclusive);

        var (applied, perLine, total) = Merge(chosen, lines);
        return new OfferEvaluation(applied, total, perLine, NearMisses(live, lines, subtotal));
    }

    // ---------- Eligibility ----------

    private static bool IsLive(Offer o, DateTime nowUtc, DateTime nowIst)
    {
        if (!o.IsActive || !o.AutoApply) return false;
        if (o.StartsAtUtc is { } from && nowUtc < from) return false;
        if (o.EndsAtUtc is { } to && nowUtc > to) return false;
        return MatchesDay(o.DaysOfWeek, nowIst)
            && MatchesTime(o.StartTime, o.EndTime, TimeOnly.FromDateTime(nowIst));
    }

    private static bool MatchesDay(string? csv, DateTime nowIst)
    {
        if (string.IsNullOrWhiteSpace(csv)) return true;
        var today = (int)nowIst.DayOfWeek;
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out var day) && day == today) return true;
        return false;
    }

    /// <summary>A window whose end sits before its start is read as crossing midnight, so a
    /// 22:00–02:00 late-night offer is expressible without two rows.</summary>
    private static bool MatchesTime(TimeOnly? start, TimeOnly? end, TimeOnly now)
    {
        if (start is not { } from || end is not { } to || from == to) return true;
        return from < to ? now >= from && now <= to : now >= from || now <= to;
    }

    private static List<OfferCartLine> Qualifying(Offer o, IReadOnlyList<OfferCartLine> lines)
    {
        switch (o.Scope)
        {
            case OfferScope.EntireBill:
                return [.. lines];
            case OfferScope.Category:
                return string.IsNullOrEmpty(o.CategoryName)
                    ? []
                    : [.. lines.Where(l => string.Equals(l.CategoryName, o.CategoryName, StringComparison.OrdinalIgnoreCase))];
            case OfferScope.SpecificItems:
                if (o.Items.Count == 0) return [];
                var ids = o.Items.Select(i => i.MenuItemId).ToHashSet();
                return [.. lines.Where(l => ids.Contains(l.MenuItemId))];
            default:
                return [];
        }
    }

    // ---------- Pricing ----------

    private static AppliedOffer? Price(Offer offer, IReadOnlyList<OfferCartLine> lines)
    {
        var qualifying = Qualifying(offer, lines);
        if (qualifying.Count == 0) return null;

        return offer.Type switch
        {
            OfferType.Percentage => PricePercentage(offer, qualifying),
            OfferType.Flat => PriceFlat(offer, qualifying),
            OfferType.BuyXGetY => PriceBuyXGetY(offer, qualifying),
            OfferType.Combo => PriceCombo(offer, lines),
            _ => null,
        };
    }

    /// <summary>Discounts a meal deal down to its fixed price. A combo needs EVERY item in its
    /// set present; the number of complete combos is the least-stocked item's count (2 burgers +
    /// 2 fries + 1 coke makes one combo, not two), capped by MaxApplicationsPerBill. The saving
    /// per combo is the à-la-carte total of one of each minus ComboPrice, and it's distributed
    /// across the combo's lines so each lands on its own GST slab — same reason BuyXGetY does.</summary>
    private static AppliedOffer? PriceCombo(Offer offer, IReadOnlyList<OfferCartLine> lines)
    {
        var requiredIds = offer.Items.Select(i => i.MenuItemId).Distinct().ToList();
        if (requiredIds.Count < 2 || offer.ComboPrice <= 0) return null;

        // One representative cart line per required item (its unit price), plus how many are on
        // the bill. A required item that's missing means no combo at all.
        var byItem = new Dictionary<int, (OfferCartLine Line, int Qty)>();
        foreach (var id in requiredIds)
        {
            var forItem = lines.Where(l => l.MenuItemId == id).ToList();
            if (forItem.Count == 0) return null;
            byItem[id] = (forItem[0], forItem.Sum(l => l.Qty));
        }

        var combos = byItem.Values.Min(v => v.Qty);
        if (offer.MaxApplicationsPerBill > 0) combos = Math.Min(combos, offer.MaxApplicationsPerBill);
        if (combos <= 0) return null;

        var alaCartePerCombo = byItem.Values.Sum(v => v.Line.UnitPrice);
        var savingPerCombo = alaCartePerCombo - offer.ComboPrice;
        if (savingPerCombo <= 0) return null; // combo priced at or above à la carte — nothing to give

        var amount = savingPerCombo * combos;

        // Attribute across the combo's lines in proportion to their price, so the reduction sits
        // on the right items (and the right tax rates).
        var comboLines = byItem.Values.Select(v => v.Line).ToList();
        return new AppliedOffer(offer.Id, offer.Title, amount,
            combos == 1 ? $"Combo — ₹{offer.ComboPrice:0.##}" : $"Combo ×{combos} — ₹{offer.ComboPrice:0.##} each",
            Distribute(amount, comboLines));
    }

    private static AppliedOffer? PricePercentage(Offer offer, List<OfferCartLine> qualifying)
    {
        if (offer.Value <= 0) return null;
        var basis = qualifying.Sum(l => l.LineTotal);
        if (basis <= 0) return null;

        var amount = Math.Round(basis * offer.Value / 100m, 2);
        if (offer.MaxDiscountAmount > 0) amount = Math.Min(amount, offer.MaxDiscountAmount);
        amount = Math.Min(amount, basis);
        if (amount <= 0) return null;

        var detail = offer.MaxDiscountAmount > 0 && amount == offer.MaxDiscountAmount
            ? $"{offer.Value:0.##}% off (capped at ₹{offer.MaxDiscountAmount:0.##})"
            : $"{offer.Value:0.##}% off";

        return new AppliedOffer(offer.Id, offer.Title, amount, detail, Distribute(amount, qualifying));
    }

    private static AppliedOffer? PriceFlat(Offer offer, List<OfferCartLine> qualifying)
    {
        if (offer.Value <= 0) return null;
        var basis = qualifying.Sum(l => l.LineTotal);
        if (basis <= 0) return null;

        // Never hand back more than the qualifying lines are worth — a ₹100 offer scoped to a
        // ₹60 side dish takes ₹60, otherwise the rest of the bill silently subsidises it.
        var amount = Math.Min(offer.Value, basis);
        return new AppliedOffer(offer.Id, offer.Title, amount, $"₹{amount:0.##} off",
            Distribute(amount, qualifying));
    }

    private static AppliedOffer? PriceBuyXGetY(Offer offer, List<OfferCartLine> qualifying)
    {
        if (offer.BuyQty <= 0 || offer.GetQty <= 0) return null;

        var setSize = offer.BuyQty + offer.GetQty;

        // Expand to individual units so a line of Qty 3 can have one unit free and two paid —
        // the whole point of BOGO is that it cuts across line boundaries.
        var units = qualifying
            .SelectMany(l => Enumerable.Repeat(l, l.Qty))
            .OrderByDescending(l => l.UnitPrice)
            .ToList();

        var sets = units.Count / setSize;
        if (offer.MaxApplicationsPerBill > 0) sets = Math.Min(sets, offer.MaxApplicationsPerBill);
        if (sets <= 0) return null;

        // Units are sorted dearest first, so the tail of each complete set is its cheapest
        // members — those are the ones given away.
        var free = new List<OfferCartLine>();
        for (var set = 0; set < sets; set++)
            for (var slot = offer.BuyQty; slot < setSize; slot++)
                free.Add(units[(set * setSize) + slot]);

        var amount = free.Sum(u => u.UnitPrice);
        if (amount <= 0) return null;

        var perLine = new Dictionary<int, decimal>();
        foreach (var unit in free)
            perLine[unit.LineKey] = perLine.GetValueOrDefault(unit.LineKey) + unit.UnitPrice;

        return new AppliedOffer(offer.Id, offer.Title, amount, DescribeFree(free), perLine);
    }

    /// <summary>"1 × Espresso free", or "3 items free" once it spans several products — the
    /// string the POS banner and the printed bill both show, so the customer can see which unit
    /// was given away rather than an unexplained deduction.</summary>
    private static string DescribeFree(List<OfferCartLine> free)
    {
        var byName = free.GroupBy(u => u.Name).ToList();
        if (byName.Count == 1)
        {
            var only = byName[0];
            return $"{only.Count()} × {only.Key} free";
        }
        return $"{free.Count} items free";
    }

    // ---------- Allocation ----------

    /// <summary>Splits one rupee figure across the lines that earned it, in proportion to each
    /// line's value. The largest line is settled last and absorbs the rounding remainder, so the
    /// parts always add back to exactly <paramref name="amount"/> — a paise adrift here shows up
    /// as a bill whose lines do not sum to its total.</summary>
    private static Dictionary<int, decimal> Distribute(decimal amount, List<OfferCartLine> lines)
    {
        var split = new Dictionary<int, decimal>();
        var basis = lines.Sum(l => l.LineTotal);
        if (basis <= 0 || amount <= 0) return split;

        var ordered = lines.OrderBy(l => l.LineTotal).ToList();
        var allocated = 0m;

        for (var i = 0; i < ordered.Count; i++)
        {
            var line = ordered[i];
            var share = i == ordered.Count - 1
                ? amount - allocated
                : Math.Round(amount * line.LineTotal / basis, 2);

            if (share <= 0) continue;
            split[line.LineKey] = split.GetValueOrDefault(line.LineKey) + share;
            allocated += share;
        }

        return split;
    }

    /// <summary>Lays the chosen offers onto the bill largest-first, never letting the discounts
    /// on a line exceed what the line is worth. Without the clamp two stacked offers on the same
    /// item could hand back more than the customer is paying for it.</summary>
    private static (List<AppliedOffer> Applied, Dictionary<int, decimal> PerLine, decimal Total) Merge(
        List<AppliedOffer> chosen, IReadOnlyList<OfferCartLine> lines)
    {
        var cap = lines.ToDictionary(l => l.LineKey, l => l.LineTotal);
        var used = new Dictionary<int, decimal>();
        var applied = new List<AppliedOffer>();

        foreach (var offer in chosen.OrderByDescending(o => o.DiscountAmount).ThenBy(o => o.OfferId))
        {
            var trimmed = new Dictionary<int, decimal>();
            var total = 0m;

            foreach (var (lineKey, wanted) in offer.PerLineDiscount)
            {
                var already = used.GetValueOrDefault(lineKey);
                var room = cap.GetValueOrDefault(lineKey) - already;
                var take = Math.Min(wanted, room);
                if (take <= 0) continue;

                trimmed[lineKey] = take;
                used[lineKey] = already + take;
                total += take;
            }

            if (total > 0)
                applied.Add(offer with { DiscountAmount = total, PerLineDiscount = trimmed });
        }

        return (applied, used, used.Values.Sum());
    }

    // ---------- Upsell ----------

    private static List<OfferNearMiss> NearMisses(
        List<Offer> live, IReadOnlyList<OfferCartLine> lines, decimal subtotal)
    {
        var misses = new List<OfferNearMiss>();

        foreach (var offer in live)
        {
            if (offer.MinOrderValue > 0 && subtotal < offer.MinOrderValue)
            {
                if (subtotal >= offer.MinOrderValue * NearMissFloor)
                {
                    var gap = Math.Round(offer.MinOrderValue - subtotal, 2);
                    misses.Add(new OfferNearMiss(offer.Id, offer.Title, $"Add ₹{gap:0.##} more to unlock"));
                }
                continue;
            }

            if (offer.Type is not OfferType.BuyXGetY || offer.BuyQty <= 0 || offer.GetQty <= 0) continue;

            var qualifying = Qualifying(offer, lines);
            if (qualifying.Count == 0) continue;

            var setSize = offer.BuyQty + offer.GetQty;
            var unitCount = qualifying.Sum(l => l.Qty);

            // Already capped out — another unit buys nothing, so say nothing.
            if (offer.MaxApplicationsPerBill > 0 && unitCount / setSize >= offer.MaxApplicationsPerBill) continue;

            var towardsNextSet = unitCount % setSize;
            if (towardsNextSet < offer.BuyQty) continue;

            var needed = setSize - towardsNextSet;
            misses.Add(new OfferNearMiss(offer.Id, offer.Title,
                $"Add {needed} more to get {offer.GetQty} free"));
        }

        return misses;
    }
}
