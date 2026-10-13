using Inventory.Application.MachineStock;
using Inventory.Domain.MachineStock;
using Inventory.Domain.Nayax;
using Inventory.Domain.Reporting.ProductMatching;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IMachineStockLedgerStore"/> (issue #56), beside the
/// rest of the adapter family and <see cref="AppDbContext"/> (issues #307/#309).
///
/// Every movement it reads is a fact some other flow already persisted: a machine refill or return
/// is a <see cref="StockAdjustment"/> with reason <see cref="StockAdjustmentReason.MachineRefill"/>
/// and this machine recorded on it, and a dispensed unit is a completed <see cref="NayaxSales"/>
/// row. It writes no second copy of either, so the machine ledger cannot disagree with Stock
/// History, and a completed sale cannot be consumed twice.
///
/// A sale is attributed to a product with <see cref="ProductMatcher"/> - the same deterministic
/// matcher <see cref="EfInventoryCostLedgerStore"/> uses to decide which product's costing a sale
/// belongs to - so the machine ledger and the costing ledger always agree about which product a
/// transaction dispensed. Sales that match no catalogue product are counted and reported rather than
/// spread across the products that happen to be in the machine.
///
/// Tenant isolation is the central query filter's, as everywhere else: there is no per-call
/// <c>BusinessId</c> predicate here, which is also what makes the observed product ids safe to pass
/// in - an id from a machine's live report resolves to a name only if the caller's own catalogue
/// holds it.
/// </summary>
public sealed class EfMachineStockLedgerStore : IMachineStockLedgerStore
{
    private readonly AppDbContext _db;

    public EfMachineStockLedgerStore(AppDbContext db) => _db = db;

    public async Task<MachineStockLedgerFacts> GetLedgerFactsAsync(
        long machineId, IReadOnlyCollection<long> observedProductIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observedProductIds);

        var refills = await _db.StockAdjustments
            .AsNoTracking()
            .Where(adjustment => adjustment.MachineId == machineId
                && adjustment.Reason == StockAdjustmentReason.MachineRefill)
            .Select(adjustment => new MachineRefillFact(
                adjustment.Id, adjustment.ProductId, adjustment.QuantityChange, adjustment.EffectiveAt))
            .ToListAsync(cancellationToken);

        var candidates = await _db.Products
            .AsNoTracking()
            .Select(product => new ProductMatchCandidate(product.Id, product.Name))
            .ToListAsync(cancellationToken);

        var saleRows = await _db.NayaxSales
            .AsNoTracking()
            .Where(EfNayaxSalesQueries.CompletedSalePredicate)
            .Where(sale => sale.MachineID == machineId)
            .Select(sale => new { sale.TransactionID, sale.NayaxProductId, sale.ProductName, sale.MachineAuthorizationTime })
            .ToListAsync(cancellationToken);

        var attributedSales = saleRows
            .Select(sale => new
            {
                sale.TransactionID,
                sale.MachineAuthorizationTime,
                ProductId = ProductMatcher.Match(candidates, sale.NayaxProductId, sale.ProductName),
            })
            .ToList();
        var sales = attributedSales
            .Where(sale => sale.ProductId.HasValue)
            .Select(sale => new MachineSaleFact(
                sale.TransactionID, sale.ProductId!.Value, sale.MachineAuthorizationTime))
            .ToList();
        var unattributedSaleCount = attributedSales.Count - sales.Count;

        var counts = await _db.MachineStockCounts
            .AsNoTracking()
            .Where(count => count.MachineId == machineId)
            .Select(count => new MachineStockCountFact(
                count.Id, count.ProductId, count.CountedQuantity, count.CountedAtUtc))
            .ToListAsync(cancellationToken);

        // The opening machine stock the inventory-cost transition recorded for this machine
        // (issue #298). It is the only other persisted statement of what a machine was holding, and
        // it is used only where no later count exists.
        var openingBaselines = await _db.InventoryCostTransitionMachineStocks
            .AsNoTracking()
            .Where(machineStock => machineStock.MachineId == machineId)
            .Select(machineStock => new MachineStockOpeningBaselineFact(
                machineStock.InventoryCostTransitionBaseline!.ProductId,
                machineStock.StockQuantity,
                machineStock.InventoryCostTransitionBaseline!.CutoffAt))
            .ToListAsync(cancellationToken);

        var pendingEventCount = await _db.NayaxMachineStockEvents
            .AsNoTracking()
            .Where(stockEvent => stockEvent.MachineId == machineId
                && stockEvent.ProcessingStatus == NayaxStockEventProcessingStatus.Unprocessed)
            .CountAsync(cancellationToken);

        var referencedProductIds = refills.Select(refill => refill.ProductId)
            .Concat(sales.Select(sale => sale.ProductId))
            .Concat(counts.Select(count => count.ProductId))
            .Concat(openingBaselines.Select(baseline => baseline.ProductId))
            .Concat(observedProductIds)
            .Distinct()
            .ToList();
        var productNames = await _db.Products
            .AsNoTracking()
            .Where(product => referencedProductIds.Contains(product.Id))
            .Select(product => new { product.Id, product.Name })
            .ToDictionaryAsync(product => product.Id, product => product.Name, cancellationToken);

        return new MachineStockLedgerFacts(
            refills, sales, counts, openingBaselines, productNames, pendingEventCount, unattributedSaleCount);
    }

    public async Task<MachineStockCountRecord?> RecordCountAsync(
        long machineId,
        long productId,
        int countedQuantity,
        DateTime countedAtUtc,
        string? notes,
        CancellationToken cancellationToken)
    {
        var product = await _db.Products
            .AsNoTracking()
            .Where(candidate => candidate.Id == productId)
            .Select(candidate => new { candidate.Id, candidate.Name })
            .FirstOrDefaultAsync(cancellationToken);
        if (product is null)
            return null;

        var count = new MachineStockCount
        {
            MachineId = machineId,
            ProductId = productId,
            CountedQuantity = countedQuantity,
            CountedAtUtc = countedAtUtc,
            Notes = notes,
        };
        _db.MachineStockCounts.Add(count);
        await _db.SaveChangesAsync(cancellationToken);

        return new MachineStockCountRecord(
            count.Id, machineId, productId, product.Name, countedQuantity, countedAtUtc, notes);
    }
}
