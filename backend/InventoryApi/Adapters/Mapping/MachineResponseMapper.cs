using Inventory.Application.Machines;
using InventoryApi.DTOs;

namespace InventoryApi.Adapters.Mapping;

/// <summary>
/// Projects the Application layer's <see cref="MachineSummary"/> onto the API-owned
/// <see cref="MachineResponse"/> the machine dashboard endpoints serialize (issue #302). It replaces
/// the step the retired <c>MachineService</c> delegator used to take through the
/// <c>Inventory.Infrastructure.Models.Machine</c> type, and copies already-resolved values only: every revenue,
/// profit and profitability status is decided by
/// <see cref="GetMachineDashboard"/>/<see cref="ListMachineDashboard"/> and their Domain policies,
/// so this mapping cannot introduce a second copy of a dashboard rule.
/// </summary>
public static class MachineResponseMapper
{
    public static MachineResponse ToResponse(MachineSummary summary) => new()
    {
        MachineID = summary.MachineId,
        MachineName = summary.MachineName,
        MachineNumber = summary.MachineNumber,
        ActorID = summary.ActorId,
        TodayGrossRevenue = summary.TodayGrossRevenue,
        CurrentWeekGrossRevenue = summary.CurrentWeekGrossRevenue,
        LastWeekGrossRevenue = summary.LastWeekGrossRevenue,
        TwoWeeksAgoGrossRevenue = summary.TwoWeeksAgoGrossRevenue,
        TodayDirectProfit = summary.TodayDirectProfit,
        TwoWeeksAgoDirectProfit = summary.TwoWeeksAgoDirectProfit,
        LastWeekDirectProfit = summary.LastWeekDirectProfit,
        CurrentWeekDirectProfit = summary.CurrentWeekDirectProfit,
        PreviousComparableWeekGrossRevenue = summary.PreviousComparableWeekGrossRevenue,
        PreviousComparableWeekDirectProfit = summary.PreviousComparableWeekDirectProfit,
        MonthToDateGrossRevenue = summary.MonthToDateGrossRevenue,
        MonthToDateDirectProfit = summary.MonthToDateDirectProfit,
        ProfitabilityStatus = summary.ProfitabilityStatus,
    };
}
