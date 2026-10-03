using Inventory.Domain.Reporting.ProductMatching;

namespace Inventory.Application.Costing;

/// <summary>
/// Costs every completed sale whose costing is still pending (issue #297), optionally only those
/// matched to one product, moved unchanged from the former
/// <c>SaleCostingService.CostPendingSalesAsync</c>. Each sale is costed through
/// <see cref="ICostSale"/> in authorization-time order and the result is saved once. Returns how
/// many sales ended up costed.
/// </summary>
public sealed class CostPendingSales
{
    private readonly ISaleCostingStore _store;
    private readonly ICostSale _costSale;

    public CostPendingSales(ISaleCostingStore store, ICostSale costSale)
    {
        _store = store;
        _costSale = costSale;
    }

    public async Task<int> Handle(long? productId = null, CancellationToken cancellationToken = default)
    {
        var products = await _store.GetProductCandidatesAsync(cancellationToken);
        var sales = (await _store.LoadCompletedSalesAsync(
                new CompletedSaleSelection(PendingOnly: true), forUpdate: true, cancellationToken))
            .Where(s => !productId.HasValue ||
                ProductMatcher.Match(products, s.NayaxProductId, s.ProductName) == productId.Value)
            .ToList();

        var costed = 0;
        foreach (var sale in sales)
        {
            var cost = await _costSale.Handle(sale, cancellationToken: cancellationToken);
            if (cost is not null)
                _store.StageCost(sale, cost);
            if ((cost?.Status ?? sale.CostingStatus) == SaleCostStatus.Costed)
                costed++;
        }

        if (sales.Count > 0)
            await _store.SaveChangesAsync(cancellationToken);
        return costed;
    }
}
