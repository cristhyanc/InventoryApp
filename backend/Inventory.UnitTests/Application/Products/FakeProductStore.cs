using Inventory.Application.Products;
using Inventory.Domain.Gst;

namespace InventoryApi.Tests.Application.Products;

/// <summary>
/// In-memory fake of the persistence port, so Application use-case tests exercise orchestration
/// without depending on EF Core or SQLite.
/// </summary>
public sealed class FakeProductStore : IProductStore
{
    private long _nextId = 1;
    private readonly HashSet<long> _productIds = [];

    /// <summary>The GST rule stored per product, absent until something sets one (issue #430).</summary>
    private readonly Dictionary<long, GstClassification> _gstRules = [];

    public ProductCreateFields? LastCreated { get; private set; }
    public ProductUpdateFields? LastUpdated { get; private set; }
    public long? LastUpdatedId { get; private set; }

    /// <summary>
    /// Runs immediately after an <see cref="ExistsAsync"/> answer is produced, so a test can make a
    /// concurrent change - deleting the product, for example - land deterministically in the window
    /// between a use case's existence check and its write.
    /// </summary>
    public Action<long>? AfterExistsAsync { get; set; }

    public FakeProductStore(IEnumerable<long>? seedProductIds = null)
    {
        if (seedProductIds is not null)
            foreach (var id in seedProductIds) _productIds.Add(id);
    }

    public void Remove(long id) => _productIds.Remove(id);

    public Task<long> CreateAsync(ProductCreateFields fields, CancellationToken cancellationToken)
    {
        LastCreated = fields;
        var id = _nextId++;
        _productIds.Add(id);
        return Task.FromResult(id);
    }

    public Task<bool> ExistsAsync(long id, CancellationToken cancellationToken)
    {
        var exists = _productIds.Contains(id);
        AfterExistsAsync?.Invoke(id);
        return Task.FromResult(exists);
    }

    public Task<bool> UpdateAsync(long id, ProductUpdateFields fields, CancellationToken cancellationToken)
    {
        if (!_productIds.Contains(id)) return Task.FromResult(false);

        LastUpdatedId = id;
        LastUpdated = fields;
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken) =>
        Task.FromResult(_productIds.Remove(id));

    public Task<GstClassification?> FindGstRuleAsync(long id, CancellationToken cancellationToken) =>
        Task.FromResult<GstClassification?>(
            _productIds.Contains(id)
                ? _gstRules.TryGetValue(id, out var rule) ? rule : GstRules.None
                : null);

    public Task<bool> SetGstRuleAsync(long id, GstClassification rule, CancellationToken cancellationToken)
    {
        if (!_productIds.Contains(id)) return Task.FromResult(false);

        _gstRules[id] = rule;
        return Task.FromResult(true);
    }
}
