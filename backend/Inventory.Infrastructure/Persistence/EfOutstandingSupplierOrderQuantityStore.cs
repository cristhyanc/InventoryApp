using Inventory.Application.Reorder;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IOutstandingSupplierOrderQuantityStore"/>. It lives in
/// Inventory.Infrastructure beside the <see cref="AppDbContext"/> it depends on (issue #309,
/// Persistence 8/8 of #153). Query moved unchanged from the legacy
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
