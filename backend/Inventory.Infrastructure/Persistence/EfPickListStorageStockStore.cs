using Inventory.Application.PickList;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IPickListStorageStockStore"/> (issue #221). It lives in
/// Inventory.Infrastructure beside the <see cref="AppDbContext"/> it depends on, following the same
/// pattern as <see cref="EfOutstandingSupplierOrderQuantityStore"/> (issue #309, Persistence 8/8 of
/// #153).
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
