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
    private readonly IInventoryCostRebuildService _rebuild;

    public ProductService(
        AppDbContext db,
        INayaxLynxClient nayaxLynxClient,
        IInventoryCostRebuildService? rebuild = null)
    {
        _db = db;
        _nayaxLynxClient = nayaxLynxClient;
        _rebuild = rebuild ?? new InventoryCostRebuildService(db);
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

        if (lowStockOnly == true) return await LowStock(search, categoryId, supplierId);
        return await query.OrderBy(p => p.Name).ToListAsync();
    }

    public async Task<Product?> Get(long id)
    {
        return await _db.Products
            .Include(p => p.Category)
            .Include(p => p.Supplier)
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<IEnumerable<Product>> LowStock(string? search = null, long? categoryId = null, int? supplierId = null)
    {
        var query = _db.Products.AsNoTracking()
            .Include(p => p.Category)
            .Include(p => p.Supplier)
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(p => p.Name.Contains(search) || (p.Sku != null && p.Sku.Contains(search)));
        if (categoryId.HasValue) query = query.Where(p => p.CategoryId == categoryId);
        if (supplierId.HasValue) query = query.Where(p => p.SupplierId == supplierId);

        var products = await query.ToListAsync();
        var productsById = products.ToDictionary(p => p.Id);
        var nayaxMachines = await _nayaxLynxClient.GetMachinesAsync();

        foreach (var machine in nayaxMachines)
        {
            var nayaxMachineProducts = await _nayaxLynxClient.GetMachineProductsAsync(machine.MachineID);
            foreach (var nayaxProduct in nayaxMachineProducts)
            {
                if (nayaxProduct.NayaxProductID is not long productId ||
                    !productsById.TryGetValue(productId, out var product))
                    continue;

                product.MachineReplenishmentNeed += nayaxProduct.MissingStockByMDB ?? 0;
            }
        }

        var outstandingByProduct = await _db.SupplierOrderLines
            .Where(line => line.SupplierOrder.Status != SupplierOrderStatus.Cancelled &&
                           line.SupplierOrder.Status != SupplierOrderStatus.Received)
            .GroupBy(line => line.ProductId)
            .Select(group => new { ProductId = group.Key, Quantity = group.Sum(line => line.QuantityOrdered - line.QuantityReceived) })
            .ToDictionaryAsync(item => item.ProductId, item => item.Quantity);
        foreach (var product in products)
            product.OnOrderQuantity = Math.Max(0m, outstandingByProduct.GetValueOrDefault(product.Id));

        return products.Where(p => p.IsReorderAlert)
            .OrderByDescending(p => p.NeedToOrder)
            .ThenBy(p => p.Name)
            .ToList();
    }

    public async Task<Product> Create(ProductCreateDto dto)
    {
        if (dto.InitialUnitCost is < 0)
            throw new InvalidOperationException("Initial unit cost cannot be negative.");
        ValidateRestockSettings(dto.LowStockThreshold, dto.RestockTo);

        var product = new Product
        {
            Name = dto.Name,
            Sku = dto.Sku,
            Description = dto.Description,
            UnitPrice = dto.UnitPrice,
            AverageUnitCost = dto.InitialUnitCost ?? 0m,
            IsActive = dto.IsActive,
            QuantityInStock = dto.QuantityInStock,
            LowStockThreshold = dto.LowStockThreshold,
            RestockTo = dto.RestockTo,
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
                UnitCost = dto.InitialUnitCost,
                TotalCost = dto.InitialUnitCost * product.QuantityInStock,
                Notes = "Initial stock on product creation",
                EffectiveAt = product.CreatedAt
            });
            await _db.SaveChangesAsync();
            if (dto.InitialUnitCost.HasValue)
            {
                await _rebuild.RebuildAsync(product.Id);
                await _db.SaveChangesAsync();
            }
        }

        return product;
    }

    public async Task<bool> Update(long id, ProductUpdateDto dto)
    {
        var product = await _db.Products.FindAsync(id);
        if (product is null) return false;

        ValidateRestockSettings(dto.LowStockThreshold, dto.RestockTo);

        product.Sku = dto.Sku;
        product.Description = dto.Description;
        product.IsActive = dto.IsActive;
        product.LowStockThreshold = dto.LowStockThreshold;
        product.RestockTo = dto.RestockTo;
        product.Unit = dto.Unit;
        product.SupplierId = dto.SupplierId;
        product.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }

    private static void ValidateRestockSettings(int lowStockThreshold, int restockTo)
    {
        if (lowStockThreshold < 0)
            throw new InvalidOperationException("Low Stock Threshold cannot be negative.");
        if (restockTo < 0)
            throw new InvalidOperationException("Restock To cannot be negative.");
        if (restockTo < lowStockThreshold)
            throw new InvalidOperationException("Restock To must be greater than or equal to the Low Stock Threshold.");
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
