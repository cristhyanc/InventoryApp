using InventoryApi.Adapters.Mapping;
using InventoryApi.DTOs;
using Inventory.Application.Products;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;

namespace InventoryApi.Services;

/// <summary>
/// A transitional delegator only (issue #240, the same shape #241 left <see cref="SiteService"/>/
/// <see cref="MachineService"/> in): every method maps the <see cref="IProductService"/> request onto
/// the migrated <see cref="Inventory.Application.Products"/> use case that owns it, and maps the
/// result back to the unchanged <see cref="Product"/> API response through
/// <see cref="ProductResponseMapper"/>. It holds no <c>AppDbContext</c>, no query, and no rule of its
/// own. Deleting it, and with it <see cref="IProductService"/>, is tracked by issue #153.
/// </summary>
public class ProductService : IProductService
{
    private readonly ListProducts _listProducts;
    private readonly GetProduct _getProduct;
    private readonly ListLowStockProducts _listLowStockProducts;
    private readonly CreateProduct _createProduct;
    private readonly UpdateProduct _updateProduct;
    private readonly DeleteProduct _deleteProduct;

    public ProductService(
        ListProducts listProducts,
        GetProduct getProduct,
        ListLowStockProducts listLowStockProducts,
        CreateProduct createProduct,
        UpdateProduct updateProduct,
        DeleteProduct deleteProduct)
    {
        _listProducts = listProducts;
        _getProduct = getProduct;
        _listLowStockProducts = listLowStockProducts;
        _createProduct = createProduct;
        _updateProduct = updateProduct;
        _deleteProduct = deleteProduct;
    }

    public async Task<IEnumerable<Product>> GetAll(
        string? search, long? categoryId, int? supplierId, bool? lowStockOnly, CancellationToken cancellationToken = default)
    {
        var records = await _listProducts.Handle(
            new ProductCatalogFilter(search, categoryId, supplierId), lowStockOnly, cancellationToken);
        return records.Select(ProductResponseMapper.ToProduct).ToList();
    }

    public async Task<Product?> Get(long id)
    {
        var record = await _getProduct.Handle(id, CancellationToken.None);
        return record is null ? null : ProductResponseMapper.ToProduct(record);
    }

    public async Task<IEnumerable<Product>> LowStock(
        string? search = null, long? categoryId = null, int? supplierId = null, CancellationToken cancellationToken = default)
    {
        var records = await _listLowStockProducts.Handle(
            new ProductCatalogFilter(search, categoryId, supplierId), cancellationToken);
        return records.Select(ProductResponseMapper.ToProduct).ToList();
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
