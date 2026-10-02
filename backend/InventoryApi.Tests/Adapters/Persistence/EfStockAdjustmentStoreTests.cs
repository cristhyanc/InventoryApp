using Inventory.Application.Stock;
using Inventory.Domain.Exceptions;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DomainStock = Inventory.Domain.Stock;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// <see cref="EfStockAdjustmentStore"/> (issue #282): stock history, the restock-cost-suggestion
/// facts, and manual stock-adjustment persistence. Relational (SQLite) because applying a movement
/// runs inside a real transaction and must leave a consistent <c>StockAdjustment</c> history, reads
/// must respect the central tenant query filter (issue #64) with no per-call business filter of its
/// own, and the restock-cost-suggestion query depends on SQL ordering across purchases.
/// </summary>
public class EfStockAdjustmentStoreTests
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private static async Task<DbContextOptions<AppDbContext>> CreateSqliteAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.EnsureCreatedAsync();
        return options;
    }

    /// <summary>
    /// EF Core InMemory, for AVCO arithmetic that does not depend on a real transaction: Microsoft's
    /// SQLite provider stores <c>decimal</c> with limited round-trip precision, which would make a
    /// high-precision average-cost assertion fail for a reason unrelated to the behaviour under
    /// test - the same reason the former <c>StockServiceTests</c> used InMemory for this scenario.
    /// </summary>
    private static AppDbContext CreateInMemoryDb(string dbName) =>
        TestAppDbContext.For(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options, BusinessA);

    private static EfStockAdjustmentStore StoreFor(AppDbContext db) =>
        new(db, new InventoryCostService(db), new InventoryCostRebuildService(db));

    private static ManualStockAdjustmentInput ManualInput(
        int quantityChange, DomainStock.StockAdjustmentReason reason, decimal? unitCost = null, long? machineId = null) =>
        new(quantityChange, reason, "note", machineId, null, unitCost);

    [Theory]
    [InlineData(0)]
    [InlineData(1.25)]
    public async Task ApplyAsync_PositiveRestock_PersistsCostAndQuantity(decimal unitCost)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            seed.Products.Add(new Product { Id = 1, Name = "p", QuantityInStock = 0, CostingQuantity = 0, InventoryValue = 0m });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var record = await StoreFor(db).ApplyAsync(1, ManualInput(4, DomainStock.StockAdjustmentReason.Restock, unitCost), CancellationToken.None);

        Assert.Equal(unitCost, record.UnitCost);
        Assert.Equal(4 * unitCost, record.TotalCost);
        Assert.Equal(4, record.QuantityAfter);
    }

    [Fact]
    public async Task ApplyAsync_MachineRefill_DecreasesStockWithoutChangingAverageCost()
    {
        using var db = CreateInMemoryDb(nameof(ApplyAsync_MachineRefill_DecreasesStockWithoutChangingAverageCost));
        db.Products.Add(new Product
        {
            Id = 1,
            Name = "p",
            QuantityInStock = 34,
            CostingQuantity = 34,
            InventoryValue = 45m,
            AverageUnitCost = 1.323529411764705882m
        });
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 1,
            QuantityChange = 34,
            QuantityAfter = 34,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = 1.323529411764705882m,
            EffectiveAt = DateTime.UtcNow.AddMinutes(-1)
        });
        await db.SaveChangesAsync();

        await StoreFor(db).ApplyAsync(1, ManualInput(-8, DomainStock.StockAdjustmentReason.MachineRefill, machineId: 7), CancellationToken.None);

        var product = await db.Products.SingleAsync(p => p.Id == 1);
        Assert.Equal(26, product.QuantityInStock);
        Assert.Equal(1.323529411764705882m, product.AverageUnitCost);
        var movement = await db.StockAdjustments.SingleAsync(x => x.Reason == StockAdjustmentReason.MachineRefill);
        Assert.Equal(1.323529411764705882m, movement.UnitCost);
        Assert.Equal(7, movement.MachineId);
    }

    [Fact]
    public async Task ApplyAsync_NegativeCorrection_RebuildsUsingCurrentAverageCost()
    {
        using var db = CreateInMemoryDb(nameof(ApplyAsync_NegativeCorrection_RebuildsUsingCurrentAverageCost));
        db.Products.Add(new Product { Id = 1, Name = "p", QuantityInStock = 8, CostingQuantity = 8, InventoryValue = 14m, AverageUnitCost = 1.75m });
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 1,
            QuantityChange = 8,
            QuantityAfter = 8,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = 1.75m,
            EffectiveAt = DateTime.UtcNow.AddMinutes(-1)
        });
        await db.SaveChangesAsync();

        var record = await StoreFor(db).ApplyAsync(1, ManualInput(-4, DomainStock.StockAdjustmentReason.Correction), CancellationToken.None);
        Assert.Equal(1.75m, record.UnitCost);
        Assert.Equal(7m, record.TotalCost);

        var product = await db.Products.SingleAsync(p => p.Id == 1);
        Assert.Equal(4, product.QuantityInStock);
        Assert.Equal(4, product.CostingQuantity);
        Assert.Equal(7m, product.InventoryValue);
    }

    [Fact]
    public async Task ApplyAsync_InsufficientStock_ThrowsAndLeavesStockUnchanged()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            seed.Products.Add(new Product { Id = 1, Name = "p", QuantityInStock = 2 });
            await seed.SaveChangesAsync();
        }

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var exception = await Assert.ThrowsAsync<InsufficientStockException>(() =>
                StoreFor(db).ApplyAsync(1, ManualInput(-3, DomainStock.StockAdjustmentReason.MachineRefill), CancellationToken.None));
            Assert.Equal("Not enough products in stock. Available stock: 2", exception.Message);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        var product = await verify.Products.SingleAsync(p => p.Id == 1);
        Assert.Equal(2, product.QuantityInStock);
        Assert.Empty(await verify.StockAdjustments.ToListAsync());
    }

    [Fact]
    public async Task GetRestockCostFactsAsync_PrefersTheLatestPurchaseOverTheAverageCost()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            seed.Products.Add(new Product { Id = 1, Name = "p", CostingQuantity = 2, InventoryValue = 8m, AverageUnitCost = 4m });
            seed.Receipts.AddRange(
                new Purchase { Id = 1, Title = "old", PurchaseDate = new DateTime(2025, 1, 1) },
                new Purchase { Id = 2, Title = "new", PurchaseDate = new DateTime(2025, 2, 1) });
            seed.ReceiptItems.AddRange(
                new PurchaseItem { Id = 1, ReceiptId = 1, ProductId = 1, Quantity = 1, UnitCost = 1.25m },
                new PurchaseItem { Id = 2, ReceiptId = 2, ProductId = 1, Quantity = 1, UnitCost = 2.50m });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var facts = await StoreFor(db).GetRestockCostFactsAsync(1, CancellationToken.None);

        Assert.NotNull(facts);
        Assert.NotNull(facts!.LastPurchase);
        Assert.Equal(2.50m, facts.LastPurchase!.UnitCost);
        Assert.Equal(new DateTime(2025, 2, 1), facts.LastPurchase.PurchaseDate);
    }

    [Fact]
    public async Task GetRestockCostFactsAsync_ReturnsCostingFactsWithoutAPurchase()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            seed.Products.Add(new Product { Id = 1, Name = "p", CostingQuantity = 3, InventoryValue = 0m, AverageUnitCost = 0m });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var facts = await StoreFor(db).GetRestockCostFactsAsync(1, CancellationToken.None);

        Assert.NotNull(facts);
        Assert.Null(facts!.LastPurchase);
        Assert.Equal(3, facts.CostingQuantity);
        Assert.Equal(0m, facts.InventoryValue);
        Assert.Equal(0m, facts.AverageUnitCost);
    }

    [Fact]
    public async Task GetRestockCostFactsAsync_ReturnsNull_ForAnotherBusinesssProduct()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using (var seed = TestAppDbContext.For(options, BusinessB))
        {
            seed.Products.Add(new Product { Id = 1, Name = "p", CostingQuantity = 3, InventoryValue = 0m, AverageUnitCost = 0m });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var facts = await StoreFor(db).GetRestockCostFactsAsync(1, CancellationToken.None);

        Assert.Null(facts);
    }

    [Fact]
    public async Task ListHistoryAsync_OrdersMostRecentFirstAndNeverCrossesBusinesses()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            seed.Products.Add(new Product { Id = 1, Name = "p", QuantityInStock = 10 });
            seed.StockAdjustments.AddRange(
                new StockAdjustment { ProductId = 1, QuantityChange = 5, QuantityAfter = 5, Reason = StockAdjustmentReason.Restock, CreatedAt = DateTime.UtcNow.AddHours(-2), EffectiveAt = DateTime.UtcNow.AddHours(-2) },
                new StockAdjustment { ProductId = 1, QuantityChange = 5, QuantityAfter = 10, Reason = StockAdjustmentReason.Restock, CreatedAt = DateTime.UtcNow.AddHours(-1), EffectiveAt = DateTime.UtcNow.AddHours(-1) });
            await seed.SaveChangesAsync();
        }
        await using (var seedB = TestAppDbContext.For(options, BusinessB))
        {
            seedB.Products.Add(new Product { Id = 2, Name = "other business product", QuantityInStock = 1 });
            seedB.StockAdjustments.Add(new StockAdjustment { ProductId = 2, QuantityChange = 1, QuantityAfter = 1, Reason = StockAdjustmentReason.Restock, EffectiveAt = DateTime.UtcNow });
            await seedB.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var history = await StoreFor(db).ListHistoryAsync(1, CancellationToken.None);

        Assert.Equal(2, history.Count);
        Assert.True(history[0].CreatedAt > history[1].CreatedAt);
    }

    [Fact]
    public async Task ProductExistsAsync_ReturnsFalse_ForAnotherBusinesssProduct()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using (var seed = TestAppDbContext.For(options, BusinessB))
        {
            seed.Products.Add(new Product { Id = 1, Name = "other business product", QuantityInStock = 1 });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        Assert.False(await StoreFor(db).ProductExistsAsync(1, CancellationToken.None));
    }
}
