using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Reporting.ProductMatching;

namespace Inventory.Application.Costing;

/// <summary>The historical sale-costing use case's contract (issue #297).</summary>
public interface ICostSale
{
    /// <summary>
    /// Decides the sale's historical cost, or returns <c>null</c> when the sale already has a
    /// final cost and <paramref name="force"/> is not set, so the caller keeps it unchanged.
    /// </summary>
    Task<SaleCostAssignment?> Handle(CostableSale sale, bool force = false, CancellationToken cancellationToken = default);
}

/// <summary>
/// Decides one sale's historical cost (issue #297, child 3 of #149), moved unchanged from the
/// former <c>InventoryApi.Services.SaleCostingService.CostSaleAsync</c>. A sale that is not
/// completed (status ID 12) is left uncosted and pending. A completed sale already costed or
/// legacy-estimated with both costs present is kept unless forced. Otherwise the precedence is:
/// the internal inventory-ledger (AVCO) cost immediately before the sale, unless the product is
/// unmatched or the sale is at or before its transition-baseline cutoff; otherwise the
/// transaction-level Nayax <c>Product Cost Price</c>; otherwise uncosted - an error when the
/// product is unmatched or the Nayax cost is negative, pending when no cost is known yet. A
/// negative cost is never applied and no cost is inferred from the product's current price.
/// </summary>
public sealed class CostSale : ICostSale
{
    private readonly ISaleCostingStore _store;
    private readonly IRebuildProductCost _rebuild;

    public CostSale(ISaleCostingStore store, IRebuildProductCost rebuild)
    {
        _store = store;
        _rebuild = rebuild;
    }

    public async Task<SaleCostAssignment?> Handle(
        CostableSale sale,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sale);

        if (!NayaxTransactionStatusClassifier.IsCompletedSale(sale.TransactionStatusId))
            return new SaleCostAssignment(null, SaleCostStatus.Pending, SaleCostOrigin.Unknown);

        if (!force &&
            sale.CostingStatus is SaleCostStatus.Costed or SaleCostStatus.LegacyEstimated &&
            sale.UnitCostAtSale.HasValue && sale.CostOfGoodsSold.HasValue)
            return null;

        var productId = ProductMatcher.Match(
            await _store.GetProductCandidatesAsync(cancellationToken),
            sale.NayaxProductId,
            sale.ProductName);

        var baselineCutoff = productId is null
            ? null
            : await _store.GetTransitionCutoffAsync(productId.Value, cancellationToken);
        var cost = productId is null || baselineCutoff.HasValue && sale.AuthorizationTime <= baselineCutoff.Value
            ? null
            : await _rebuild.GetAverageUnitCostAtAsync(productId.Value, sale.AuthorizationTime, sale.TransactionId, cancellationToken);

        if (cost is >= 0)
            return new SaleCostAssignment(cost.Value, SaleCostStatus.Costed, SaleCostOrigin.InventoryLedger);
        if (sale.NayaxProductCostPrice is >= 0)
            return new SaleCostAssignment(sale.NayaxProductCostPrice.Value, SaleCostStatus.Costed, SaleCostOrigin.NayaxTransactionExport);

        return new SaleCostAssignment(
            null,
            productId is null || sale.NayaxProductCostPrice < 0 ? SaleCostStatus.Error : SaleCostStatus.Pending,
            SaleCostOrigin.Unknown);
    }
}
