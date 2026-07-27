using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>
/// Analytics() is deliberately NOT Plus-gated — "how much did I sell today" is core
/// day-1 POS visibility, not an upsell, same reasoning as StaffController's roster
/// split. Only Forecast() (predictive, not just visibility) carries its own explicit
/// RequirePlus.
/// </summary>
[ApiController]
[Route("api/dashboard")]
[Authorize(Policy = Policies.OwnerOrManager)]
public class DashboardController(CafePosDbContext db) : ControllerBase
{
    /// <summary>
    /// Real revenue/sales/inventory/peak-hour analytics computed from actual orders —
    /// replaces the Dashboard screen's previously-hardcoded ANALYTICS_DATA constant.
    ///
    /// Two ways to pick the period: pass <c>days</c> for a rolling window ending now
    /// (the original behavior, still the default), or pass <c>from</c>/<c>to</c>
    /// (yyyy-MM-dd) for an explicit calendar-day range — either one alone extends to
    /// today/the other bound, both together is an arbitrary custom range. `from`/`to`
    /// take priority over `days` when present.
    /// </summary>
    [HttpGet("analytics")]
    public async Task<DashboardAnalyticsDto> Analytics([FromQuery] int days = 7, [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null)
    {
        // Period bounds are computed in IST (a from/to date or "today" means the cafe's
        // calendar day, see IstClock) and converted to UTC only for the SQL comparison
        // against the stored-UTC CreatedAt.
        var nowIst = IstClock.NowIst;

        DateTime periodStartIst;
        DateTime periodEndExclusiveIst;
        DateTime previousPeriodStartIst;

        if (from is not null || to is not null)
        {
            periodStartIst = (from ?? to!.Value).ToDateTime(TimeOnly.MinValue);
            periodEndExclusiveIst = (to ?? from!.Value).ToDateTime(TimeOnly.MinValue).AddDays(1);
            if (periodEndExclusiveIst <= periodStartIst) periodEndExclusiveIst = periodStartIst.AddDays(1);
            previousPeriodStartIst = periodStartIst - (periodEndExclusiveIst - periodStartIst);
        }
        else
        {
            if (days <= 0) days = 7;
            periodStartIst = nowIst.AddDays(-days);
            periodEndExclusiveIst = nowIst;
            previousPeriodStartIst = nowIst.AddDays(-2 * days);
        }

        var previousPeriodStartUtc = previousPeriodStartIst - IstClock.Offset;
        var periodEndExclusiveUtc = periodEndExclusiveIst - IstClock.Offset;
        var orders = await db.Orders
            .Include(o => o.Items)
            .Where(o => o.CreatedAt >= previousPeriodStartUtc && o.CreatedAt < periodEndExclusiveUtc)
            .ToListAsync();

        var currentPaid = orders.Where(o => o.Paid && IstClock.ToIst(o.CreatedAt) >= periodStartIst).ToList();
        var previousPaid = orders.Where(o => o.Paid && IstClock.ToIst(o.CreatedAt) >= previousPeriodStartIst && IstClock.ToIst(o.CreatedAt) < periodStartIst).ToList();

        var revenue = currentPaid.Sum(o => o.Total);
        var previousRevenue = previousPaid.Sum(o => o.Total);
        var salesCount = currentPaid.Count;
        var avgOrderValue = salesCount > 0 ? revenue / salesCount : 0m;
        var gstCollected = currentPaid.Sum(o => o.Tax);
        var refundsTotal = currentPaid.Where(o => o.Refunded).Sum(o => o.RefundedAmount ?? 0m);

        // Calendar-day revenue (resets at IST midnight, not a rolling 24h window) — its
        // own query, deliberately independent of whatever period/range was requested
        // above, so it's always "today" even when viewing a custom range that excludes today.
        var todayStartUtc = IstClock.IstDateStartUtc(DateOnly.FromDateTime(nowIst.Date));
        var todayPaidTotal = await db.Orders
            .Where(o => o.Paid && o.CreatedAt >= todayStartUtc && o.CreatedAt < todayStartUtc.AddDays(1))
            .Select(o => o.Total)
            .ToListAsync();
        var todayRevenue = todayPaidTotal.Sum();
        var todaySalesCount = todayPaidTotal.Count;

        var inventoryItems = await db.InventoryItems.ToListAsync();
        var inventoryValue = inventoryItems.Sum(i => (decimal)i.Current * i.UnitCost);

        var daySpan = Math.Max(1, (int)Math.Ceiling((periodEndExclusiveIst - periodStartIst).TotalDays));
        var weekly = Enumerable.Range(0, daySpan).Select(offset =>
        {
            var day = periodStartIst.Date.AddDays(offset);
            var dayRevenue = currentPaid.Where(o => IstClock.ToIst(o.CreatedAt).Date == day).Sum(o => o.Total);
            return new DailyRevenueDto(daySpan <= 7 ? day.ToString("ddd").ToUpperInvariant() : day.ToString("d MMM"), dayRevenue);
        }).ToList();

        // IST hours against DaypartBuckets' cafe-local-time ranges — same shift
        // OrdersController.RushForecast applies; raw UTC hours put the 1 PM lunch rush
        // in the morning bucket.
        var hourCounts = DaypartBuckets.All
            .Select(b => currentPaid.Count(o => IstClock.ToIst(o.CreatedAt).Hour >= b.StartHour && IstClock.ToIst(o.CreatedAt).Hour < b.EndHour))
            .ToList();
        var maxHourCount = Math.Max(1, hourCounts.Count > 0 ? hourCounts.Max() : 0);
        var peakHours = DaypartBuckets.All
            .Select((b, i) => new HourlyLoadDto(b.Label, hourCounts[i], (int)Math.Round(hourCounts[i] * 100.0 / maxHourCount)))
            .ToList();

        var topItems = currentPaid
            .SelectMany(o => o.Items)
            .GroupBy(i => i.Name)
            .Select(g => new TopItemDto(g.Key, g.Sum(i => i.Qty)))
            .OrderByDescending(t => t.Qty)
            .Take(3)
            .ToList();

        return new DashboardAnalyticsDto(revenue, previousRevenue, salesCount, avgOrderValue, inventoryValue, gstCollected, refundsTotal, weekly, peakHours, topItems, todayRevenue, todaySalesCount);
    }

    /// <summary>
    /// Real sales forecast — a linear-trend line fit to the last 14 days of actual paid
    /// revenue, projected forward. Replaces the AI Assistant screen's previous
    /// Math.random() placeholder. No AI/LLM involved; this is ordinary least-squares
    /// regression on real numbers, which is what "trend forecasting" actually means.
    /// Needs at least 3 days of history to fit a meaningful line — below that it just
    /// repeats the flat average, which is the honest answer when there's too little data.
    /// </summary>
    [Authorize(Policy = Policies.RequirePlus)]
    [HttpGet("forecast")]
    public async Task<SalesForecastDto> Forecast([FromQuery] int forecastDays = 7)
    {
        if (forecastDays <= 0) forecastDays = 7;
        const int historyDays = 14;
        // History days are IST calendar days (see IstClock) — same day-boundary rule as
        // Analytics above, so "yesterday's revenue" means the same thing on both charts.
        var nowIst = IstClock.NowIst;
        var historyStartIst = nowIst.Date.AddDays(-historyDays + 1);
        var historyStartUtc = historyStartIst - IstClock.Offset;

        var paidOrders = await db.Orders
            .Where(o => o.Paid && o.CreatedAt >= historyStartUtc)
            .Select(o => new { o.CreatedAt, o.Total })
            .ToListAsync();

        var dailyRevenue = Enumerable.Range(0, historyDays)
            .Select(offset => historyStartIst.AddDays(offset))
            .Select(day => paidOrders.Where(o => IstClock.ToIst(o.CreatedAt).Date == day).Sum(o => o.Total))
            .ToList();

        var (slope, intercept) = FitLinearTrend(dailyRevenue);

        var forecast = Enumerable.Range(1, forecastDays)
            .Select(i =>
            {
                var dayIndex = historyDays - 1 + i; // continue the same x-axis used to fit the line
                var predicted = (decimal)slope * dayIndex + intercept;
                var date = nowIst.Date.AddDays(i);
                return new ForecastPointDto(date.ToString("MMM d"), Math.Max(0, Math.Round(predicted, 2)));
            })
            .ToList();

        return new SalesForecastDto(forecast, dailyRevenue.Count(r => r > 0) >= 3 ? "Linear trend (last 14 days)" : "Flat average (not enough order history yet)");
    }

    /// <summary>Ordinary least-squares fit of y = slope*x + intercept over the given
    /// series (x = index 0..n-1). Falls back to a flat line at the series' average when
    /// there isn't enough real variation to fit a trend from (e.g. a brand-new cafe).</summary>
    private static (double slope, decimal intercept) FitLinearTrend(List<decimal> series)
    {
        var n = series.Count;
        var nonZeroDays = series.Count(v => v > 0);
        if (n < 2 || nonZeroDays < 3)
            return (0, n > 0 ? series.Average() : 0);

        var xs = Enumerable.Range(0, n).Select(i => (double)i).ToArray();
        var ys = series.Select(v => (double)v).ToArray();
        var xMean = xs.Average();
        var yMean = ys.Average();

        var covariance = xs.Zip(ys, (x, y) => (x - xMean) * (y - yMean)).Sum();
        var variance = xs.Sum(x => (x - xMean) * (x - xMean));
        var slope = variance == 0 ? 0 : covariance / variance;
        var intercept = (decimal)(yMean - slope * xMean);

        return (slope, intercept);
    }
}
