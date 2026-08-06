namespace CafePOS.Api.Infrastructure;

/// <summary>Shared dayparts (cafe local time — see IstClock) used by both DashboardController's
/// peak-hours chart and OrdersController's KDS rush-forecast endpoint, so "which part of
/// the day" means the same thing everywhere in the app instead of two controllers each
/// defining their own slightly-different buckets.
///
/// The buckets tile all 24 hours. They used to stop at 21:00 and pick up again at 06:00, so an
/// order rung up late at night or after midnight landed in no bucket at all — counted in the
/// day's revenue, yet absent from the peak-hours chart and never feeding the rush forecast's
/// averages. For a cafe that trades past midnight those are exactly the hours worth seeing,
/// so the gap is now its own bucket, which wraps around midnight.</summary>
public static class DaypartBuckets
{
    /// <param name="Label">x-axis label for the peak-hours chart — roughly the middle of the range.</param>
    /// <param name="StartHour">Inclusive.</param>
    /// <param name="EndHour">Exclusive. Less than StartHour means the bucket crosses midnight;
    /// <see cref="Daypart.Contains"/> is what knows the difference, so never compare against
    /// StartHour/EndHour by hand.</param>
    public readonly record struct Daypart(string Label, string DisplayLabel, int StartHour, int EndHour)
    {
        public bool Contains(int hour) => StartHour < EndHour
            ? hour >= StartHour && hour < EndHour
            : hour >= StartHour || hour < EndHour; // wraps past midnight
    }

    public static readonly Daypart[] All =
    [
        new("08:00", "8–10 AM", 6, 10),
        new("12:00", "10 AM–2 PM", 10, 14),
        new("15:00", "2–5 PM", 14, 17),
        new("19:00", "5–9 PM", 17, 21),
        new("01:00", "9 PM–6 AM", 21, 6),
    ];

    /// <summary>Index of the bucket a cafe-local hour falls in. Never -1: the buckets cover
    /// the whole clock.</summary>
    public static int IndexOf(int hour) => Array.FindIndex(All, b => b.Contains(hour));

    /// <summary>The bucket after the one containing <paramref name="hour"/>, wrapping from the
    /// night bucket back round to the morning — "what's coming next" always has an answer, which
    /// is what the rush forecast needs at 11 PM as much as at 11 AM.</summary>
    public static int NextIndexAfter(int hour) => (IndexOf(hour) + 1) % All.Length;
}
