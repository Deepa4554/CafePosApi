namespace CafePOS.Api.Infrastructure;

/// <summary>
/// Cafe local time (Asia/Kolkata, IST = UTC+5:30, no DST). CreatedAt timestamps are
/// stored UTC; anything that talks in calendar days or times of day — "today's
/// revenue", the peak-hours chart, a from/to date range picked in the app — means the
/// cafe's own clock, not UTC's. Before this helper only OrdersController.RushForecast
/// did the shift (inline AddHours(5.5)); the dashboard/report day boundaries silently
/// ran on UTC midnight, i.e. 5:30 AM IST.
/// </summary>
public static class IstClock
{
    public static readonly TimeSpan Offset = TimeSpan.FromMinutes(330);

    /// <summary>The current wall-clock time at the cafe. A display/bucketing value — never
    /// store it, and never put it in a DTO: adding a TimeSpan keeps Kind = Utc, so this comes
    /// out holding IST wall-clock while still claiming to be UTC. System.Text.Json would stamp
    /// it with a "Z" and the client would shift it a second time (see DeliveryController's
    /// ToOffset note). Format it to a string, or use IstDateStartUtc for a real instant.</summary>
    public static DateTime NowIst => DateTime.UtcNow + Offset;

    /// <summary>Shifts a stored-UTC timestamp to cafe wall-clock time for day/hour bucketing.</summary>
    public static DateTime ToIst(DateTime utc) => utc + Offset;

    /// <summary>The UTC instant at which the given IST calendar date begins — use this to
    /// build SQL-comparable bounds from a user-picked yyyy-MM-dd date.</summary>
    public static DateTime IstDateStartUtc(DateOnly istDate) => istDate.ToDateTime(TimeOnly.MinValue) - Offset;
}
