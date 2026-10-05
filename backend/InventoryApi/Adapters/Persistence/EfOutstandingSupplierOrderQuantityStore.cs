using Inventory.Application.Reorder;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IOutstandingSupplierOrderQuantityStore"/>. It lives
/// in InventoryApi, not Inventory.Infrastructure, because it depends on <see cref="AppDbContext"/>,
/// which still lives there. Query moved unchanged from the legacy
/// <c>InventoryApi.Services.ProductService.LowStock</c> outstanding-order projection (issue #47).
/// </summary>
public sealed class EfOutstandingSupplierOrderQuantityStore : IOutstandingSupplierOrderQuantityStore
{
    private readonly AppDbContext _db;

    public EfOutstandingSupplierOrderQuantityStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyDictionary<long, decimal>> GetOutstandingQuantitiesByProductAsync(CancellationToken cancellationToken) =>
        await _db.SupplierOrderLines
            .Where(line => line.SupplierOrder.Status != SupplierOrderStatus.Cancelled &&
                           line.SupplierOrder.Status != SupplierOrderStatus.Received)
            .GroupBy(line => line.ProductId)
            .Select(group => new { ProductId = group.Key, Quantity = group.Sum(line => line.QuantityOrdered - line.QuantityReceived) })
            .ToDictionaryAsync(item => item.ProductId, item => item.Quantity, cancellationToken);
}
