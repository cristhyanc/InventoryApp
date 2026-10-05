using Inventory.Application.PickList;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IPickListStorageStockStore"/> (issue #221). It lives
/// in InventoryApi, not Inventory.Infrastructure, because it depends on <see cref="AppDbContext"/>,
/// which still lives there, following the same pattern as <see cref="EfOutstandingSupplierOrderQuantityStore"/>.
/// <c>AppDbContext</c>'s global tenant query filter scopes this read the same way it scopes every other
/// <c>_db.Products</c> read; no per-call business filter is added here.
/// </summary>
public sealed class EfPickListStorageStockStore : IPickListStorageStockStore
{
    private readonly AppDbContext _db;

    public EfPickListStorageStockStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyDictionary<long, PickListStorageProduct>> GetStorageProductsAsync(
        IReadOnlyCollection<long> productIds, CancellationToken cancellationToken)
    {
        var products = await _db.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.QuantityInStock })
            .ToListAsync(cancellationToken);

        return products.ToDictionary(
            p => p.Id,
            p => new PickListStorageProduct(p.Id, p.Name, p.QuantityInStock));
    }
}
