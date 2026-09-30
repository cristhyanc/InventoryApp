using InventoryApi.Data;
using InventoryApi.DTOs;
using Inventory.Application.Products;
using Inventory.Application.Reorder;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Services;

/// <summary>
/// <see cref="Create"/>/<see cref="Update"/>/<see cref="Delete"/> delegate to the migrated
/// <see cref="CreateProduct"/>/<see cref="UpdateProduct"/>/<see cref="DeleteProduct"/> use cases
/// (issue #240), preserving this service's existing <see cref="IProductService"/> contract and
/// exception/return shapes exactly. <see cref="GetAll"/>/<see cref="Get"/>/<see cref="LowStock"/>
/// stay here: they return the EF <see cref="Product"/> entity directly (the API's existing response
/// shape) and its reorder formulas (<see cref="Product.NeedToOrder"/>/<see cref="Product.IsReorderAlert"/>)
/// remain future work (see <c>docs/architecture.md</c> backend migration track item 6).
/// </summary>
public class ProductService : IProductService
{
    private readonly AppDbContext _db;
    private readonly CalculateReorderNeeds _calculateReorderNeeds;
    private readonly CreateProduct _createProduct;
    private readonly UpdateProduct _updateProduct;
    private readonly DeleteProduct _deleteProduct;

    public ProductService(
        AppDbContext db,
        CalculateReorderNeeds calculateReorderNeeds,
        CreateProduct createProduct,
        UpdateProduct updateProduct,
        DeleteProduct deleteProduct)
    {
        _db = db;
        _calculateReorderNeeds = calculateReorderNeeds;
        _createProduct = createProduct;
        _updateProduct = updateProduct;
        _deleteProduct = deleteProduct;
    }

    public async Task<IEnumerable<Product>> GetAll(
        string? search, long? categoryId, int? supplierId, bool? lowStockOnly, CancellationToken cancellationToken = default)
    {
        var query = _db.Products
            .Include(p => p.Category)
            .Include(p => p.Supplier)
            .Include(p => p.StockAdjustments)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(p => p.Name.Contains(search) || (p.Sku != null && p.Sku.Contains(search)));

        if (categoryId.HasValue) query = query.Where(p => p.CategoryId == categoryId);
        if (supplierId.HasValue) query = query.Where(p => p.SupplierId == supplierId);

        if (lowStockOnly == true) return await LowStock(search, categoryId, supplierId, cancellationToken);
        return await query.OrderBy(p => p.Name).ToListAsync(cancellationToken);
    }

    public async Task<Product?> Get(long id)
    {
        return await _db.Products
            .Include(p => p.Category)
            .Include(p => p.Supplier)
            .Include(p => p.StockAdjustments)
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<IEnumerable<Product>> LowStock(
        string? search = null, long? categoryId = null, int? supplierId = null, CancellationToken cancellationToken = default)
    {
        var query = _db.Products.AsNoTracking()
            .Include(p => p.Category)
            .Include(p => p.Supplier)
            .Include(p => p.StockAdjustments)
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(p => p.Name.Contains(search) || (p.Sku != null && p.Sku.Contains(search)));
        if (categoryId.HasValue) query = query.Where(p => p.CategoryId == categoryId);
        if (supplierId.HasValue) query = query.Where(p => p.SupplierId == supplierId);

        var products = await query.ToListAsync(cancellationToken);

        var reorderNeeds = await _calculateReorderNeeds.Handle(cancellationToken);
        foreach (var product in products)
        {
            product.MachineReplenishmentNeed =
                reorderNeeds.MachineReplenishmentNeedByProductId.GetValueOrDefault(product.Id);
            product.OnOrderQuantity =
                Math.Max(0m, reorderNeeds.OnOrderQuantityByProductId.GetValueOrDefault(product.Id));
        }

        return products.Where(p => p.IsReorderAlert)
            .OrderByDescending(p => p.NeedToOrder)
            .ThenBy(p => p.Name)
            .ToList();
    }

    public async Task<Product> Create(ProductCreateDto dto)
    {
        var fields = new ProductCreateFields(
            dto.Name, dto.Sku, dto.Description, dto.UnitPrice, dto.QuantityInStock,
            dto.LowStockThreshold, dto.RestockTo, dto.Unit, dto.CategoryId, dto.SupplierId,
            dto.IsActive, dto.InitialUnitCost);

        var result = await _createProduct.Handle(fields, CancellationToken.None);
        if (!result.IsValid) throw new InvalidOperationException(result.ValidationError);

        return (await Get(result.ProductId!.Value))!;
    }

    public async Task<bool> Update(long id, ProductUpdateDto dto)
    {
        var fields = new ProductUpdateFields(
            dto.Sku, dto.Description, dto.LowStockThreshold, dto.RestockTo, dto.Unit, dto.SupplierId, dto.IsActive);

        var result = await _updateProduct.Handle(id, fields, CancellationToken.None);
        return result.Outcome switch
        {
            UpdateProductOutcome.Success => true,
            UpdateProductOutcome.NotFound => false,
            _ => throw new InvalidOperationException(result.ValidationError),
        };
    }

    public Task<bool> Delete(long id) => _deleteProduct.Handle(id, CancellationToken.None);
}
