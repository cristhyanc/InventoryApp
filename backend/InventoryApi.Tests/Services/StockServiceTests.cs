using System;
using System.Collections.Generic;
using System.Threading;
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
    public async Task Adjust_rejects_restock_without_valid_unit_cost()
    {
        using var db = CreateDbContext("stock_test");
        db.Products.Add(new Product { Id = 1, Name = "p", QuantityInStock = 2, CostingQuantity = 2, InventoryValue = 4m, AverageUnitCost = 2m });
        await db.SaveChangesAsync();

        IStockService svc = new StockService(db);

        var exception = await Assert.ThrowsAsync<InventoryCostDataQualityException>(() =>
            svc.Adjust(1, new StockAdjustmentDto(3, StockAdjustmentReason.Restock, "note", null, System.DateTime.UtcNow.AddDays(30))));

        Assert.Contains("no valid unit cost", exception.Message);
        var product = await db.Products.FindAsync(1L);
        Assert.Equal(5, product!.QuantityInStock);
    }
}
