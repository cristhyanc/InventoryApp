using Inventory.Application.SupplierOrders;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="ISupplierOrderStore"/>. It still lives in
/// InventoryApi, not Inventory.Infrastructure, following the same precedent as
/// <c>EfOperatingExpenseStore</c>/<c>EfProductStore</c>: <see cref="AppDbContext"/> and the
/// persistence models it depends on moved there in issue #307, and moving this adapter family after
/// them is Persistence 7/8 and 8/8 of #153.
/// </summary>
public sealed class EfSupplierOrderStore : ISupplierOrderStore
{
    private readonly AppDbContext _db;

    public EfSupplierOrderStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<SupplierOrderRecord>> ListActiveAsync(CancellationToken cancellationToken)
    {
        var orders = await _db.SupplierOrders
            .AsNoTracking()
            .Include(order => order.Supplier)
            .Include(order => order.Lines)
                .ThenInclude(line => line.Product)
            .Where(order => order.Status != SupplierOrderStatus.Cancelled && order.Status != SupplierOrderStatus.Received)
            .OrderBy(order => order.ExpectedDate ?? order.OrderDate)
            .ThenBy(order => order.Id)
            .ToListAsync(cancellationToken);

        return orders.Select(ToRecord).ToList();
    }

    public async Task<SupplierOrderRecord?> FindByIdAsync(int id, CancellationToken cancellationToken)
    {
        var order = await _db.SupplierOrders
            .AsNoTracking()
            .Include(o => o.Supplier)
            .Include(o => o.Lines)
                .ThenInclude(line => line.Product)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        return order is null ? null : ToRecord(order);
    }

    public Task<bool> SupplierExistsAsync(int supplierId, CancellationToken cancellationToken) =>
        _db.Suppliers.AnyAsync(supplier => supplier.Id == supplierId, cancellationToken);

    public async Task<bool> AllProductsExistAsync(IReadOnlyCollection<long> productIds, CancellationToken cancellationToken)
    {
        if (productIds.Count == 0) return true;
        var existingCount = await _db.Products.CountAsync(product => productIds.Contains(product.Id), cancellationToken);
        return existingCount == productIds.Count;
    }

    public async Task<SupplierOrderRecord> CreateAsync(SupplierOrderCreateFields fields, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var order = new SupplierOrder
        {
            SupplierId = fields.SupplierId,
            OrderDate = fields.OrderDate,
            ExpectedDate = fields.ExpectedDate,
            Reference = fields.Reference,
            Notes = fields.Notes,
            CreatedAt = now,
            UpdatedAt = now,
            Lines = fields.Lines.Select(line => new SupplierOrderLine
            {
                ProductId = line.ProductId,
                QuantityOrdered = line.QuantityOrdered,
                UnitPrice = line.UnitPrice,
                Notes = line.Notes,
            }).ToList(),
        };
        _db.SupplierOrders.Add(order);
        await _db.SaveChangesAsync(cancellationToken);

        var created = await _db.SupplierOrders
            .Include(o => o.Supplier)
            .Include(o => o.Lines)
                .ThenInclude(line => line.Product)
            .AsNoTracking()
            .SingleAsync(o => o.Id == order.Id, cancellationToken);
        return ToRecord(created);
    }

    public async Task<bool> CancelAsync(int id, CancellationToken cancellationToken)
    {
        var order = await _db.SupplierOrders.FindAsync([id], cancellationToken);
        if (order is null || order.Status is SupplierOrderStatus.Cancelled or SupplierOrderStatus.Received) return false;

        order.Status = SupplierOrderStatus.Cancelled;
        order.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static SupplierOrderRecord ToRecord(SupplierOrder order) => new(
        order.Id,
        order.BusinessId,
        order.SupplierId,
        order.Supplier is null ? null : ToSupplierRecord(order.Supplier),
        order.OrderDate,
        order.ExpectedDate,
        order.Reference,
        order.Notes,
        (Inventory.Domain.SupplierOrders.SupplierOrderStatus)order.Status,
        order.CreatedAt,
        order.UpdatedAt,
        order.Lines.Select(ToLineRecord).ToList());

    private static SupplierOrderSupplierRecord ToSupplierRecord(Supplier supplier) => new(
        supplier.Id, supplier.Name, supplier.ContactName, supplier.Phone, supplier.Email, supplier.Address);

    private static SupplierOrderLineRecord ToLineRecord(SupplierOrderLine line) => new(
        line.Id,
        line.SupplierOrderId,
        line.ProductId,
        ToProductSummary(line.Product),
        line.QuantityOrdered,
        line.QuantityReceived,
        line.UnitPrice,
        line.Notes);

    private static SupplierOrderProductSummaryRecord ToProductSummary(Product product) => new(
        product.Id, product.Name, product.Sku, product.Description, product.UnitPrice, product.AverageUnitCost,
        product.CostingQuantity, product.InventoryValue, product.QuantityInStock, product.LowStockThreshold,
        product.RestockTo, product.Unit, product.IsActive, product.CreatedAt, product.UpdatedAt,
        product.CategoryId, product.SupplierId);
}
