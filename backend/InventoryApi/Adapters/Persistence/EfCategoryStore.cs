using Inventory.Application.Categories;
using InventoryApi.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="ICategoryStore"/>. It lives in InventoryApi,
/// not Inventory.Infrastructure, because it depends on <see cref="AppDbContext"/> and
/// <see cref="Models.Category"/>, which still live in InventoryApi. Move it into
/// Inventory.Infrastructure once the shared AppDbContext and persistence models relocate there.
/// </summary>
public sealed class EfCategoryStore : ICategoryStore
{
    private readonly AppDbContext _db;

    public EfCategoryStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<CategoryRecord>> ListOrderedByNameAsync(CancellationToken cancellationToken) =>
        await _db.Categories
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new CategoryRecord(c.Id, c.Name, c.Description))
            .ToListAsync(cancellationToken);

    public async Task<CategoryRecord?> FindByIdAsync(long id, CancellationToken cancellationToken)
    {
        var entity = await _db.Categories.FindAsync([id], cancellationToken);
        return entity is null ? null : new CategoryRecord(entity.Id, entity.Name, entity.Description);
    }
}
