namespace Inventory.Domain.Machines;

/// <summary>
/// A single dashboard period's already-resolved direct-profit facts for one machine. <see cref="ResolutionUnavailable"/>
/// covers every reason the per-sale commission resolution itself could not produce a value: a
/// completed sale in the period with no persisted COGS, the machine not mapped to a site while it has
/// sales, or an ambiguous/missing site commission agreement covering one of its sale dates. <see cref="NetSalesBeforeFees"/>
/// is the sum of settlement value minus persisted COGS minus resolved site commission across the
/// period's completed sales, meaningless when <see cref="ResolutionUnavailable"/> is true.
/// </summary>
public readonly record struct MachineDashboardDirectProfitInputs(
    bool ResolutionUnavailable,
    decimal NetSalesBeforeFees,
    bool HasMissingFeeRates,
    decimal FeesIncludingGst);

/// <summary>
/// The machine dashboard's period direct-profit rule: unavailable when commission resolution failed
/// or an effective Nayax processing fee rate is missing for the period, otherwise net sales less fees.
/// Mirrors the former <c>InventoryApi.Services.MachineService.CalculateDirectProfitAsync</c> private
/// helper exactly (issue #241). Deliberately separate from
/// <c>Inventory.Domain.Reporting.Profitability.MachineDirectProfitPolicy</c>, which answers the same
/// question at report-row (aggregate period) granularity rather than this dashboard's fixed rolling
/// periods - the two are kept apart rather than merged, matching the precedent already documented for
/// row-level versus aggregate profitability rules.
/// </summary>
public static class MachineDashboardDirectProfitPolicy
{
    public static decimal? Calculate(MachineDashboardDirectProfitInputs inputs)
    {
        if (inputs.ResolutionUnavailable || inputs.HasMissingFeeRates) return null;
        return inputs.NetSalesBeforeFees - inputs.FeesIncludingGst;
    }
}
