using Inventory.Application.Products;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using DomainStock = Inventory.Domain.Stock;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IProductCatalogStore"/>. It still lives in
/// InventoryApi, not Inventory.Infrastructure, following the same precedent as
/// <see cref="EfCategoryStore"/>/<see cref="EfProductStore"/>: <see cref="AppDbContext"/> and
/// <see cref="Product"/> moved there in issue #307, and moving this adapter family after them is
/// Persistence 7/8 and 8/8 of #153.
///
/// Tenant scoping is the central <see cref="AppDbContext"/> global query filter, never a predicate
/// here (AGENTS.md § Tenant ownership and data isolation). The category/supplier/stock-adjustment
/// detail is loaded with explicit <c>Include</c>s rather than lazily (issue #52), so a caller still
/// sees it after this context is disposed.
/// </summary>
public sealed class EfProductCatalogStore : IProductCatalogStore
{
    private readonly AppDbContext _db;

    public EfProductCatalogStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ProductRecord>> ListOrderedByNameAsync(
        ProductCatalogFilter filter, CancellationToken cancellationToken) =>
        ToRecords(await Filtered(_db.Products, filter)
            .OrderBy(product => product.Name)
            .ToListAsync(cancellationToken));

    public async Task<IReadOnlyList<ProductRecord>> ListUnorderedAsync(
        ProductCatalogFilter filter, CancellationToken cancellationToken) =>
        ToRecords(await Filtered(_db.Products.AsNoTracking(), filter).ToListAsync(cancellationToken));

    public async Task<ProductRecord?> FindAsync(long id, CancellationToken cancellationToken)
    {
        var product = await WithDetail(_db.Products)
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        return product is null ? null : ToRecord(product);
    }

    private static IQueryable<Product> Filtered(IQueryable<Product> products, ProductCatalogFilter filter)
    {
        var query = WithDetail(products);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search;
            query = query.Where(product => product.Name.Contains(search)
                || (product.Sku != null && product.Sku.Contains(search)));
        }

        if (filter.CategoryId.HasValue) query = query.Where(product => product.CategoryId == filter.CategoryId);
        if (filter.SupplierId.HasValue) query = query.Where(product => product.SupplierId == filter.SupplierId);

        return query;
    }

    private static IQueryable<Product> WithDetail(IQueryable<Product> products) =>
        products
            .Include(product => product.Category)
            .Include(product => product.Supplier)
            .Include(product => product.StockAdjustments);

    private static IReadOnlyList<ProductRecord> ToRecords(IEnumerable<Product> products) =>
        products.Select(ToRecord).ToList();

    private static ProductRecord ToRecord(Product product) => new()
    {
        Id = product.Id,
        Name = product.Name,
        Sku = product.Sku,
        Description = product.Description,
        UnitPrice = product.UnitPrice,
        AverageUnitCost = product.AverageUnitCost,
        CostingQuantity = product.CostingQuantity,
        InventoryValue = product.InventoryValue,
        QuantityInStock = product.QuantityInStock,
        LowStockThreshold = product.LowStockThreshold,
        RestockTo = product.RestockTo,
        Unit = product.Unit,
        IsActive = product.IsActive,
        CreatedAt = product.CreatedAt,
        UpdatedAt = product.UpdatedAt,
        CategoryId = product.CategoryId,
        Category = product.Category is null
            ? null
            : new ProductCategoryRecord(product.Category.Id, product.Category.Name, product.Category.Description),
        SupplierId = product.SupplierId,
        Supplier = product.Supplier is null
            ? null
            : new ProductSupplierRecord(
                product.Supplier.Id,
                product.Supplier.Name,
                product.Supplier.ContactName,
                product.Supplier.Phone,
                product.Supplier.Email,
                product.Supplier.Address),
        StockAdjustments = product.StockAdjustments.Select(adjustment => new ProductStockAdjustmentRecord(
            adjustment.Id,
            adjustment.ProductId,
            adjustment.ReceiptItemId,
            adjustment.QuantityChange,
            adjustment.QuantityAfter,
            adjustment.UnitCost,
            adjustment.TotalCost,
            adjustment.CostingQuantityAfter,
            adjustment.AverageUnitCostAfter,
            adjustment.InventoryValueAfter,
            (DomainStock.StockAdjustmentReason)adjustment.Reason,
            (DomainStock.StockAdjustmentSource)adjustment.Source,
            adjustment.MachineId,
            adjustment.Notes,
            adjustment.EatBefore,
            adjustment.CreatedAt,
            adjustment.EffectiveAt)).ToList(),
    };
}
