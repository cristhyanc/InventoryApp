#nullable enable

using Inventory.Application.Categories;

namespace InventoryApi.Tests.Application.Categories;

/// <summary>
/// In-memory fake of the persistence port, so Application use-case tests exercise orchestration
/// without depending on EF Core or SQLite.
/// </summary>
public sealed class FakeCategoryStore : ICategoryStore
{
    private readonly List<CategoryRecord> _categories = [];

    public FakeCategoryStore(IEnumerable<CategoryRecord>? seed = null)
    {
        if (seed is not null) _categories.AddRange(seed);
    }

    public Task<IReadOnlyList<CategoryRecord>> ListOrderedByNameAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CategoryRecord>>(
            _categories.OrderBy(x => x.Name, StringComparer.Ordinal).ToList());

    public Task<CategoryRecord?> FindByIdAsync(long id, CancellationToken cancellationToken) =>
        Task.FromResult(_categories.SingleOrDefault(x => x.Id == id));
}
