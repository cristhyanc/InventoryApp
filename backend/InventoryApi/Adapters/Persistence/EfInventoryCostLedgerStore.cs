using Inventory.Application.Costing;
using Inventory.Domain.Costing;
using Inventory.Domain.Reporting.ProductMatching;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using DomainStock = Inventory.Domain.Stock;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IInventoryCostLedgerStore"/> (issue #296). It
/// still lives in InventoryApi, not Inventory.Infrastructure: <see cref="AppDbContext"/> and the
/// persistence models it depends on moved there in issue #307, and moving this adapter family after
/// them is Persistence 7/8 and 8/8 of #153.
///
/// The queries are unchanged from the former <c>InventoryCostRebuildService</c>: the product's stock
/// movements, the business's completed Nayax sales matched to the product through the Domain
/// <see cref="ProductMatcher"/> (issue #301 removed the <c>NayaxProductMatcher</c> wrapper this used
/// to call; the catalogue is projected onto candidates once per read and the ID/name matching
/// semantics are unchanged), its costing repairs (issue #359) and its latest transition
/// baseline, all read through <see cref="AppDbContext"/>'s business query filter. Each replay input keeps a reference to the
/// row it came from, so <see cref="StageReplay"/> writes the outcome
/// <see cref="RebuildProductCost"/> decided back onto exactly that tracked row. It never saves, never
/// opens a transaction and applies no costing rule of its own.
/// </summary>
public sealed class EfInventoryCostLedgerStore : IInventoryCostLedgerStore
{
    private readonly AppDbContext _db;

    public EfInventoryCostLedgerStore(AppDbContext db) => _db = db;

    public async Task<InventoryCostLedger?> LoadAsync(long productId, bool forUpdate, CancellationToken cancellationToken)
    {
        var productQuery = forUpdate ? _db.Products.AsQueryable() : _db.Products.AsNoTracking();
        var product = await productQuery.SingleOrDefaultAsync(p => p.Id == productId, cancellationToken);
        if (product is null)
            return null;

        var adjustmentQuery = forUpdate ? _db.StockAdjustments.AsQueryable() : _db.StockAdjustments.AsNoTracking();
        var saleQuery = forUpdate ? _db.NayaxSales.AsQueryable() : _db.NayaxSales.AsNoTracking();
        var adjustments = await adjustmentQuery.Where(x => x.ProductId == productId).ToListAsync(cancellationToken);
        var candidates = await ProductCandidatesAsync(cancellationToken);
        var sales = (await saleQuery
                .Where(EfNayaxSalesQueries.CompletedSalePredicate)
                .ToListAsync(cancellationToken))
            .Where(s => ProductMatcher.Match(candidates, s.NayaxProductId, s.ProductName) == productId)
            .ToList();
        var repairs = await LoadRepairsAsync(productId, asOf: null, cancellationToken);
        var baseline = await _db.InventoryCostTransitionBaselines.AsNoTracking()
            .Where(x => x.ProductId == productId)
            .OrderByDescending(x => x.CutoffAt)
            .FirstOrDefaultAsync(cancellationToken);

        return EfInventoryCostLedger.Create(product, adjustments, sales, repairs, baseline);
    }

    public async Task<InventoryCostLedger?> LoadAsOfAsync(long productId, DateTime asOf, CancellationToken cancellationToken)
    {
        var product = await _db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == productId, cancellationToken);
        if (product is null)
            return null;

        var adjustments = await _db.StockAdjustments.AsNoTracking()
            .Where(x => x.ProductId == productId && x.EffectiveAt <= asOf)
            .ToListAsync(cancellationToken);
        var candidates = await ProductCandidatesAsync(cancellationToken);
        var sales = (await _db.NayaxSales.AsNoTracking()
                .Where(EfNayaxSalesQueries.CompletedSalePredicate)
                .Where(s => s.MachineAuthorizationTime <= asOf)
                .ToListAsync(cancellationToken))
            .Where(s => ProductMatcher.Match(candidates, s.NayaxProductId, s.ProductName) == productId)
            .ToList();
        var repairs = await LoadRepairsAsync(productId, asOf, cancellationToken);
        var baseline = await _db.InventoryCostTransitionBaselines.AsNoTracking()
            .Where(x => x.ProductId == productId && x.CutoffAt <= asOf)
            .OrderByDescending(x => x.CutoffAt)
            .FirstOrDefaultAsync(cancellationToken);

        return EfInventoryCostLedger.Create(product, adjustments, sales, repairs, baseline);
    }

    /// <summary>
    /// The business's product catalogue as the Domain <see cref="ProductMatcher"/> consumes it,
    /// projected once per read so matching a whole sale history stays one pass over one list.
    /// </summary>
    private async Task<IReadOnlyList<ProductMatchCandidate>> ProductCandidatesAsync(CancellationToken cancellationToken) =>
        (await _db.Products.AsNoTracking().ToListAsync(cancellationToken))
            .Select(product => new ProductMatchCandidate(product.Id, product.Name))
            .ToList();

    /// <summary>
    /// The product's costing repairs (issue #359), effective at or before <paramref name="asOf"/>
    /// when one is given. Always untracked, whatever the caller asked for: a repair record is
    /// append-only, so the replay reads it and never writes anything back to it.
    /// </summary>
    private async Task<List<InventoryCostRepair>> LoadRepairsAsync(
        long productId,
        DateTime? asOf,
        CancellationToken cancellationToken)
    {
        var query = _db.InventoryCostRepairs.AsNoTracking().Where(x => x.ProductId == productId);
        if (asOf.HasValue)
            query = query.Where(x => x.EffectiveAt <= asOf.Value);
        return await query.ToListAsync(cancellationToken);
    }

    public void StageReplay(
        InventoryCostLedger ledger,
        IReadOnlyCollection<CostReplayAdjustmentOutcome> adjustments,
        IReadOnlyCollection<CostReplaySaleCost> recostedSales)
    {
        var efLedger = AsEfLedger(ledger);
        foreach (var outcome in adjustments)
        {
            var adjustment = efLedger.AdjustmentFor(outcome.Adjustment);
            adjustment.QuantityAfter = outcome.QuantityAfter;
            adjustment.CostingQuantityAfter = outcome.CostingQuantityAfter;
            adjustment.InventoryValueAfter = outcome.InventoryValueAfter;
            adjustment.AverageUnitCostAfter = outcome.AverageUnitCostAfter;
            if (outcome.AssignedUnitCost.HasValue)
                adjustment.UnitCost = outcome.AssignedUnitCost;
            if (outcome.AssignedTotalCost.HasValue)
                adjustment.TotalCost = outcome.AssignedTotalCost;
        }

        foreach (var saleCost in recostedSales)
        {
            var sale = efLedger.SaleFor(saleCost.Sale);
            sale.UnitCostAtSale = saleCost.UnitCost;
            sale.CostOfGoodsSold = saleCost.UnitCost;
            sale.CostingStatus = SaleCostingStatus.Costed;
            sale.CostSource = SaleCostSource.InventoryLedger;
        }
    }

    public void StageProductPosition(InventoryCostLedger ledger, ProductCostPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);

        var product = AsEfLedger(ledger).ProductEntity;
        // A null physical quantity means the use case has no authority over physical stock (issue
        // #359): the stored value stays, exactly as a null assigned movement cost leaves one alone.
        if (position.PhysicalQuantity.HasValue)
            product.QuantityInStock = position.PhysicalQuantity.Value;
        product.CostingQuantity = position.CostingQuantity;
        product.InventoryValue = position.InventoryValue;
        product.AverageUnitCost = position.AverageUnitCost;
        product.UpdatedAt = DateTime.UtcNow;
    }

    private static EfInventoryCostLedger AsEfLedger(InventoryCostLedger ledger) =>
        ledger as EfInventoryCostLedger
        ?? throw new ArgumentException("The ledger was not loaded by this store.", nameof(ledger));

    private sealed class EfInventoryCostLedger : InventoryCostLedger
    {
        private readonly Dictionary<CostReplayAdjustment, StockAdjustment> _adjustments;
        private readonly Dictionary<CostReplaySale, NayaxSales> _sales;

        private EfInventoryCostLedger(
            Product product,
            List<CostReplayAdjustment> adjustmentInputs,
            Dictionary<CostReplayAdjustment, StockAdjustment> adjustments,
            List<CostReplaySale> saleInputs,
            Dictionary<CostReplaySale, NayaxSales> sales,
            List<CostReplayRepair> repairInputs,
            InventoryCostTransitionBaseline? baseline)
            : base(
                new CostReplayProduct(product.Id, product.QuantityInStock, product.CostingQuantity, product.InventoryValue),
                adjustmentInputs,
                saleInputs,
                repairInputs,
                baseline is null
                    ? null
                    : new CostReplayBaseline(baseline.CutoffAt, baseline.HomeStockQuantity, baseline.OpeningCostingQuantity, baseline.InventoryValue))
        {
            ProductEntity = product;
            _adjustments = adjustments;
            _sales = sales;
        }

        public Product ProductEntity { get; }

        public static EfInventoryCostLedger Create(
            Product product,
            IReadOnlyCollection<StockAdjustment> adjustments,
            IReadOnlyCollection<NayaxSales> sales,
            IReadOnlyCollection<InventoryCostRepair> repairs,
            InventoryCostTransitionBaseline? baseline)
        {
            var adjustmentInputs = new List<CostReplayAdjustment>(adjustments.Count);
            var adjustmentsByInput = new Dictionary<CostReplayAdjustment, StockAdjustment>(adjustments.Count, ReferenceEqualityComparer.Instance);
            foreach (var adjustment in adjustments)
            {
                var input = new CostReplayAdjustment(
                    adjustment.Id,
                    adjustment.EffectiveAt,
                    (DomainStock.StockAdjustmentReason)adjustment.Reason,
                    adjustment.QuantityChange,
                    adjustment.UnitCost,
                    adjustment.ReceiptItemId.HasValue);
                adjustmentInputs.Add(input);
                adjustmentsByInput.Add(input, adjustment);
            }

            var saleInputs = new List<CostReplaySale>(sales.Count);
            var salesByInput = new Dictionary<CostReplaySale, NayaxSales>(sales.Count, ReferenceEqualityComparer.Instance);
            foreach (var sale in sales)
            {
                var input = new CostReplaySale(sale.TransactionID, sale.MachineAuthorizationTime);
                saleInputs.Add(input);
                salesByInput.Add(input, sale);
            }

            var repairInputs = repairs
                .Select(repair => new CostReplayRepair(repair.Id, repair.EffectiveAt, repair.Quantity, repair.UnitCost))
                .ToList();

            return new EfInventoryCostLedger(
                product, adjustmentInputs, adjustmentsByInput, saleInputs, salesByInput, repairInputs, baseline);
        }

        public StockAdjustment AdjustmentFor(CostReplayAdjustment input) => _adjustments[input];

        public NayaxSales SaleFor(CostReplaySale input) => _sales[input];
    }
}
