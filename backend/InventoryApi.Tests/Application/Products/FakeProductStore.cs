using Inventory.Application.Products;

namespace InventoryApi.Tests.Application.Products;

/// <summary>
/// In-memory fake of the persistence port, so Application use-case tests exercise orchestration
/// without depending on EF Core or SQLite.
/// </summary>
public sealed class FakeProductStore : IProductStore
{
    private long _nextId = 1;
    private readonly HashSet<long> _productIds = [];

    public ProductCreateFields? LastCreated { get; private set; }
    public ProductUpdateFields? LastUpdated { get; private set; }
    public long? LastUpdatedId { get; private set; }

    public FakeProductStore(IEnumerable<long>? seedProductIds = null)
    {
        if (seedProductIds is not null)
            foreach (var id in seedProductIds) _productIds.Add(id);
    }

    public Task<long> CreateAsync(ProductCreateFields fields, CancellationToken cancellationToken)
    {
        LastCreated = fields;
        var id = _nextId++;
        _productIds.Add(id);
        return Task.FromResult(id);
    }

    public Task<bool> ExistsAsync(long id, CancellationToken cancellationToken) =>
        Task.FromResult(_productIds.Contains(id));

    public Task UpdateAsync(long id, ProductUpdateFields fields, CancellationToken cancellationToken)
    {
        LastUpdatedId = id;
        LastUpdated = fields;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken) =>
        Task.FromResult(_productIds.Remove(id));
}
