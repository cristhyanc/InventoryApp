#nullable enable

using Inventory.Application.Categories;

namespace InventoryApi.Tests.Application.Categories;

/// <summary>
/// In-memory fake of the persistence port, so Application use-case tests exercise orchestration
/// without depending on EF Core or SQLite.
///
/// <para>Since issue #736 the port writes as well as reads. The refusals that depend on stored rows
/// - a duplicate name, a provider-managed name, a category products still use - are decided by the
/// real adapter and are proved against the relational schema in
/// <c>InventoryApi.Tests.Adapters.Persistence.EfCategoryStoreTests</c>; here
/// <see cref="RefusalToReturn"/> stands in for whichever one a use-case test needs.</para>
/// </summary>
public sealed class FakeCategoryStore : ICategoryStore
{
    private readonly List<CategoryRecord> _categories = [];
    private long _nextId = 1;

    public FakeCategoryStore(IEnumerable<CategoryRecord>? seed = null)
    {
        if (seed is not null) _categories.AddRange(seed);
        if (_categories.Count > 0) _nextId = _categories.Max(category => category.Id) + 1;
    }

    /// <summary>When set, every write is refused with this message.</summary>
    public string? RefusalToReturn { get; set; }

    public CategoryWriteFields? LastWritten { get; private set; }

    public long? LastWrittenId { get; private set; }

    public Task<IReadOnlyList<CategoryRecord>> ListOrderedByNameAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CategoryRecord>>(
            _categories.OrderBy(x => x.Name, StringComparer.Ordinal).ToList());

    public Task<CategoryRecord?> FindByIdAsync(long id, CancellationToken cancellationToken) =>
        Task.FromResult(_categories.SingleOrDefault(x => x.Id == id));

    public Task<CategoryWriteAttempt> CreateAsync(
        CategoryWriteFields fields, CancellationToken cancellationToken)
    {
        if (RefusalToReturn is { } refusal) return Task.FromResult(CategoryWriteAttempt.Refused(refusal));

        LastWritten = fields;
        var created = new CategoryRecord(_nextId++, fields.Name!, fields.Description);
        _categories.Add(created);
        LastWrittenId = created.Id;
        return Task.FromResult(CategoryWriteAttempt.Succeeded(created.Id));
    }

    public Task<CategoryWriteAttempt> UpdateAsync(
        long id, CategoryWriteFields fields, CancellationToken cancellationToken)
    {
        var existing = _categories.SingleOrDefault(x => x.Id == id);
        if (existing is null) return Task.FromResult(CategoryWriteAttempt.NotFound());
        if (RefusalToReturn is { } refusal) return Task.FromResult(CategoryWriteAttempt.Refused(refusal));

        _categories.Remove(existing);
        _categories.Add(existing with { Name = fields.Name!, Description = fields.Description });
        LastWritten = fields;
        LastWrittenId = id;
        return Task.FromResult(CategoryWriteAttempt.Succeeded(id));
    }

    public Task<CategoryWriteAttempt> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var existing = _categories.SingleOrDefault(x => x.Id == id);
        if (existing is null) return Task.FromResult(CategoryWriteAttempt.NotFound());
        if (RefusalToReturn is { } refusal) return Task.FromResult(CategoryWriteAttempt.Refused(refusal));

        _categories.Remove(existing);
        LastWrittenId = id;
        return Task.FromResult(CategoryWriteAttempt.Succeeded(id));
    }
}
