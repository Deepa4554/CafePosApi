using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

[ApiController]
[Route("api/dashboard")]
[Authorize(Policy = Policies.OwnerOrManager)]
[Authorize(Policy = Policies.RequirePlus)]
public class DashboardController(CafePosDbContext db) : ControllerBase
{
    private static readonly (string Label, int StartHour, int EndHour)[] HourBuckets =
    [
        ("08:00", 6, 10),
        ("12:00", 10, 14),
        ("15:00", 14, 17),
        ("19:00", 17, 21),
    ];

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
        var now = DateTime.UtcNow;

        DateTime periodStart;
        DateTime periodEndExclusive;
        DateTime previousPeriodStart;

        if (from is not null || to is not null)
        {
            periodStart = (from ?? to!.Value).ToDateTime(TimeOnly.MinValue);
            periodEndExclusive = (to ?? from!.Value).ToDateTime(TimeOnly.MinValue).AddDays(1);
            if (periodEndExclusive <= periodStart) periodEndExclusive = periodStart.AddDays(1);
            previousPeriodStart = periodStart - (periodEndExclusive - periodStart);
        }
        else
        {
            if (days <= 0) days = 7;
            periodStart = now.AddDays(-days);
            periodEndExclusive = now;
            previousPeriodStart = now.AddDays(-2 * days);
        }

        var orders = await db.Orders
            .Include(o => o.Items)
            .Where(o => o.CreatedAt >= previousPeriodStart && o.CreatedAt < periodEndExclusive)
            .ToListAsync();

        var currentPaid = orders.Where(o => o.Paid && o.CreatedAt >= periodStart).ToList();
        var previousPaid = orders.Where(o => o.Paid && o.CreatedAt >= previousPeriodStart && o.CreatedAt < periodStart).ToList();

        var revenue = currentPaid.Sum(o => o.Total);
        var previousRevenue = previousPaid.Sum(o => o.Total);
        var salesCount = currentPaid.Count;
        var avgOrderValue = salesCount > 0 ? revenue / salesCount : 0m;
        var gstCollected = currentPaid.Sum(o => o.Tax);
        var refundsTotal = currentPaid.Where(o => o.Refunded).Sum(o => o.RefundedAmount ?? 0m);

        // Calendar-day revenue (resets at midnight, not a rolling 24h window) — its own
        // query, deliberately independent of whatever period/range was requested above,
        // so it's always "today" even when viewing a custom range that excludes today.
        var todayPaidTotal = await db.Orders
            .Where(o => o.Paid && o.CreatedAt >= now.Date && o.CreatedAt < now.Date.AddDays(1))
            .Select(o => o.Total)
            .ToListAsync();
        var todayRevenue = todayPaidTotal.Sum();
        var todaySalesCount = todayPaidTotal.Count;

        var inventoryItems = await db.InventoryItems.ToListAsync();
        var inventoryValue = inventoryItems.Sum(i => (decimal)i.Current * i.UnitCost);

        var daySpan = Math.Max(1, (int)Math.Ceiling((periodEndExclusive - periodStart).TotalDays));
        var weekly = Enumerable.Range(0, daySpan).Select(offset =>
        {
            var day = periodStart.Date.AddDays(offset);
            var dayRevenue = currentPaid.Where(o => o.CreatedAt.Date == day).Sum(o => o.Total);
            return new DailyRevenueDto(daySpan <= 7 ? day.ToString("ddd").ToUpperInvariant() : day.ToString("d MMM"), dayRevenue);
        }).ToList();

        var hourCounts = HourBuckets
            .Select(b => currentPaid.Count(o => o.CreatedAt.Hour >= b.StartHour && o.CreatedAt.Hour < b.EndHour))
            .ToList();
        var maxHourCount = Math.Max(1, hourCounts.Count > 0 ? hourCounts.Max() : 0);
        var peakHours = HourBuckets
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
    [HttpGet("forecast")]
    public async Task<SalesForecastDto> Forecast([FromQuery] int forecastDays = 7)
    {
        if (forecastDays <= 0) forecastDays = 7;
        const int historyDays = 14;
        var now = DateTime.UtcNow;
        var historyStart = now.Date.AddDays(-historyDays + 1);

        var paidOrders = await db.Orders
            .Where(o => o.Paid && o.CreatedAt >= historyStart)
            .Select(o => new { o.CreatedAt, o.Total })
            .ToListAsync();

        var dailyRevenue = Enumerable.Range(0, historyDays)
            .Select(offset => historyStart.AddDays(offset))
            .Select(day => paidOrders.Where(o => o.CreatedAt.Date == day).Sum(o => o.Total))
            .ToList();

        var (slope, intercept) = FitLinearTrend(dailyRevenue);

        var forecast = Enumerable.Range(1, forecastDays)
            .Select(i =>
            {
                var dayIndex = historyDays - 1 + i; // continue the same x-axis used to fit the line
                var predicted = (decimal)slope * dayIndex + intercept;
                var date = now.Date.AddDays(i);
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
