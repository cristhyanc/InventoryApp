using Inventory.Application.SupplierOrders;
using Inventory.Domain.SupplierOrders;

namespace InventoryApi.Tests.Application.SupplierOrders;

/// <summary>
/// In-memory fake of the persistence port, so Application use-case tests exercise orchestration
/// without depending on EF Core or SQLite. See <c>EfSupplierOrderStoreTests</c> for behaviour that
/// needs a real query.
/// </summary>
public sealed class FakeSupplierOrderStore : ISupplierOrderStore
{
    private readonly Dictionary<int, SupplierOrderRecord> _records = [];
    private int _nextId = 1;

    public bool SupplierExists { get; set; } = true;
    public bool AllProductsExist { get; set; } = true;
    public SupplierOrderRecord? LastCreated { get; private set; }

    public Task<IReadOnlyList<SupplierOrderRecord>> ListActiveAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SupplierOrderRecord>>(_records.Values
            .Where(order => order.Status is not (SupplierOrderStatus.Cancelled or SupplierOrderStatus.Received))
            .ToList());

    public Task<SupplierOrderRecord?> FindByIdAsync(int id, CancellationToken cancellationToken) =>
        Task.FromResult(_records.GetValueOrDefault(id));

    public Task<bool> SupplierExistsAsync(int supplierId, CancellationToken cancellationToken) => Task.FromResult(SupplierExists);

    public Task<bool> AllProductsExistAsync(IReadOnlyCollection<long> productIds, CancellationToken cancellationToken) =>
        Task.FromResult(AllProductsExist);

    public Task<SupplierOrderRecord> CreateAsync(SupplierOrderCreateFields fields, CancellationToken cancellationToken)
    {
        var id = _nextId++;
        var now = DateTime.UtcNow;
        var record = new SupplierOrderRecord(
            id, 1, fields.SupplierId, null, fields.OrderDate, fields.ExpectedDate, fields.Reference, fields.Notes,
            SupplierOrderStatus.Ordered, now, now,
            fields.Lines.Select(line => new SupplierOrderLineRecord(
                0, id, line.ProductId,
                new SupplierOrderProductSummaryRecord(line.ProductId, "Product", null, null, 0, 0, null, null, 0, 0, 0, null, true, now, now, null, null),
                line.QuantityOrdered, 0, line.UnitPrice, line.Notes)).ToList());
        _records[id] = record;
        LastCreated = record;
        return Task.FromResult(record);
    }

    public Task<bool> CancelAsync(int id, CancellationToken cancellationToken)
    {
        if (!_records.TryGetValue(id, out var existing) || existing.Status is SupplierOrderStatus.Cancelled or SupplierOrderStatus.Received)
            return Task.FromResult(false);

        _records[id] = existing with { Status = SupplierOrderStatus.Cancelled };
        return Task.FromResult(true);
    }
}
