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
    public static MachineSummary Build(NayaxMachine machine, MachineDashboardFacts facts)
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
            profitabilityStatus);
    }

    private static (decimal GrossRevenue, decimal? DirectProfit) Resolve(MachineDashboardPeriodFacts period) =>
        (period.GrossRevenue, MachineDashboardDirectProfitPolicy.Calculate(period.ProfitInputs));
}
