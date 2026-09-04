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
        var adj = await svc.Adjust(1, new StockAdjustmentDto(3, StockAdjustmentReason.Restock, "note", null, System.DateTime.UtcNow.AddDays(30)));
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

    [Fact]
    public async Task Adjust_Throws_When_Stock_Would_Become_Negative()
    {
        using var db = CreateDbContext("stock_insufficient_test");
        db.Products.Add(new Product { Id = 1, Name = "p", QuantityInStock = 2 });
        await db.SaveChangesAsync();

        IStockService svc = new StockService(db);

        var exception = await Assert.ThrowsAsync<InsufficientStockException>(() =>
            svc.Adjust(1, new StockAdjustmentDto(-3, StockAdjustmentReason.MachineRefill, "note", 1, null)));

        Assert.Equal("Not enough products in stock. Available stock: 2", exception.Message);
        var product = await db.Products.FindAsync(1L);
        Assert.Equal(2, product.QuantityInStock);
    }

    [Fact]
    public async Task Machine_refill_decreases_stock_without_changing_average_cost()
    {
        using var db = CreateDbContext("stock_avco_refill_test");
        db.Products.Add(new Product { Id = 1, Name = "p", QuantityInStock = 34, AverageUnitCost = 1.323529411764705882m });
        await db.SaveChangesAsync();

        IStockService svc = new StockService(db);
        await svc.Adjust(1, new StockAdjustmentDto(-8, StockAdjustmentReason.MachineRefill, "refill", 7, null));

        var product = await db.Products.FindAsync(1L);
        Assert.Equal(26, product!.QuantityInStock);
        Assert.Equal(1.323529411764705882m, product.AverageUnitCost);
        var movement = await db.StockAdjustments.SingleAsync();
        Assert.Equal(1.323529411764705882m, movement.UnitCost);
        Assert.Equal(8 * product.AverageUnitCost, movement.TotalCost);
    }

    [Fact]
    public async Task Positive_adjustment_without_cost_does_not_invent_average_cost()
    {
        using var db = CreateDbContext("stock_unknown_cost_test");
        db.Products.Add(new Product { Id = 1, Name = "p", QuantityInStock = 2, AverageUnitCost = 3m });
        await db.SaveChangesAsync();

        IStockService svc = new StockService(db);
        await svc.Adjust(1, new StockAdjustmentDto(3, StockAdjustmentReason.Correction, "count correction", null, null));

        var product = await db.Products.FindAsync(1L);
        Assert.Equal(5, product!.QuantityInStock);
        Assert.Equal(3m, product.AverageUnitCost);
        var movement = await db.StockAdjustments.SingleAsync();
        Assert.Null(movement.UnitCost);
        Assert.Null(movement.TotalCost);
    }
}
