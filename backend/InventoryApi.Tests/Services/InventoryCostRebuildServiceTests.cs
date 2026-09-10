using System;
using System.IO;
using System.Threading.Tasks;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Services;

public class InventoryCostRebuildServiceTests
{
    [Fact]
    public async Task Rebuild_accepts_valid_inventory_history()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 1, Name = "Snack", QuantityInStock = 10, AverageUnitCost = 2m });
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 1,
            QuantityChange = 10,
            QuantityAfter = 10,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = 2m,
            TotalCost = 20m,
            CostingQuantityAfter = 10,
            InventoryValueAfter = 20m,
            AverageUnitCostAfter = 2m,
            EffectiveAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
        });
        await db.SaveChangesAsync();

        var result = await new InventoryCostRebuildService(db).RebuildAsync(1);

        Assert.Equal(1, result.ProductId);
        Assert.Equal(10, (await db.Products.SingleAsync()).QuantityInStock);
    }

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
