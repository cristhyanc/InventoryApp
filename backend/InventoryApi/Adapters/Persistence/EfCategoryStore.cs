using Inventory.Application.Categories;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="ICategoryStore"/>. It still lives in
/// InventoryApi, not Inventory.Infrastructure: <see cref="AppDbContext"/> and
/// <see cref="Inventory.Infrastructure.Models.Category"/> moved there in issue #307, and moving
/// this adapter family after them is
/// Persistence 7/8 and 8/8 of #153.
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
