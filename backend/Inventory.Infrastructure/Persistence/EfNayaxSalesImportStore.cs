using Inventory.Application.Costing;
using Inventory.Application.Imports;
using Inventory.Domain.Providers;
using Inventory.Domain.Reporting.ProductMatching;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="INayaxSalesImportStore"/> (issue #301). It lives in
/// Inventory.Infrastructure beside the <see cref="AppDbContext"/> and the
/// <see cref="NayaxSales"/>/<see cref="Product"/> persistence models it depends on, which moved
/// there in issue #307; this adapter followed them in issue #309 (Persistence 8/8 of #153).
///
/// The queries are the former
/// <c>ImportService.ImportNayaxSalesFromExcelAsync</c>'s: the untracked product catalogue, the
/// existing-transaction lookup by the remote <c>TransactionID</c>, and the affected products'
/// transition baselines, all read through <see cref="AppDbContext"/>'s business query filter with no
/// business predicate of its own. The existing-transaction lookup being tenant-filtered is what
/// keeps a <c>TransactionID</c> two businesses both hold an insert for the importing business rather
/// than an update of somebody else's sale; ownership of a new row is stamped centrally on save.
///
/// <para>Issue #1300 changed one thing about them: that lookup is one pass over the file's
/// transaction identifiers in fixed-size batches, instead of one query per row. It is called inside
/// the import's write-locked transaction (<see cref="EfNayaxSalesImportTransactions"/>), which is
/// what makes a row the Nayax SQS queue reader stored while the file was being read an update here
/// rather than a second insert the unique index refuses.</para>
///
/// Each staged or loaded sale keeps a reference to its row, so <see cref="Update"/> and
/// <see cref="StageCost"/> write onto exactly that tracked entity. It applies no import or costing
/// rule of its own and never saves outside <see cref="SaveChangesAsync"/>.
/// </summary>
public sealed class EfNayaxSalesImportStore : INayaxSalesImportStore
{
    /// <summary>
    /// How many transaction identifiers one stored-sale lookup statement carries (issue #1300). The
    /// import reads what the business already holds in one pass over the file's identifiers rather
    /// than one query per row, and batching that pass keeps each statement a fixed, modest size
    /// whatever the provider does with a long <c>IN</c> list - a file at the row limit is 10
    /// statements instead of 5,000.
    /// </summary>
    private const int LookupBatchSize = 500;

    private readonly AppDbContext _db;

    public EfNayaxSalesImportStore(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<ProductMatchCandidate>> GetProductCandidatesAsync(CancellationToken cancellationToken) =>
        (await _db.Products.AsNoTracking().ToListAsync(cancellationToken))
            .Select(product => new ProductMatchCandidate(product.Id, product.Name))
            .ToList();

    public async Task<IReadOnlyCollection<long>> GetConflictingTransactionIdsAsync(CancellationToken cancellationToken) =>
        await _db.NayaxSaleProductConflicts.AsNoTracking()
            .Select(conflict => conflict.TransactionId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<long, StoredImportedSale>> FindByTransactionIdsAsync(
        IReadOnlyCollection<long> transactionIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transactionIds);

        var stored = new Dictionary<long, StoredImportedSale>(transactionIds.Count);
        foreach (var batch in transactionIds.Distinct().Chunk(LookupBatchSize))
        {
            var existing = await _db.NayaxSales
                .Where(x => batch.Contains(x.TransactionID))
                .ToListAsync(cancellationToken);

            // The costing view the import already used, plus the three imported facts it does not
            // carry and a blank cell in the file must not erase (issue #1298). Read from the same
            // tracked rows, so what the use case compares against is what Update then writes onto.
            foreach (var row in existing)
            {
                stored[row.TransactionID] = new StoredImportedSale(
                    new NayaxCostableSale(row),
                    row.SettlementValue,
                    row.PaymentMethod,
                    row.MachineName);
            }
        }

        return stored;
    }

    public CostableSale Add(ImportedNayaxSale sale)
    {
        ArgumentNullException.ThrowIfNull(sale);

        var added = new NayaxSales
        {
            TransactionID = sale.TransactionId,
            MachineID = sale.MachineId,
            MachineAuthorizationTime = sale.MachineAuthorizationTime,
            TransactionStatusId = sale.TransactionStatusId,
            // The resolved local product and the export's own raw provider id, each in its own
            // column and each said to be what it is (issue #758).
            ProductId = sale.ProductId,
            Provider = ExternalProvider.Nayax,
            NayaxProductId = sale.NayaxProductId,
            ProviderProductIdIsRaw = true,
            MachineName = sale.MachineName,
            SettlementValue = sale.SettlementValue,
            PaymentMethod = sale.PaymentMethod,
            ProductName = sale.ProductName,
            NayaxProductCostPrice = sale.NayaxProductCostPrice,
        };

        _db.NayaxSales.Add(added);
        return new NayaxCostableSale(added);
    }

    public CostableSale Update(CostableSale stored, ImportedNayaxSale sale)
    {
        ArgumentNullException.ThrowIfNull(sale);

        var entity = AsEntity(stored);
        entity.MachineID = sale.MachineId;
        entity.TransactionStatusId = sale.TransactionStatusId;
        entity.ProductId = sale.ProductId;
        entity.Provider = ExternalProvider.Nayax;
        entity.NayaxProductId = sale.NayaxProductId;
        entity.ProviderProductIdIsRaw = true;
        entity.MachineName = sale.MachineName;
        entity.SettlementValue = sale.SettlementValue;
        entity.PaymentMethod = sale.PaymentMethod;
        entity.ProductName = sale.ProductName;
        entity.MachineAuthorizationTime = sale.MachineAuthorizationTime;
        entity.NayaxProductCostPrice = sale.NayaxProductCostPrice;

        // A fresh view over the same row, so the caller costs and classifies the updated facts
        // rather than the ones it read before the overwrite.
        return new NayaxCostableSale(entity);
    }

    public void StageCost(CostableSale sale, SaleCostAssignment cost) => AsEntity(sale).ApplySaleCost(cost);

    public async Task<IReadOnlyDictionary<long, DateTime>> GetTransitionCutoffsAsync(
        IReadOnlyCollection<long> productIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(productIds);

        var ids = productIds as List<long> ?? productIds.ToList();
        return await _db.InventoryCostTransitionBaselines.AsNoTracking()
            .Where(x => ids.Contains(x.ProductId))
            .ToDictionaryAsync(x => x.ProductId, x => x.CutoffAt, cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);

    private static NayaxSales AsEntity(CostableSale sale) =>
        (sale as NayaxCostableSale
            ?? throw new ArgumentException("The sale was not loaded from a NayaxSales row.", nameof(sale)))
        .Entity;
}
