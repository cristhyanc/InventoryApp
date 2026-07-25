using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Services;

public class ProductService : IProductService
{
    private readonly AppDbContext _db;
    private readonly INayaxLynxClient _nayaxLynxClient;

    public ProductService(AppDbContext db, INayaxLynxClient nayaxLynxClient)
    {
        _db = db;
        _nayaxLynxClient = nayaxLynxClient;
    }

    public async Task<IEnumerable<Product>> GetAll(string? search, long? categoryId, int? supplierId, bool? lowStockOnly)
    {
        var query = _db.Products
            .Include(p => p.Category)
            .Include(p => p.Supplier)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(p => p.Name.Contains(search) || (p.Sku != null && p.Sku.Contains(search)));

        if (categoryId.HasValue) query = query.Where(p => p.CategoryId == categoryId);
        if (supplierId.HasValue) query = query.Where(p => p.SupplierId == supplierId);
        if (lowStockOnly == true) query = query.Where(p => p.QuantityInStock <= p.LowStockThreshold);

        return await query.OrderBy(p => p.Name).ToListAsync();
    }

    public async Task<Product?> Get(long id)
    {
        return await _db.Products
            .Include(p => p.Category)
            .Include(p => p.Supplier)
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<IEnumerable<Product>> LowStock()
    {
        return await _db.Products
            .Include(p => p.Category)
            .Where(p => p.IsActive && p.QuantityInStock <= p.LowStockThreshold)
            .OrderBy(p => p.QuantityInStock)
            .ToListAsync();
    }

    public async Task<bool> ImportProductsAsync()
    {
        var productsTask = _nayaxLynxClient.GetProductsAsync();
        var groupsTask = _nayaxLynxClient.GetProductGroupssAsync();
        var localProductsTask = _db.Products.ToListAsync();
        var localCatsTask = _db.Categories.ToListAsync();

        await Task.WhenAll(productsTask, groupsTask, localProductsTask, localCatsTask);

        var nayaxProducts = await productsTask;
        var nayaxGroups = await groupsTask;
        var localProducts = await localProductsTask;
        var localCategories = await localCatsTask;

        var newProducts = new List<Product>();
        var newCategories = new List<Category>();

        foreach (var group in nayaxGroups)
        {
            var category = localCategories.Where(x => x.Id == group.ProductGroupID).SingleOrDefault();

            if(category == null)
            {
                category = new Category { Id = group.ProductGroupID!.Value,  Name = group.ProductGroupName!, Description = group.ProductGroupName };
                newCategories.Add(category);
            }
        }

        foreach (var item in nayaxProducts)
        {
            var product = localProducts.Where(x=> x.Id == item.NayaxProductId).SingleOrDefault();

            if(product == null)
            {
                product = new Product
                {
                    Id = item.NayaxProductId,
                    CreatedAt = DateTime.UtcNow
                };
                newProducts.Add(product);
            }

            product.Mapped = true;
            product.Name = item.ProductName!;
            product.Description = item.ProductDescription;
            product.UnitPrice = item.ProductCostPrice??0;
            product.CategoryId = item.ProductGroupId;
            product.UpdatedAt = DateTime.UtcNow;
        }

        if(newCategories.Any()) _db.Categories.AddRange(newCategories);
        if(newProducts.Any()) _db.Products.AddRange(newProducts);

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<Product> Create(ProductCreateDto dto)
    {
        var product = new Product
        {
            Name = dto.Name,
            Sku = dto.Sku,
            Description = dto.Description,
            UnitPrice = dto.UnitPrice,
            IsActive = dto.IsActive,
            QuantityInStock = dto.QuantityInStock,
            LowStockThreshold = dto.LowStockThreshold,
            Unit = dto.Unit,
            CategoryId = dto.CategoryId,
            SupplierId = dto.SupplierId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Products.Add(product);
        await _db.SaveChangesAsync();

        if (product.QuantityInStock > 0)
        {
            _db.StockAdjustments.Add(new StockAdjustment
            {
                ProductId = product.Id,
                QuantityChange = product.QuantityInStock,
                QuantityAfter = product.QuantityInStock,
                Reason = StockAdjustmentReason.Restock,
                Notes = "Initial stock on product creation"
            });
            await _db.SaveChangesAsync();
        }

        return product;
    }

    public async Task<bool> Update(long id, ProductUpdateDto dto)
    {
        var product = await _db.Products.FindAsync(id);
        if (product is null) return false;

        product.Name = dto.Name;
        product.Sku = dto.Sku;
        product.Description = dto.Description;
        product.UnitPrice = dto.UnitPrice;
        product.IsActive = dto.IsActive;
        product.LowStockThreshold = dto.LowStockThreshold;
        product.Unit = dto.Unit;
        product.CategoryId = dto.CategoryId;
        product.SupplierId = dto.SupplierId;
        product.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> Delete(long id)
    {
        var product = await _db.Products.FindAsync(id);
        if (product is null) return false;
        _db.Products.Remove(product);
        await _db.SaveChangesAsync();
        return true;
    }
}
