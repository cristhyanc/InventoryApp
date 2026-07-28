using System.Threading.Tasks;
using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Services;

public class StockServiceTests
{
    private static AppDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task Adjust_Increases_Quantity_And_Creates_Adjustment()
    {
        using var db = CreateDbContext("stock_test");
        db.Products.Add(new Product { Id = 1, Name = "p", QuantityInStock = 2 });
        await db.SaveChangesAsync();

        IStockService svc = new StockService(db);
        var adj = await svc.Adjust(1, new StockAdjustmentDto(3, StockAdjustmentReason.Restock, "note", System.DateTime.UtcNow.AddDays(30)));
        Assert.NotNull(adj);

        var product = await db.Products.FindAsync(1L);
        Assert.Equal(5, product.QuantityInStock);
    }

    [Fact]
    public async Task History_Returns_Empty_For_NonExisting()
    {
        using var db = CreateDbContext("stock_hist_test");
        IStockService svc = new StockService(db);
        var hist = await svc.History(99);
        Assert.Empty(hist);
    }
}
