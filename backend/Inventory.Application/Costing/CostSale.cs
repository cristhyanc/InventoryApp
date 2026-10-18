using Inventory.Domain.FinancialConfiguration;

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
/// unresolved or the sale is at or before its transition-baseline cutoff; otherwise the
/// transaction-level Nayax <c>Product Cost Price</c>; otherwise uncosted - an error when the
/// product is unresolved or the Nayax cost is negative, pending when no cost is known yet. A
/// negative cost is never applied and no cost is inferred from the product's current price.
///
/// <para>Which product a sale belongs to is the persisted <c>NayaxSales.ProductId</c> association
/// (issue #758, parent #329 decision A2c), not a match made here: this use case reads
/// <see cref="CostableSale.ProductId"/> and never falls back to the provider's product id or to the
/// reported product name. A sale with no association is unresolved and is costed exactly as an
/// unmatched sale already was.</para>
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

        // The product the sale is persisted against (issue #758). Deliberately not re-derived from
        // the provider's product id or the reported name: that association was decided once by the
        // writer that stored the sale, and re-deciding it here would let a product rename or a
        // duplicate name move historical COGS.
        //
        // An unresolved sale reads no ledger cost and consumes no inventory. It does still take the
        // transaction-level Nayax cost below when the sale carries one, which is precedence step 2
        // of AGENTS.md § Inventory and historical costing invariants and exactly what an unmatched
        // sale has always done - not a new outcome this change introduced. Issue #758 asks for an
        // unresolved sale not to be costed at all, which that established precedence contradicts,
        // so the difference is raised on PR #766 for a human decision rather than settled here.
        var productId = sale.ProductId;

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
