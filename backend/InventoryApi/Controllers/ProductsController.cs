using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Xml.Linq;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly INayaxLynxClient _nayaxLynxClient;

    public ProductsController(AppDbContext db, INayaxLynxClient nayaxLynxClient)
    {
        _db = db;
        _nayaxLynxClient= nayaxLynxClient;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Product>>> GetAll(
        [FromQuery] string? search,
        [FromQuery] int? categoryId,
        [FromQuery] int? supplierId,
        [FromQuery] bool? lowStockOnly)
    {
        var query = _db.Products
            .Include(p => p.Category)
            .Include(p => p.Supplier)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(p => p.Name.Contains(search) || (p.Sku != null && p.Sku.Contains(search)));

        //if (type.HasValue) query = query.Where(p => p.Type == type);
        if (categoryId.HasValue) query = query.Where(p => p.CategoryId == categoryId);
        if (supplierId.HasValue) query = query.Where(p => p.SupplierId == supplierId);
        if (lowStockOnly == true) query = query.Where(p => p.QuantityInStock <= p.LowStockThreshold);

        var result = await query.OrderBy(p => p.Name).ToListAsync();

        return Ok(result);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Product>> Get(int id)
    {
        var product = await _db.Products
            .Include(p => p.Category)
            .Include(p => p.Supplier)
            .FirstOrDefaultAsync(p => p.Id == id);
        return product is null ? NotFound() : Ok(product);
    }

    [HttpGet("alerts/low-stock")]
    public async Task<ActionResult<IEnumerable<Product>>> LowStock()
    {
        var items = await _db.Products
            .Include(p => p.Category)
            .Where(p => p.QuantityInStock <= p.LowStockThreshold)
            .OrderBy(p => p.QuantityInStock)
            .ToListAsync();
        return Ok(items);
    }

    [HttpPost("importProducts")]
    public async Task<ActionResult<bool>> ImportProducts()
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

        var products = new List<Product>();
        var categories = new List<Category>();        

        foreach (var group in nayaxGroups)
        {
            var category = localCategories.Where(x => x.Id == group.ProductGroupID).SingleOrDefault();

            if(category == null)
            {
                category = new Category { Id = group.ProductGroupID!.Value,  Name = group.ProductGroupName!, Description = group.ProductGroupRef };
                categories.Add(category);
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
            }

            product.Mapped = true;
            product.Name = item.ProductName!;
            product.Description = item.ProductDescription;
            product.UnitPrice = item.ProductCostPrice??0;
            product.CategoryId = item.ProductGroupId;
            product.UpdatedAt = DateTime.UtcNow;

            products.Add(product);
        }


         _db.Categories.AddRange(categories);
        _db.Products.AddRange(products);
        await _db.SaveChangesAsync();
        return true;
    }

    [HttpPost]
    public async Task<ActionResult<Product>> Create(ProductCreateDto dto)
    {
        var product = new Product
        {
            Name = dto.Name,
            Sku = dto.Sku,
            Description = dto.Description,
            UnitPrice = dto.UnitPrice,
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

        return CreatedAtAction(nameof(Get), new { id = product.Id }, product);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, ProductUpdateDto dto)
    {
        var product = await _db.Products.FindAsync(id);
        if (product is null) return NotFound();

        product.Name = dto.Name;
        product.Sku = dto.Sku;
        product.Description = dto.Description;
        product.UnitPrice = dto.UnitPrice;
        product.LowStockThreshold = dto.LowStockThreshold;
        product.Unit = dto.Unit;
        product.CategoryId = dto.CategoryId;
        product.SupplierId = dto.SupplierId;
        product.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var product = await _db.Products.FindAsync(id);
        if (product is null) return NotFound();
        _db.Products.Remove(product);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
