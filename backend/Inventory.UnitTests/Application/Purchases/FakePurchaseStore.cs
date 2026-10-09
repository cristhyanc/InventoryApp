using Inventory.Application.Purchases;
using Inventory.Domain.Purchases;

namespace InventoryApi.Tests.Application.Purchases;

/// <summary>
/// In-memory fake of the persistence port, so Application use-case tests exercise orchestration
/// without depending on EF Core or SQLite. See <c>EfPurchaseStoreTests</c> for behaviour that needs
/// a real transaction/query.
/// </summary>
public sealed class FakePurchaseStore : IPurchaseStore
{
    private readonly Dictionary<int, PurchaseRecord> _records = [];
    private int _nextId = 1;

    public bool SupplierExists { get; set; } = true;
    public bool AllProductsExist { get; set; } = true;
    public (long ProductId, DateTime CutoffAt)? ConflictingBaseline { get; set; }
    public bool ThrowOnCreate { get; set; }
    public PurchaseRecord? LastCreated { get; private set; }

    /// <summary>
    /// The effective purchase date the use case checked against the cost-transition baselines, so a
    /// test can see the date the validation used and not only the date that was persisted.
    /// </summary>
    public DateTime? LastConflictCheckedPurchaseDate { get; private set; }

    public Task<IReadOnlyList<PurchaseRecord>> ListAsync(int? supplierId, CancellationToken cancellationToken)
    {
        IEnumerable<PurchaseRecord> query = _records.Values;
        if (supplierId.HasValue) query = query.Where(x => x.SupplierId == supplierId);
        return Task.FromResult<IReadOnlyList<PurchaseRecord>>(query.ToList());
    }

    public Task<PurchaseRecord?> FindByIdAsync(int id, CancellationToken cancellationToken) =>
        Task.FromResult(_records.GetValueOrDefault(id));

    public Task<PurchaseFileMetadata?> FindFileMetadataAsync(int id, CancellationToken cancellationToken) =>
        Task.FromResult(_records.TryGetValue(id, out var record)
            ? new PurchaseFileMetadata(record.StoredFileName, record.ContentType, record.FileName, record.FileSizeBytes)
            : null);

    public Task<bool> SupplierExistsAsync(int supplierId, CancellationToken cancellationToken) => Task.FromResult(SupplierExists);

    public Task<bool> AllProductsExistAsync(IReadOnlyCollection<long> productIds, CancellationToken cancellationToken) =>
        Task.FromResult(AllProductsExist);

    public Task<(long ProductId, DateTime CutoffAt)?> FindConflictingCostTransitionBaselineAsync(
        IReadOnlyCollection<long> productIds, DateTime purchaseDate, CancellationToken cancellationToken)
    {
        LastConflictCheckedPurchaseDate = purchaseDate;
        return Task.FromResult(ConflictingBaseline);
    }

    public Task<PurchaseRecord> CreateAsync(
        PurchaseFields fields, IReadOnlyList<PurchaseItemInput> items, PurchaseFileMetadata file, CancellationToken cancellationToken)
    {
        if (ThrowOnCreate) throw new InvalidOperationException("store failure");

        var id = _nextId++;
        // The GST classifications are resolved by the same Domain rules the EF store uses, so a use-case
        // test sees the states a real create would persist (issue #429).
        var delivery = PurchaseGstPolicy.ClassifyCharge(
            fields.DeliveryCost, PurchaseGstPolicy.Classify(fields.DeliveryGstClassification));
        var package = PurchaseGstPolicy.ClassifyCharge(
            fields.PackageCost, PurchaseGstPolicy.Classify(fields.PackageGstClassification));
        var record = new PurchaseRecord(
            id, 1, fields.Title ?? string.Empty, fields.Notes, fields.TotalAmount,
            fields.DeliveryCost, delivery.Classification, delivery.Source,
            fields.PackageCost, package.Classification, package.Source,
            fields.PurchaseDate ?? DateTime.UtcNow, fields.SupplierId, null,
            items.Select(item =>
            {
                var state = PurchaseGstPolicy.Classify(item.GstClassification);
                return new PurchaseItemRecord(
                    0, id, item.ProductId, item.Quantity, item.UnitCost, state.Classification, state.Source, null);
            }).ToList(),
            file.FileName, file.StoredFileName, file.ContentType, file.FileSizeBytes, DateTime.UtcNow);
        _records[id] = record;
        LastCreated = record;
        return Task.FromResult(record);
    }

    public Task<PurchaseRecord?> UpdateAsync(
        int id, PurchaseFields fields, IReadOnlyList<PurchaseItemInput>? items, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not needed by these tests; see EfPurchaseStoreTests for Update behaviour.");

    public Task<string?> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        if (!_records.Remove(id, out var existing)) return Task.FromResult<string?>(null);
        return Task.FromResult<string?>(existing.StoredFileName);
    }
}
