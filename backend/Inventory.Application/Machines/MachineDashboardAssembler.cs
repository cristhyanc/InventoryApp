using Inventory.Application.Nayax;
using Inventory.Domain.Machines;

namespace Inventory.Application.Machines;

/// <summary>
/// Shared assembly step for <see cref="ListMachineDashboard"/>/<see cref="GetMachineDashboard"/>:
/// applies <see cref="MachineDashboardDirectProfitPolicy"/> to each period and
/// <see cref="MachineProfitabilityStatusPolicy"/> to the machine's overall status, including the
/// missing-fee-rate fallback status. Mirrors the former
/// <c>InventoryApi.Services.MachineService.GetMachineSalesAsync</c> exactly (issue #241).
/// </summary>
internal static class MachineDashboardAssembler
{
    /// <summary>
    /// Assembles one machine's summary. <paramref name="localMachineId"/> is this business's own
    /// machine row for <paramref name="machine"/>, already resolved from the provider's number
    /// through <c>INayaxMachineLinkStore</c> (issue #780), or <c>null</c> when it has recorded
    /// none - resolved by the caller rather than here, because one lookup serves the whole fleet.
    /// <see cref="GetMachineDashboard"/> passes <c>null</c> always: the single-machine route is out
    /// of scope for issue #780 and resolves nothing (see <see cref="MachineSummary"/>).
    /// <paramref name="facts"/> carries the period aggregates the policies below are applied to.
    /// </summary>
    public static MachineSummary Build(
        NayaxMachine machine, MachineDashboardFacts facts, long? localMachineId)
    {
        var today = Resolve(facts.Today);
        var currentWeek = Resolve(facts.CurrentWeek);
        var previousComparableWeek = Resolve(facts.PreviousComparableWeek);
        var lastWeek = Resolve(facts.LastWeek);
        var monthToDate = Resolve(facts.MonthToDate);
        var twoWeeksAgo = Resolve(facts.TwoWeeksAgo);

        var profitabilityStatus = MachineProfitabilityStatusPolicy.Determine(facts.StatusInputs);
        if (profitabilityStatus is null &&
            facts.StatusInputs.HasCompletedSales &&
            new[]
            {
                today.DirectProfit, currentWeek.DirectProfit, previousComparableWeek.DirectProfit,
                lastWeek.DirectProfit, monthToDate.DirectProfit, twoWeeksAgo.DirectProfit
            }.Any(profit => !profit.HasValue))
            profitabilityStatus = "Unavailable: an effective Nayax processing fee rate is missing.";

        return new MachineSummary(
            machine.MachineID,
            localMachineId,
            machine.MachineName,
            machine.MachineNumber,
            machine.ActorID,
            today.GrossRevenue,
            currentWeek.GrossRevenue,
            previousComparableWeek.GrossRevenue,
            lastWeek.GrossRevenue,
            monthToDate.GrossRevenue,
            twoWeeksAgo.GrossRevenue,
            today.DirectProfit,
            currentWeek.DirectProfit,
            previousComparableWeek.DirectProfit,
            lastWeek.DirectProfit,
            monthToDate.DirectProfit,
            twoWeeksAgo.DirectProfit,
            profitabilityStatus,
            machine.CustomerID);
    }

    private static (decimal GrossRevenue, decimal? DirectProfit) Resolve(MachineDashboardPeriodFacts period) =>
        (period.GrossRevenue, MachineDashboardDirectProfitPolicy.Calculate(period.ProfitInputs));
}
