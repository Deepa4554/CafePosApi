namespace CafePOS.Api.Contracts;

public record DailyRevenueDto(string Day, decimal Revenue);
public record HourlyLoadDto(string Time, int OrderCount, int PctOfPeak);
public record TopItemDto(string Name, int Qty);

public record DashboardAnalyticsDto(
    decimal Revenue,
    decimal PreviousPeriodRevenue,
    int SalesCount,
    decimal AvgOrderValue,
    decimal InventoryValue,
    decimal GstCollected,
    decimal RefundsTotal,
    List<DailyRevenueDto> Weekly,
    List<HourlyLoadDto> PeakHours,
    List<TopItemDto> TopItems);
