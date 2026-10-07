using Inventory.Application.Gst;
using Inventory.Domain.Gst;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IHistoricalGstClassificationStore"/> (issue #433). It
/// lives in Inventory.Infrastructure beside <see cref="AppDbContext"/> and the
/// <see cref="Purchase"/>/<see cref="PurchaseItem"/>/<see cref="Product"/>/<see cref="Supplier"/>
/// persistence models it reads, like every other persistence adapter since issue #309.
///
/// It calculates nothing and classifies nothing. The load projects raw stored values - amounts,
/// quantities, unit costs, classifications, provenances and configured rules - exactly as
/// <see cref="Inventory.Infrastructure.Reporting.Persistence.EfGstReportFactsProvider"/> does for the
/// GST accounting aid, so the taxable/GST-free/unknown decision, the precedence and every rounding
/// rule stay in <see cref="HistoricalGstClassificationPolicy"/> and
/// <c>Inventory.Domain.Purchases.PurchaseGstPolicy"</c>. The apply persists the states the use case
/// hands it and nothing else: no amount, no unit cost, no costing quantity, no inventory value and no
/// stock movement is written, because GST classification is accounting data only.
///
/// Everything it reads and writes goes through <see cref="AppDbContext"/>'s business query filters
/// and its ownership stamp on save, so there is no business predicate here by design and the adapter
/// cannot see or classify another business's purchase (AGENTS.md § Tenant ownership and data
/// isolation). On a relational provider an apply runs in a database transaction; the EF InMemory
/// provider used by some tests has none.
/// </summary>
public sealed class EfHistoricalGstClassificationStore : IHistoricalGstClassificationStore
{
    private readonly AppDbContext _db;

    public EfHistoricalGstClassificationStore(AppDbContext db) => _db = db;

    public async Task<IHistoricalGstClassificationTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken) =>
        new Transaction(_db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null);

    public async Task<IReadOnlyList<HistoricalGstPurchase>> LoadPurchasesAsync(CancellationToken cancellationToken)
    {
        // A purchase with no supplier, and the defensive case of a line whose product is not
        // visible, project a null rule rather than a value: the absence of configuration is
        // GstRules.None, which is not an explicit GST-free default (AGENTS.md § Product GST rules
        // and supplier defaults).
        var rows = await _db.Receipts.AsNoTracking()
            .Select(purchase => new
            {
                purchase.Id,
                purchase.DeliveryCost,
                purchase.DeliveryGstClassification,
                purchase.DeliveryGstClassificationSource,
                purchase.PackageCost,
                purchase.PackageGstClassification,
                purchase.PackageGstClassificationSource,
                SupplierProductLines = (GstClassification?)purchase.Supplier!.ProductLineGstDefault,
                SupplierDelivery = (GstClassification?)purchase.Supplier.DeliveryGstDefault,
                SupplierPackage = (GstClassification?)purchase.Supplier.PackageGstDefault,
                Lines = purchase.Items
                    .Select(item => new
                    {
                        item.Id,
                        item.ProductId,
                        item.Quantity,
                        item.UnitCost,
                        item.GstClassification,
                        item.GstClassificationSource,
                        ProductRule = (GstClassification?)item.Product!.GstRule,
                    })
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        return rows.ConvertAll(row => new HistoricalGstPurchase(
            row.Id,
            row.DeliveryCost,
            new GstClassificationState(row.DeliveryGstClassification, row.DeliveryGstClassificationSource),
            row.PackageCost,
            new GstClassificationState(row.PackageGstClassification, row.PackageGstClassificationSource),
            new SupplierGstDefaults(
                row.SupplierProductLines ?? GstRules.None,
                row.SupplierDelivery ?? GstRules.None,
                row.SupplierPackage ?? GstRules.None),
            row.Lines.ConvertAll(line => new HistoricalGstPurchaseLine(
                line.Id,
                line.ProductId,
                line.Quantity,
                line.UnitCost,
                new GstClassificationState(line.GstClassification, line.GstClassificationSource),
                line.ProductRule ?? GstRules.None))));
    }

    public async Task<int> ApplyAsync(
        IReadOnlyCollection<HistoricalGstClassificationChange> changes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);

        if (changes.Count == 0)
            return 0;

        var lineIds = changes
            .Where(change => change.Kind == GstComponentKind.ProductLine)
            .Select(change => change.LineId ?? throw MissingLineId(change))
            .ToList();
        var purchaseIds = changes
            .Where(change => change.Kind != GstComponentKind.ProductLine)
            .Select(change => change.PurchaseId)
            .Distinct()
            .ToList();

        // Loaded tracked, inside the caller's transaction, so the writes below are staged on the
        // rows the apply itself read and are covered by the one rollback.
        var lines = await _db.ReceiptItems
            .Where(item => lineIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var purchases = await _db.Receipts
            .Where(purchase => purchaseIds.Contains(purchase.Id))
            .ToDictionaryAsync(purchase => purchase.Id, cancellationToken);

        foreach (var change in changes)
        {
            switch (change.Kind)
            {
                case GstComponentKind.ProductLine:
                    var line = Required(lines, change.LineId ?? throw MissingLineId(change), "purchase line");
                    line.GstClassification = change.Classification;
                    line.GstClassificationSource = change.Source;
                    break;

                case GstComponentKind.DeliveryCharge:
                    var deliveryPurchase = Required(purchases, change.PurchaseId, "purchase");
                    deliveryPurchase.DeliveryGstClassification = change.Classification;
                    deliveryPurchase.DeliveryGstClassificationSource = change.Source;
                    break;

                case GstComponentKind.PackageCharge:
                    var packagePurchase = Required(purchases, change.PurchaseId, "purchase");
                    packagePurchase.PackageGstClassification = change.Classification;
                    packagePurchase.PackageGstClassificationSource = change.Source;
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported GST component kind {change.Kind} in a historical classification change.");
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return changes.Count;
    }

    /// <summary>
    /// The row a validated plan named. It cannot be absent - the plan was recomputed from this
    /// business's own rows inside this transaction - so an absent one is a fail-closed error that
    /// rolls the whole apply back rather than a partially applied classification.
    /// </summary>
    private static TRow Required<TRow>(IReadOnlyDictionary<int, TRow> rows, int id, string what) =>
        rows.TryGetValue(id, out var row)
            ? row
            : throw new InvalidOperationException(
                $"The {what} a previewed GST classification named is no longer available; nothing was classified.");

    private static InvalidOperationException MissingLineId(HistoricalGstClassificationChange change) =>
        new($"A product-line GST classification change for purchase {change.PurchaseId} carries no line id.");

    private sealed class Transaction(IDbContextTransaction? transaction) : IHistoricalGstClassificationTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) =>
            transaction?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

        public ValueTask DisposeAsync() => transaction?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
