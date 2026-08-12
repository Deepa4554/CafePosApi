using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;

namespace CafePOS.Api.Contracts;

/// <summary>Wire shape for an <see cref="Offer"/>. Two deliberate departures from the entity:
/// the item scope travels as a plain list of menu item ids rather than join rows, and the
/// weekday set as a list of ints rather than the CSV the column stores — the setup screen
/// should not have to know either storage detail.</summary>
public record OfferDto(
    int Id,
    string Title,
    OfferType Type,
    OfferScope Scope,
    string? CategoryName,
    List<int> MenuItemIds,
    decimal Value,
    decimal MaxDiscountAmount,
    int BuyQty,
    int GetQty,
    decimal ComboPrice,
    decimal MinOrderValue,
    int MaxApplicationsPerBill,
    bool Stackable,
    DateTime? StartsAtUtc,
    DateTime? EndsAtUtc,
    List<int> DaysOfWeek,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    bool AutoApply,
    bool IsActive)
{
    public static OfferDto From(Offer o) => new(
        o.Id, o.Title, o.Type, o.Scope, o.CategoryName,
        [.. o.Items.Select(i => i.MenuItemId)],
        o.Value, o.MaxDiscountAmount, o.BuyQty, o.GetQty, o.ComboPrice,
        o.MinOrderValue, o.MaxApplicationsPerBill, o.Stackable,
        o.StartsAtUtc, o.EndsAtUtc, OfferDays.Parse(o.DaysOfWeek),
        o.StartTime, o.EndTime, o.AutoApply, o.IsActive);
}

/// <summary>Everything the setup screen collects. Most fields carry a default so the common
/// offers stay a three-field form: "20% off" is Type + Value, "Buy 2 Get 1" is Type + BuyQty +
/// GetQty. The rest only appear behind Advanced.</summary>
public record CreateOfferRequest(
    string Title,
    OfferType Type,
    OfferScope Scope = OfferScope.EntireBill,
    string? CategoryName = null,
    List<int>? MenuItemIds = null,
    decimal Value = 0,
    decimal MaxDiscountAmount = 0,
    int BuyQty = 0,
    int GetQty = 0,
    decimal ComboPrice = 0,
    decimal MinOrderValue = 0,
    int MaxApplicationsPerBill = 0,
    List<int>? DaysOfWeek = null,
    DateTime? StartsAtUtc = null,
    DateTime? EndsAtUtc = null,
    TimeOnly? StartTime = null,
    TimeOnly? EndTime = null,
    bool Stackable = false,
    bool AutoApply = true);

/// <summary>PATCH shape — every field optional, only the supplied ones are written.
///
/// A null therefore means "leave alone", which is what lets a one-field call like the
/// active/inactive toggle avoid wiping the rest of the offer. That makes clearing an optional
/// window impossible to express as a value, so the two that a user can genuinely remove — the
/// daily time window and the run-date range — get an explicit flag each. Without them the
/// editor's Clear button saves cleanly and changes nothing.</summary>
public record UpdateOfferRequest(
    string? Title = null,
    OfferType? Type = null,
    OfferScope? Scope = null,
    string? CategoryName = null,
    List<int>? MenuItemIds = null,
    decimal? Value = null,
    decimal? MaxDiscountAmount = null,
    int? BuyQty = null,
    int? GetQty = null,
    decimal? ComboPrice = null,
    decimal? MinOrderValue = null,
    int? MaxApplicationsPerBill = null,
    List<int>? DaysOfWeek = null,
    DateTime? StartsAtUtc = null,
    DateTime? EndsAtUtc = null,
    TimeOnly? StartTime = null,
    TimeOnly? EndTime = null,
    bool? Stackable = null,
    bool? AutoApply = null,
    bool? IsActive = null,
    bool ClearTimeWindow = false,
    bool ClearRunDates = false);

// ---------- Preview ----------

/// <summary>One line of the cart being priced. Mirrors <see cref="OfferCartLine"/>; kept as its
/// own record so the engine's input type isn't part of the public API surface.</summary>
public record OfferPreviewLine(
    int LineKey, int MenuItemId, string? CategoryName, string Name, decimal UnitPrice, int Qty);

/// <summary>Prices a cart against the cafe's offers. Supply <paramref name="Draft"/> to price an
/// offer that has not been saved — that is what lets the setup screen show the effect on a
/// sample bill while the owner is still typing, instead of the save-it-then-punch-a-test-bill
/// loop that makes configuring an offer a half-hour job. With no draft, every live offer is
/// evaluated, which is what the POS cart banner asks for.</summary>
public record OfferPreviewRequest(List<OfferPreviewLine> Lines, CreateOfferRequest? Draft = null);

public record AppliedOfferDto(int OfferId, string Title, decimal DiscountAmount, string Detail);

public record OfferNearMissDto(int OfferId, string Title, string Nudge);

public record OfferPreviewResult(
    List<AppliedOfferDto> Applied,
    decimal TotalDiscount,
    List<OfferNearMissDto> NearMisses)
{
    public static OfferPreviewResult From(OfferEvaluation e) => new(
        [.. e.Applied.Select(a => new AppliedOfferDto(a.OfferId, a.Title, a.DiscountAmount, a.Detail))],
        e.TotalDiscount,
        [.. e.NearMisses.Select(n => new OfferNearMissDto(n.OfferId, n.Title, n.Nudge))]);
}

/// <summary>Converts between the weekday CSV the column stores and the int list the API speaks.
/// Values are System.DayOfWeek numbers (0 = Sunday).</summary>
public static class OfferDays
{
    public static List<int> Parse(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return [];
        return [.. csv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var day) ? day : -1)
            .Where(day => day is >= 0 and <= 6)
            .Distinct()
            .Order()];
    }

    /// <summary>Null for "every day" rather than a list of all seven — the engine treats an
    /// empty set as unrestricted, and storing 0-6 would only invite a stale six-day row when
    /// someone edits the list later.</summary>
    public static string? ToCsv(List<int>? days)
    {
        if (days is null || days.Count == 0) return null;
        var valid = days.Where(d => d is >= 0 and <= 6).Distinct().Order().ToList();
        return valid.Count is 0 or 7 ? null : string.Join(',', valid);
    }
}
