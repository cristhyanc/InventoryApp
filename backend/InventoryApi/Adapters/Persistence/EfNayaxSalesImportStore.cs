using Inventory.Application.Costing;
using Inventory.Application.Imports;
using Inventory.Domain.Reporting.ProductMatching;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="INayaxSalesImportStore"/> (issue #301). It lives
/// in InventoryApi, not Inventory.Infrastructure, because it depends on <see cref="AppDbContext"/>
/// and the <see cref="NayaxSales"/>/<see cref="Product"/> persistence models, which still live in
/// InventoryApi; move it into Inventory.Infrastructure once they relocate there (issue #153).
///
/// The queries are unchanged from the former
/// <c>ImportService.ImportNayaxSalesFromExcelAsync</c>: the untracked product catalogue, the
/// existing-transaction lookup by the remote <c>TransactionID</c>, and the affected products'
/// transition baselines, all read through <see cref="AppDbContext"/>'s business query filter with no
/// business predicate of its own. The existing-transaction lookup being tenant-filtered is what
/// keeps a <c>TransactionID</c> two businesses both hold an insert for the importing business rather
/// than an update of somebody else's sale; ownership of a new row is stamped centrally on save.
///
/// Each staged or loaded sale keeps a reference to its row, so <see cref="Update"/> and
/// <see cref="StageCost"/> write onto exactly that tracked entity. It applies no import or costing
/// rule of its own and never saves outside <see cref="SaveChangesAsync"/>.
/// </summary>
public sealed class EfNayaxSalesImportStore : INayaxSalesImportStore
{
    private readonly AppDbContext _db;

    public EfNayaxSalesImportStore(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<ProductMatchCandidate>> GetProductCandidatesAsync(CancellationToken cancellationToken) =>
        (await _db.Products.AsNoTracking().ToListAsync(cancellationToken))
            .Select(product => new ProductMatchCandidate(product.Id, product.Name))
            .ToList();

    public async Task<CostableSale?> FindByTransactionIdAsync(long transactionId, CancellationToken cancellationToken)
    {
        var existing = await _db.NayaxSales
            .FirstOrDefaultAsync(x => x.TransactionID == transactionId, cancellationToken);
        return existing is null ? null : new NayaxCostableSale(existing);
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
            NayaxProductId = sale.NayaxProductId,
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
        entity.NayaxProductId = sale.NayaxProductId;
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
