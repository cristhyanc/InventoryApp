namespace Inventory.Domain.Machines;

/// <summary>
/// Already-resolved facts about a machine's completed sales over its trailing lookback window and its
/// site commission coverage, used only to choose the dashboard's human-readable profitability status
/// message.
/// </summary>
public readonly record struct MachineProfitabilityStatusInputs(
    bool AnyCompletedSaleMissingCogs,
    bool HasCompletedSales,
    bool MachineMappedToSite,
    bool SiteCommissionCoverageAmbiguousOrMissing);

/// <summary>
/// The machine dashboard's profitability-status message rule, in priority order: missing persisted
/// COGS, then no site mapping, then ambiguous/missing commission agreement coverage, otherwise no
/// status (profit figures stand on their own, or the caller may still report a missing-fee-rate
/// status - see <see cref="MachineDashboardDirectProfitPolicy"/>). Mirrors the former
/// <c>InventoryApi.Services.MachineService.GetProfitabilityStatus</c> private helper exactly (issue #241).
/// </summary>
public static class MachineProfitabilityStatusPolicy
{
    public static string? Determine(MachineProfitabilityStatusInputs inputs)
    {
        if (inputs.AnyCompletedSaleMissingCogs)
            return "Unavailable: one or more completed sales have no persisted COGS.";
        if (!inputs.MachineMappedToSite && inputs.HasCompletedSales)
            return "Unavailable: the machine is not mapped to a site.";
        if (inputs.MachineMappedToSite && inputs.SiteCommissionCoverageAmbiguousOrMissing)
            return "Unavailable: commission agreement coverage is missing or ambiguous.";
        return null;
    }
}
