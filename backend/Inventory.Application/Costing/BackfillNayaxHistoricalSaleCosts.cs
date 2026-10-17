using Inventory.Application.Nayax;
using Inventory.Domain.Reporting.ProductMatching;

namespace Inventory.Application.Costing;

/// <summary>
/// Recovers historical sale costs from the transaction-level Nayax <c>Product Cost Price</c>
/// already persisted on each sale by the Nayax transaction import (issue #297), moved unchanged
/// from the former <c>SaleCostingService.BackfillHistoricalCostsFromNayaxAsync</c> behind
/// <c>POST api/sale-costing/nayax-cost-backfill/dry-run</c> and <c>/apply</c>. It makes no Nayax
/// API call. A completed sale in the optional authorization-time range (and, optionally, matched to
/// one product) is a candidate when it is still pending or in error - or any status with
/// <c>force</c> - and has a non-negative Nayax cost; a candidate is costed at that cost with the
/// <see cref="SaleCostOrigin.NayaxTransactionExport"/> provenance. A negative Nayax cost is never
/// applied. A dry run loads untracked rows and stages and saves nothing.
///
/// <para>The optional filter names a <em>local</em> product id, and each sale's stored Nayax product
/// id is resolved to one through the provider identity link (issue #747, parent #329 decision A2c)
/// rather than compared with <c>Product.Id</c>. A Nayax id with no link is an unknown product,
/// exactly as an unmatched id already was: the name fallback still runs, and a sale that matches
/// nothing is simply not selected by a filtered run - never attributed to the product whose local id
/// happens to equal the remote one.</para>
/// </summary>
public sealed class BackfillNayaxHistoricalSaleCosts
{
    private readonly ISaleCostingStore _store;
    private readonly INayaxProductLinkStore _productLinks;

    public BackfillNayaxHistoricalSaleCosts(ISaleCostingStore store, INayaxProductLinkStore productLinks)
    {
        _store = store;
        _productLinks = productLinks;
    }

    public async Task<NayaxCostBackfillResult> Handle(
        bool dryRun = true,
        bool force = false,
        DateTime? from = null,
        DateTime? to = null,
        long? productId = null,
        CancellationToken cancellationToken = default)
    {
        var products = await _store.GetProductCandidatesAsync(cancellationToken);
        var loaded = await _store.LoadCompletedSalesAsync(
            new CompletedSaleSelection(From: from, To: to), forUpdate: !dryRun, cancellationToken);

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
        var withNayaxCost = sales.Count(s => s.NayaxProductCostPrice.HasValue);
        var invalid = sales.Count(s => s.NayaxProductCostPrice < 0);
        var alreadyCosted = sales.Count(s =>
            s.CostingStatus is SaleCostStatus.Costed or SaleCostStatus.LegacyEstimated);
        var candidates = sales.Where(s =>
            (force || s.CostingStatus is SaleCostStatus.Pending or SaleCostStatus.Error) &&
            s.NayaxProductCostPrice is >= 0).ToHashSet();

        var stillPending = sales.Count(s =>
            !candidates.Contains(s) &&
            s.CostingStatus is SaleCostStatus.Pending or SaleCostStatus.Error);
        var errorRows = sales.Count(s =>
            !candidates.Contains(s) && s.CostingStatus == SaleCostStatus.Error);

        if (!dryRun)
        {
            foreach (var sale in sales.Where(candidates.Contains))
                _store.StageCost(sale, new SaleCostAssignment(
                    sale.NayaxProductCostPrice, SaleCostStatus.Costed, SaleCostOrigin.NayaxTransactionExport));
            await _store.SaveChangesAsync(cancellationToken);
        }

        return new NayaxCostBackfillResult(
            sales.Count, withNayaxCost, candidates.Count, alreadyCosted,
            stillPending, invalid, errorRows, dryRun);
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
