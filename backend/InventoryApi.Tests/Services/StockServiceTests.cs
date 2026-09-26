using Inventory.Domain.Exceptions;
using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Xunit;

namespace InventoryApi.Tests.Services;

public class StockServiceTests
{
    private static AppDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return TestAppDbContext.Unrestricted(options);
    }

    [Fact]
    public async Task Positive_restock_with_null_unit_cost_is_rejected()
    {
        using var db = CreateDbContext("stock_test");
        db.Products.Add(new Product { Id = 1, Name = "p", QuantityInStock = 2, CostingQuantity = 2, InventoryValue = 6m, AverageUnitCost = 3m });
        db.StockAdjustments.Add(new StockAdjustment { ProductId = 1, QuantityChange = 2, Reason = StockAdjustmentReason.Restock, UnitCost = 3m, EffectiveAt = System.DateTime.UtcNow.AddMinutes(-1) });
        await db.SaveChangesAsync();

        IStockService svc = new StockService(db);
        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            svc.Adjust(1, new StockAdjustmentDto(3, StockAdjustmentReason.Restock, "note", null, System.DateTime.UtcNow.AddDays(30))));

        Assert.Equal("Unit cost is required for a positive Restock adjustment.", exception.Message);
        Assert.Single(await db.StockAdjustments.ToListAsync());
    }

    [Fact]
    public async Task Positive_restock_with_negative_unit_cost_is_rejected()
    {
        using var db = CreateDbContext("stock_negative_restock_cost_test");
        db.Products.Add(new Product { Id = 1, Name = "p", QuantityInStock = 2 });
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            new StockService(db).Adjust(1, new StockAdjustmentDto(3, StockAdjustmentReason.Restock, null, null, null, -1m)));

        Assert.Equal("Unit cost cannot be negative for a positive Restock adjustment.", exception.Message);
        Assert.Empty(await db.StockAdjustments.ToListAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1.25)]
    public async Task Positive_restock_with_valid_unit_cost_persists_costs(decimal unitCost)
    {
        using var db = CreateDbContext($"stock_valid_restock_cost_test_{unitCost}");
        db.Products.Add(new Product { Id = 1, Name = "p", QuantityInStock = 0, CostingQuantity = 0, InventoryValue = 0m });
        await db.SaveChangesAsync();

        var adjustment = await new StockService(db).Adjust(
            1, new StockAdjustmentDto(4, StockAdjustmentReason.Restock, null, null, null, unitCost));

        Assert.NotNull(adjustment);
        Assert.Equal(unitCost, adjustment!.UnitCost);
        Assert.Equal(4 * unitCost, adjustment.TotalCost);
    }

    [Fact]
    public async Task Restock_cost_suggestion_uses_latest_purchase_cost_over_average_cost()
    {
        using var db = CreateDbContext("stock_latest_purchase_suggestion_test");
        db.Products.Add(new Product { Id = 1, Name = "p", CostingQuantity = 2, InventoryValue = 8m, AverageUnitCost = 4m });
        db.Receipts.AddRange(
            new Purchase { Id = 1, Title = "old", PurchaseDate = new DateTime(2025, 1, 1) },
            new Purchase { Id = 2, Title = "new", PurchaseDate = new DateTime(2025, 2, 1) });
        db.ReceiptItems.AddRange(
            new PurchaseItem { Id = 1, ReceiptId = 1, ProductId = 1, Quantity = 1, UnitCost = 1.25m },
            new PurchaseItem { Id = 2, ReceiptId = 2, ProductId = 1, Quantity = 1, UnitCost = 2.50m });
        await db.SaveChangesAsync();

        var suggestion = await new StockService(db).GetRestockCostSuggestion(1);

        Assert.NotNull(suggestion);
        Assert.Equal(2.50m, suggestion!.UnitCost);
        Assert.Equal("LastPurchase", suggestion.Source);
        Assert.Equal(new DateTime(2025, 2, 1), suggestion.PurchaseDate);
    }

    [Fact]
    public async Task Restock_cost_suggestion_uses_known_average_only_without_purchase()
    {
        using var db = CreateDbContext("stock_average_suggestion_test");
        db.Products.Add(new Product { Id = 1, Name = "known zero", CostingQuantity = 3, InventoryValue = 0m, AverageUnitCost = 0m });
        db.Products.Add(new Product { Id = 2, Name = "unknown", CostingQuantity = null, InventoryValue = null, AverageUnitCost = 0m });
        await db.SaveChangesAsync();

        var known = await new StockService(db).GetRestockCostSuggestion(1);
        var unknown = await new StockService(db).GetRestockCostSuggestion(2);

        Assert.Equal(0m, known!.UnitCost);
        Assert.Equal("AverageUnitCost", known.Source);
        Assert.Null(unknown!.UnitCost);
        Assert.Equal("None", unknown.Source);
    }

    [Fact]
    public async Task Positive_correction_request_is_rejected_before_persistence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var setup = TestAppDbContext.Unrestricted(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Products.Add(new Product { Id = 1, Name = "p", QuantityInStock = 2, CostingQuantity = 2, InventoryValue = 6m, AverageUnitCost = 3m });
            setup.StockAdjustments.Add(new StockAdjustment { ProductId = 1, QuantityChange = 2, Reason = StockAdjustmentReason.Restock, UnitCost = 3m, EffectiveAt = DateTime.UtcNow.AddMinutes(-1) });
            await setup.SaveChangesAsync();
        }

        await using (var db = TestAppDbContext.Unrestricted(options))
        {
            var exception = await Assert.ThrowsAsync<DomainValidationException>(() => new StockService(db).Adjust(
                1, new StockAdjustmentDto(3, StockAdjustmentReason.Correction, null, null, null)));
            Assert.Equal("Correction quantity must remove stock.", exception.Message);
        }

        await using var verification = TestAppDbContext.Unrestricted(options);
        Assert.Single(await verification.StockAdjustments.ToListAsync());
        Assert.Equal(2, (await verification.Products.SingleAsync()).QuantityInStock);
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
        Assert.NotNull(product);
        Assert.Equal(2, product.QuantityInStock);
    }

    [Fact]
    public async Task Machine_refill_decreases_stock_without_changing_average_cost()
    {
        using var db = CreateDbContext("stock_avco_refill_test");
        db.Products.Add(new Product { Id = 1, Name = "p", QuantityInStock = 34, CostingQuantity = 34, InventoryValue = 45m, AverageUnitCost = 1.323529411764705882m });
        db.StockAdjustments.Add(new StockAdjustment { ProductId = 1, QuantityChange = 34, Reason = StockAdjustmentReason.Restock, UnitCost = 1.323529411764705882m, EffectiveAt = System.DateTime.UtcNow.AddMinutes(-1) });
        await db.SaveChangesAsync();

        IStockService svc = new StockService(db);
        await svc.Adjust(1, new StockAdjustmentDto(-8, StockAdjustmentReason.MachineRefill, "refill", 7, null));

        var product = await db.Products.FindAsync(1L);
        Assert.Equal(26, product!.QuantityInStock);
        Assert.Equal(1.323529411764705882m, product.AverageUnitCost);
        var movement = await db.StockAdjustments.SingleAsync(x => x.Reason == StockAdjustmentReason.MachineRefill);
        Assert.Equal(1.323529411764705882m, movement.UnitCost);
        Assert.Equal(8 * product.AverageUnitCost, movement.TotalCost);
    }

    [Fact]
    public async Task Zero_correction_request_is_rejected_before_persistence()
    {
        using var db = CreateDbContext("stock_unknown_cost_test");
        db.Products.Add(new Product { Id = 1, Name = "p", QuantityInStock = 2, CostingQuantity = 2, InventoryValue = 6m, AverageUnitCost = 3m });
        db.StockAdjustments.Add(new StockAdjustment { ProductId = 1, QuantityChange = 2, Reason = StockAdjustmentReason.Restock, UnitCost = 3m, EffectiveAt = System.DateTime.UtcNow.AddMinutes(-1) });
        await db.SaveChangesAsync();

        IStockService svc = new StockService(db);
        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            svc.Adjust(1, new StockAdjustmentDto(0, StockAdjustmentReason.Correction, "count correction", null, null)));

        Assert.Equal("Correction quantity must remove stock.", exception.Message);
        Assert.Single(await db.StockAdjustments.ToListAsync());
    }

    [Fact]
    public async Task Negative_correction_rebuilds_using_current_average_cost()
    {
        using var db = CreateDbContext("stock_negative_correction_test");
        db.Products.Add(new Product { Id = 1, Name = "p", QuantityInStock = 8, CostingQuantity = 8, InventoryValue = 14m, AverageUnitCost = 1.75m });
        db.StockAdjustments.Add(new StockAdjustment { ProductId = 1, QuantityChange = 8, Reason = StockAdjustmentReason.Restock, UnitCost = 1.75m, EffectiveAt = DateTime.UtcNow.AddMinutes(-1) });
        await db.SaveChangesAsync();

        var adjustment = await new StockService(db).Adjust(
            1, new StockAdjustmentDto(-4, StockAdjustmentReason.Correction, null, null, null));

        var product = await db.Products.SingleAsync();
        Assert.Equal(4, product.QuantityInStock);
        Assert.Equal(4, product.CostingQuantity);
        Assert.Equal(7m, product.InventoryValue);
        Assert.Equal(1.75m, product.AverageUnitCost);
        Assert.Equal(1.75m, adjustment!.UnitCost);
        Assert.Equal(7m, adjustment.TotalCost);
    }
}
