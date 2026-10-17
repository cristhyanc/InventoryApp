using Inventory.Application.Nayax;
using Inventory.Domain.Reporting.ProductMatching;

namespace Inventory.Application.Costing;

/// <summary>
/// Costs every completed sale whose costing is still pending (issue #297), optionally only those
/// matched to one product, moved unchanged from the former
/// <c>SaleCostingService.CostPendingSalesAsync</c>. Each sale is costed through
/// <see cref="ICostSale"/> in authorization-time order and the result is saved once. Returns how
/// many sales ended up costed.
///
/// <para>The optional filter names a <em>local</em> product id, and each sale's stored Nayax product
/// id is resolved to one through the provider identity link (issue #747, parent #329 decision A2c)
/// rather than compared with <c>Product.Id</c>. A Nayax id with no link is an unknown product,
/// exactly as an unmatched id already was: the name fallback still runs, and a sale that matches
/// nothing is simply not selected by a filtered run - never attributed to the product whose local id
/// happens to equal the remote one.</para>
/// </summary>
public sealed class CostPendingSales
{
    private readonly ISaleCostingStore _store;
    private readonly ICostSale _costSale;
    private readonly INayaxProductLinkStore _productLinks;

    public CostPendingSales(ISaleCostingStore store, ICostSale costSale, INayaxProductLinkStore productLinks)
    {
        _store = store;
        _costSale = costSale;
        _productLinks = productLinks;
    }

    public async Task<int> Handle(long? productId = null, CancellationToken cancellationToken = default)
    {
        var products = await _store.GetProductCandidatesAsync(cancellationToken);
        var loaded = await _store.LoadCompletedSalesAsync(
            new CompletedSaleSelection(PendingOnly: true), forUpdate: true, cancellationToken);

        // Resolved once for the whole batch, and awaited in sequence with the reads above: they all
        // land on the request's own scoped persistence context, which supports one operation at a
        // time (docs/architecture.md § Concurrency inside one request).
        var localProductIdByNayaxId = await _productLinks.GetLocalProductIdsAsync(
            loaded
                .Where(s => s.NayaxProductId.HasValue)
                .Select(s => s.NayaxProductId!.Value)
                .Distinct()
                .ToList(),
            cancellationToken);
        var sales = loaded
            .Where(s => !productId.HasValue ||
                ProductMatcher.MatchLinked(
                    products,
                    LocalProductId(s.NayaxProductId, localProductIdByNayaxId),
                    s.ProductName) == productId.Value)
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

    /// <summary>
    /// The local product a sale's stored Nayax product id names, or <see langword="null"/> when this
    /// business holds no Nayax link for it (issue #747).
    /// </summary>
    private static long? LocalProductId(
        long? nayaxProductId, IReadOnlyDictionary<long, long> localProductIdByNayaxId) =>
        nayaxProductId is long id && localProductIdByNayaxId.TryGetValue(id, out var localProductId)
            ? localProductId
            : null;
}
