using Inventory.Application.Sites;
using Inventory.Domain.Sites;

namespace InventoryApi.Tests.Application.Sites;

/// <summary>One business date's resolved commission amounts and Nayax processing fee rate.</summary>
public sealed record SiteFinancialConfigurationOnDate(
    IReadOnlyDictionary<decimal, decimal> CommissionAmountByRetailPrice,
    decimal? FeeExGst);

/// <summary>
/// In-memory fake of the commission/fee resolution port that answers differently per effective date,
/// the way the real effective-dated configuration does (issue #310). A date with no configured entry
/// resolves to no commission and no fee rate - the same "no rate covers this date" answer the real
/// adapter gives - so a use case that asks for the wrong date produces visibly different pricing
/// instead of silently passing. Every requested date is recorded as well, so a test can assert both
/// the selected configuration and the date it was selected for.
/// </summary>
public sealed class EffectiveDatedSiteFactsStore : ISiteFactsStore
{
    private static readonly SiteFinancialConfigurationOnDate NoConfiguration =
        new(new Dictionary<decimal, decimal>(), null);

    private readonly IReadOnlyDictionary<DateTime, SiteFinancialConfigurationOnDate> _byBusinessDate;
    private readonly IReadOnlyDictionary<long, SiteProductCostBasis> _costBasis;

    public EffectiveDatedSiteFactsStore(
        IReadOnlyDictionary<DateTime, SiteFinancialConfigurationOnDate> byBusinessDate,
        IReadOnlyDictionary<long, SiteProductCostBasis>? costBasis = null)
    {
        _byBusinessDate = byBusinessDate;
        _costBasis = costBasis ?? new Dictionary<long, SiteProductCostBasis>();
    }

    /// <summary>The dates the commission and fee lookups were asked for, in call order.</summary>
    public List<DateTime> AsOfDates { get; } = [];

    public Task<IReadOnlyList<SiteProductActivityFact>> GetProductActivityAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<SiteCompletedSaleFact>> GetRecentCompletedSalesAsync(
        IReadOnlyCollection<long> machineIds, DateTime since, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyDictionary<long, SiteProductCostBasis>> GetProductCostBasisAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_costBasis);

    public Task<SiteCardCommissionResolution> ResolveCardCommissionAsync(
        long siteId, DateTime asOfDate, IReadOnlyCollection<decimal> candidateRetailPrices, CancellationToken cancellationToken)
    {
        AsOfDates.Add(asOfDate);
        return Task.FromResult(new SiteCardCommissionResolution(false, On(asOfDate).CommissionAmountByRetailPrice));
    }

    public Task<decimal?> ResolveEffectiveFeeExGstAsync(DateTime asOfDate, CancellationToken cancellationToken)
    {
        AsOfDates.Add(asOfDate);
        return Task.FromResult(On(asOfDate).FeeExGst);
    }

    private SiteFinancialConfigurationOnDate On(DateTime asOfDate) =>
        _byBusinessDate.GetValueOrDefault(asOfDate.Date, NoConfiguration);
}
