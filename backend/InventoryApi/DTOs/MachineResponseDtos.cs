namespace InventoryApi.DTOs;

/// <summary>
/// The wire shape of a machine on the "/api/machines" dashboard endpoints (issue #302), replacing
/// the <c>Inventory.Infrastructure.Models.Machine</c> type the controller used to serialize through the retired
/// <c>MachineService</c> delegator. Key names, order and values match that type's serializable
/// surface exactly, so this is not a contract change;
/// <c>InventoryApi.Tests.DTOs.MachineJsonContractTests</c> compares the serialized bytes of both.
///
/// A machine is not persisted: it is a live Nayax view with this business's own sales and costs
/// resolved onto it by <c>Inventory.Application.Machines.GetMachineDashboard</c>/
/// <c>ListMachineDashboard</c>. This response carries that use case's
/// <c>MachineSummary</c> unchanged - every revenue, profit and status value is already resolved
/// there, so nothing is derived here.
///
/// <c>MachineID</c>/<c>ActorID</c> keep the entity's spelling deliberately: the camel-case naming
/// policy turns them into the <c>machineID</c>/<c>actorID</c> keys clients have always read, which
/// a <c>MachineId</c>/<c>ActorId</c> member would silently have renamed to <c>machineId</c>.
///
/// <para><b>Every revenue, profit and status field is nullable because it may be withheld</b>
/// (issue #698). A caller without <c>Dashboard.ViewFinancials</c> - today an Operator - still reaches
/// these endpoints for the restocking work they exist for, and is answered <c>null</c> in all
/// thirteen of them; <see cref="InventoryApi.Adapters.Mapping.FinancialFigureVisibility"/> is the one
/// thing that decides which projection it gets. A withheld figure must never arrive as <c>0</c>,
/// which a client would read as a real "no sales" figure, and the gross-revenue fields stay
/// <c>required</c> so that the redacted projection has to state each withheld figure rather than
/// inherit it from a default.</para>
/// </summary>
public sealed record MachineResponse
{
    public required long MachineID { get; init; }
    public string? MachineName { get; init; }
    public string? MachineNumber { get; init; }
    public long? ActorID { get; init; }

    public required decimal? TodayGrossRevenue { get; init; }
    public required decimal? CurrentWeekGrossRevenue { get; init; }
    public required decimal? LastWeekGrossRevenue { get; init; }
    public required decimal? TwoWeeksAgoGrossRevenue { get; init; }

    /// <summary>
    /// Direct profit for the period, or <c>null</c> when it cannot be calculated - an unavailable
    /// profit is never zero (AGENTS.md § Profit calculations) - or when the caller may not be shown
    /// it at all. <see cref="ProfitabilityStatus"/> carries the reason the dashboard shows alongside
    /// it, and is itself withheld from a caller who may not see the figures.
    /// </summary>
    public decimal? TodayDirectProfit { get; init; }

    public decimal? TwoWeeksAgoDirectProfit { get; init; }
    public decimal? LastWeekDirectProfit { get; init; }
    public decimal? CurrentWeekDirectProfit { get; init; }

    public required decimal? PreviousComparableWeekGrossRevenue { get; init; }
    public decimal? PreviousComparableWeekDirectProfit { get; init; }

    public required decimal? MonthToDateGrossRevenue { get; init; }
    public decimal? MonthToDateDirectProfit { get; init; }

    public string? ProfitabilityStatus { get; init; }
}
