using InventoryApi.Models;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Services;

public sealed partial class ImportService
{
    public async Task<bool> ImportProductsAsync()
    {
        var productsTask = _nayaxLynxClient.GetProductsAsync();
        var groupsTask = _nayaxLynxClient.GetProductGroupssAsync();
        var localProductsTask = _db.Products.ToListAsync();
        var localCategoriesTask = _db.Categories.ToListAsync();

        await Task.WhenAll(productsTask, groupsTask, localProductsTask, localCategoriesTask);

        var nayaxProducts = await productsTask;
        var nayaxGroups = await groupsTask;
        var localProducts = await localProductsTask;
        var localCategories = await localCategoriesTask;
        var newProducts = new List<Product>();
        var newCategories = new List<Category>();

        foreach (var group in nayaxGroups)
        {
            if (group.ProductGroupID is not int groupId || string.IsNullOrWhiteSpace(group.ProductGroupName))
                continue;
            if (localCategories.All(category => category.Id != groupId))
                newCategories.Add(new Category { Id = groupId, Name = group.ProductGroupName, Description = group.ProductGroupName });
        }

        foreach (var item in nayaxProducts)
        {
            var product = localProducts.SingleOrDefault(product => product.Id == item.NayaxProductId);
            if (product is null)
            {
                product = new Product
                {
                    Id = item.NayaxProductId,
                    RestockTo = 0,
                    CreatedAt = DateTime.UtcNow
                };
                newProducts.Add(product);
            }

            product.Name = item.ProductName!;
            product.Description = item.ProductDescription;
            product.UnitPrice = item.ProductCostPrice ?? 0m;
            product.CategoryId = item.ProductGroupId;
            product.UpdatedAt = DateTime.UtcNow;
        }

        if (newCategories.Count > 0) _db.Categories.AddRange(newCategories);
        if (newProducts.Count > 0) _db.Products.AddRange(newProducts);
        await _db.SaveChangesAsync();
        return true;
    }
}
