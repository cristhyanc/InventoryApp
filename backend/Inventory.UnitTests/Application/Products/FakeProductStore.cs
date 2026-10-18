using Inventory.Application.Products;
using Inventory.Domain.Gst;

namespace InventoryApi.Tests.Application.Products;

/// <summary>
/// In-memory fake of the persistence port, so Application use-case tests exercise orchestration
/// without depending on EF Core or SQLite.
///
/// <para>Since issue #736 the port's three write methods report a
/// <see cref="ProductWriteAttempt"/>, because the refusals that depend on stored rows - a
/// provider-managed field, a category of another business, a product history refers to - are decided
/// where that data is. This fake decides none of them: <see cref="RefusalToReturn"/> is how a
/// use-case test makes the store refuse, and what each refusal actually means is proved against the
/// real relational adapter in <c>InventoryApi.Tests.Adapters.Persistence.EfProductStoreTests</c>.</para>
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
    /// When set, every create, update and delete of an existing product is refused with this
    /// message, standing in for a refusal the real adapter decides from stored data.
    /// </summary>
    public string? RefusalToReturn { get; set; }

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

    public Task<ProductWriteAttempt> CreateAsync(
        ProductCreateFields fields, CancellationToken cancellationToken)
    {
        if (RefusalToReturn is { } refusal) return Task.FromResult(ProductWriteAttempt.Refused(refusal));

        LastCreated = fields;
        var id = _nextId++;
        _productIds.Add(id);
        return Task.FromResult(ProductWriteAttempt.Succeeded(id));
    }

    public Task<bool> ExistsAsync(long id, CancellationToken cancellationToken)
    {
        var exists = _productIds.Contains(id);
        AfterExistsAsync?.Invoke(id);
        return Task.FromResult(exists);
    }

    public Task<ProductWriteAttempt> UpdateAsync(
        long id, ProductUpdateFields fields, CancellationToken cancellationToken)
    {
        if (!_productIds.Contains(id)) return Task.FromResult(ProductWriteAttempt.NotFound());
        if (RefusalToReturn is { } refusal) return Task.FromResult(ProductWriteAttempt.Refused(refusal));

        LastUpdatedId = id;
        LastUpdated = fields;
        return Task.FromResult(ProductWriteAttempt.Succeeded(id));
    }

    public Task<ProductWriteAttempt> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        if (!_productIds.Contains(id)) return Task.FromResult(ProductWriteAttempt.NotFound());
        if (RefusalToReturn is { } refusal) return Task.FromResult(ProductWriteAttempt.Refused(refusal));

        _productIds.Remove(id);
        return Task.FromResult(ProductWriteAttempt.Succeeded(id));
    }

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
