using InventoryApi.Controllers;
using InventoryApi.Data;
using InventoryApi.DTOs;
using Inventory.Application.Nayax;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

public class ProductsControllerTests
{
    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return TestAppDbContext.Unrestricted(options);
    }

    private static ProductsController CreateController(AppDbContext db)
    {
        var nayaxMock = new Mock<INayaxLynxClient>();
        return new ProductsController(new ProductService(db, nayaxMock.Object));
    }

    private static ProductUpdateDto UpdateDto(int lowStockThreshold, int restockTo) =>
        new("Coke", null, null, 1m, lowStockThreshold, restockTo, "unit", null, null, true);

    [Fact]
    public async Task Update_RestockToBelowThreshold_ReturnsBadRequestWithMessage()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Coke", LowStockThreshold = 57, RestockTo = 100 });
        await db.SaveChangesAsync();

        var result = await CreateController(db).Update(1, UpdateDto(57, 40));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Restock To must be greater than or equal to the Low Stock Threshold.", badRequest.Value);
    }

    [Fact]
    public async Task Update_NegativeLowStockThreshold_ReturnsBadRequest()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Coke", LowStockThreshold = 5, RestockTo = 10 });
        await db.SaveChangesAsync();

        var result = await CreateController(db).Update(1, UpdateDto(-1, 10));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Update_NegativeRestockTo_ReturnsBadRequest()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Coke", LowStockThreshold = 5, RestockTo = 10 });
        await db.SaveChangesAsync();

        var result = await CreateController(db).Update(1, UpdateDto(5, -1));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Update_NonExistentProduct_ReturnsNotFound_EvenWithInvalidRestockSettings()
    {
        using var db = CreateDbContext();

        var result = await CreateController(db).Update(999, UpdateDto(57, 40));

        Assert.IsType<NotFoundResult>(result);
    }
}
