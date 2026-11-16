using Inventory.Domain.NayaxSqs;

namespace Inventory.Application.Costing;

/// <summary>The historical sale-costing use case's contract (issue #297).</summary>
public interface ICostSale
{
    /// <summary>
    /// Decides the sale's historical cost, or returns <c>null</c> when the sale already has a
    /// final cost and <paramref name="force"/> is not set, so the caller keeps it unchanged.
    /// <paramref name="precedence"/> is how many of the four precedence steps to apply; the default
    /// is the sale-time path a rebuild follows (issue #1261).
    /// </summary>
    Task<SaleCostAssignment?> Handle(
        CostableSale sale,
        bool force = false,
        SaleCostPrecedenceScope precedence = SaleCostPrecedenceScope.WithoutLatestPurchase,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Decides one sale's historical cost (issue #297, child 3 of #149), moved unchanged from the
/// former <c>InventoryApi.Services.SaleCostingService.CostSaleAsync</c>. A sale that consumed no
/// unit is left uncosted and pending; which sales consumed one is
/// <see cref="NayaxSaleCountingPolicy.ConsumesStock"/> - status 12 or status 62, neither excluded by
/// review (issue #1107), because a refunded sale dispensed its unit and keeps the cost it was sold
/// at. A sale already costed or legacy-estimated with both costs present is kept unless forced.
/// Otherwise the precedence is:
/// the internal inventory-ledger (AVCO) cost immediately before the sale, unless the product is
/// unresolved or the sale is at or before its transition-baseline cutoff; otherwise the
/// transaction-level Nayax <c>Product Cost Price</c>; otherwise uncosted - an error when the
/// product is unresolved or the Nayax cost is negative, pending when no cost is known yet. A
/// negative cost is never applied and no cost is inferred from the product's current price.
///
/// <para>Precedence step 2 - the latest costed purchase replayed before the sale, stored as
/// <see cref="SaleCostOrigin.Estimated"/> - is applied only for a caller that asks for
/// <see cref="SaleCostPrecedenceScope.Complete"/> (issue #1261), which is the forced backfill. The
/// sale-time path keeps going straight from step 1 to step 3, because the inventory-cost rebuild that
/// follows a stored sale replays step 2 straight afterwards; a forced backfill has no such rebuild
/// inside its own transaction, and re-costing a step-2 sale through step 3 is what let that tool
/// downgrade a cost it recomputed.</para>
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
        SaleCostPrecedenceScope precedence = SaleCostPrecedenceScope.WithoutLatestPurchase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sale);

        if (!NayaxSaleCountingPolicy.ConsumesStock(sale.TransactionStatusId, sale.SqsVerification))
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

        // The product's own cost history is readable for this sale only when the sale is resolved and
        // after the transition baseline cutoff: at or before it the baseline *is* the opening, and the
        // replay ignores everything there, so neither step 1 nor step 2 has anything to say.
        var readsTheLedger = productId is not null &&
            !(baselineCutoff.HasValue && sale.AuthorizationTime <= baselineCutoff.Value);
        var cost = readsTheLedger
            ? await _rebuild.GetAverageUnitCostAtAsync(productId!.Value, sale.AuthorizationTime, sale.TransactionId, cancellationToken)
            : null;

        if (cost is >= 0)
            return new SaleCostAssignment(cost.Value, SaleCostStatus.Costed, SaleCostOrigin.InventoryLedger);

        // Precedence step 2, for the callers that ask for the complete precedence (issue #1261): the
        // ledger has no known cost before this sale, so the latest costed purchase replayed before it
        // is a better answer than the cost Nayax sent with the transaction. The sale-time path
        // deliberately skips it, because the inventory-cost rebuild that follows a stored sale
        // replays this very step straight afterwards.
        if (precedence == SaleCostPrecedenceScope.Complete && readsTheLedger)
        {
            var latestPurchase = await _rebuild.GetLatestPurchaseUnitCostBeforeAsync(
                productId!.Value, sale.AuthorizationTime, sale.TransactionId, cancellationToken);
            if (latestPurchase is >= 0)
                return new SaleCostAssignment(latestPurchase.Value, SaleCostStatus.Costed, SaleCostOrigin.Estimated);
        }

        if (sale.NayaxProductCostPrice is >= 0)
            return new SaleCostAssignment(sale.NayaxProductCostPrice.Value, SaleCostStatus.Costed, SaleCostOrigin.NayaxTransactionExport);

        return new SaleCostAssignment(
            null,
            productId is null || sale.NayaxProductCostPrice < 0 ? SaleCostStatus.Error : SaleCostStatus.Pending,
            SaleCostOrigin.Unknown);
    }
}
