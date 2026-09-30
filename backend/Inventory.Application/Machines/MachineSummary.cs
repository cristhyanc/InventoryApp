namespace Inventory.Application.Machines;

public sealed record MachineSummary(
    long MachineId,
    string? MachineName,
    string? MachineNumber,
    long? ActorId,
    decimal TodayGrossRevenue,
    decimal CurrentWeekGrossRevenue,
    decimal PreviousComparableWeekGrossRevenue,
    decimal LastWeekGrossRevenue,
    decimal MonthToDateGrossRevenue,
    decimal TwoWeeksAgoGrossRevenue,
    decimal? TodayDirectProfit,
    decimal? CurrentWeekDirectProfit,
    decimal? PreviousComparableWeekDirectProfit,
    decimal? LastWeekDirectProfit,
    decimal? MonthToDateDirectProfit,
    decimal? TwoWeeksAgoDirectProfit,
    string? ProfitabilityStatus);
