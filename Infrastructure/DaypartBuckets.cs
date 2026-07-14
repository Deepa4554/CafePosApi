namespace CafePOS.Api.Infrastructure;

/// <summary>Shared 4-hour dayparts (cafe local time) used by both DashboardController's
/// peak-hours chart and OrdersController's KDS rush-forecast endpoint, so "which part of
/// the day" means the same thing everywhere in the app instead of two controllers each
/// defining their own slightly-different buckets.</summary>
public static class DaypartBuckets
{
    public static readonly (string Label, string DisplayLabel, int StartHour, int EndHour)[] All =
    [
        ("08:00", "8–10 AM", 6, 10),
        ("12:00", "10 AM–2 PM", 10, 14),
        ("15:00", "2–5 PM", 14, 17),
        ("19:00", "5–9 PM", 17, 21),
    ];
}
