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


}
