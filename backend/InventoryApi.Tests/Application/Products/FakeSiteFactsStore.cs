using Inventory.Application.Sites;
using Inventory.Domain.Sites;

namespace InventoryApi.Tests.Application.Products;

/// <summary>
/// In-memory fake of the commission/fee resolution port shared with the site dashboard slice, so
/// <c>ResolveMachineProductPricing</c> tests exercise its own orchestration without EF Core or SQLite.
/// </summary>
public sealed class FakeSiteFactsStore : ISiteFactsStore
{
    private readonly bool _configurationUnavailable;
    private readonly IReadOnlyDictionary<decimal, decimal> _commissionByPrice;
    private readonly decimal? _feeExGst;

    public FakeSiteFactsStore(
        IReadOnlyDictionary<decimal, decimal> commissionByPrice, decimal? feeExGst, bool configurationUnavailable = false)
    {
        _commissionByPrice = commissionByPrice;
        _feeExGst = feeExGst;
        _configurationUnavailable = configurationUnavailable;
    }

    public Task<IReadOnlyList<SiteProductActivityFact>> GetProductActivityAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<SiteCompletedSaleFact>> GetRecentCompletedSalesAsync(
        IReadOnlyCollection<long> machineIds, DateTime since, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyDictionary<long, SiteProductCostBasis>> GetProductCostBasisAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<SiteCardCommissionResolution> ResolveCardCommissionAsync(
        long siteId, DateTime asOfDate, IReadOnlyCollection<decimal> candidateRetailPrices, CancellationToken cancellationToken) =>
        Task.FromResult(new SiteCardCommissionResolution(_configurationUnavailable, _commissionByPrice));

    public Task<decimal?> ResolveEffectiveFeeExGstAsync(DateTime asOfDate, CancellationToken cancellationToken) =>
        Task.FromResult(_feeExGst);
}
