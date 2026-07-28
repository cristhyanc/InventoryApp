using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using InventoryApi.Services;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Services;

public class ProductServiceTests
{
    private static AppDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task Create_Update_Delete_Product_And_StockAdjustment_Created()
    {
        using var db = CreateDbContext("prod_test");
        var nayaxMock = new Mock<INayaxLynxClient>();
        IProductService svc = new ProductService(db, nayaxMock.Object);

        var dto = new ProductCreateDto("p1", null, null, 10m, 5, 1, "unit", null, null, true);
        var product = await svc.Create(new ProductCreateDto("p1", null, null, 10m, 5, 1, "unit", null, null, true));

        Assert.NotNull(product);
        Assert.Equal("p1", product.Name);

        // Verify stock adjustment created
        var adjustments = await db.StockAdjustments.ToListAsync();
        Assert.Single(adjustments);

        var updateDto = new ProductUpdateDto("p1-up", null, null, 12m, 1, "unit", null, null, true);
        var ok = await svc.Update(product.Id, updateDto);
        Assert.True(ok);

        var deleted = await svc.Delete(product.Id);
        Assert.True(deleted);
    }

    [Fact]
    public async Task ImportProducts_Adds_New()
    {
        using var db = CreateDbContext("import_test");
        var nayaxMock = new Mock<INayaxLynxClient>();

        nayaxMock.Setup(m => m.GetProductsAsync(It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new List<NayaxProduct>
            {
                new NayaxProduct { NayaxProductId = 100, ProductName = "X", ProductGroupId = 10, ProductCostPrice = 2 }
            });

        nayaxMock.Setup(m => m.GetProductGroupssAsync(It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new List<NayaxProductGroup>
            {
                new NayaxProductGroup { ProductGroupID = 10, ProductGroupName = "G1" }
            });

        IProductService svc = new ProductService(db, nayaxMock.Object);
        var result = await svc.ImportProductsAsync();
        Assert.True(result);

        var prod = await db.Products.FindAsync(100L);
        Assert.NotNull(prod);
        var cat = await db.Categories.FindAsync(10L);
        Assert.NotNull(cat);
    }
}
