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
///
/// <para>Since issue #698 the projection also depends on <see cref="FinancialFigureVisibility"/>:
/// a caller who may not be shown the figures gets <see cref="Redacted"/>, which carries the machine's
/// identity and withholds all thirteen of them as <c>null</c>. Every projection has to state which
/// shape it wants, so a new call site cannot publish the figures by forgetting to ask.</para>
/// </summary>
public static class MachineResponseMapper
{
    public static MachineResponse ToResponse(MachineSummary summary, FinancialFigureVisibility visibility) =>
        visibility == FinancialFigureVisibility.Visible ? Full(summary) : Redacted(summary);

    private static MachineResponse Full(MachineSummary summary) => new()
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

    /// <summary>
    /// The machine without the figures <c>Dashboard.ViewFinancials</c> governs (issue #698). Each one
    /// is written out as <c>null</c> rather than left to a default, so a revenue or profit field
    /// added to the response later has to be decided here too, and so a withheld figure can never
    /// arrive as the <c>0</c> a client would read as a real one. Identity, name, number and the
    /// resolved site actor stay: without them the fleet an Operator restocks is unusable.
    /// </summary>
    private static MachineResponse Redacted(MachineSummary summary) => new()
    {
        MachineID = summary.MachineId,
        MachineName = summary.MachineName,
        MachineNumber = summary.MachineNumber,
        ActorID = summary.ActorId,
        TodayGrossRevenue = null,
        CurrentWeekGrossRevenue = null,
        LastWeekGrossRevenue = null,
        TwoWeeksAgoGrossRevenue = null,
        TodayDirectProfit = null,
        TwoWeeksAgoDirectProfit = null,
        LastWeekDirectProfit = null,
        CurrentWeekDirectProfit = null,
        PreviousComparableWeekGrossRevenue = null,
        PreviousComparableWeekDirectProfit = null,
        MonthToDateGrossRevenue = null,
        MonthToDateDirectProfit = null,
        ProfitabilityStatus = null,
    };
}
