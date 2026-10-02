using Inventory.Application.Stock;
using Inventory.Domain.InventoryCounting;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// <see cref="EfInventoryCountAdjustmentStore"/> (issue #245): the Take Inventory apply use case's
/// only touchpoint with persisted storage quantity and the audit trail. Relational (SQLite) because
/// applying a movement runs inside a real transaction and must leave a consistent
/// <c>StockAdjustment</c> history, and reads must respect the central tenant query filter (issue
/// #64) with no per-call business filter of its own.
/// </summary>
public class EfInventoryCountAdjustmentStoreTests
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

    private static EfInventoryCountAdjustmentStore StoreFor(AppDbContext db)
    {
        var costing = TestCostingUseCases.RecordMovement(db);
        var rebuild = TestCostingUseCases.Rebuild(db);
        var getRestockCostSuggestion = new GetRestockCostSuggestion(new EfStockAdjustmentStore(db, costing, rebuild));
        return new EfInventoryCountAdjustmentStore(db, costing, rebuild, getRestockCostSuggestion);
    }

    [Fact]
    public async Task GetCurrentStockAsync_ReturnsTheAuthoritativeQuantity()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            seed.Products.Add(new Product { Id = 100, Name = "Coke Zero", QuantityInStock = 17 });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var result = await StoreFor(db).GetCurrentStockAsync(100, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Coke Zero", result!.ProductName);
        Assert.Equal(17, result.QuantityInStock);
    }

    [Fact]
    public async Task GetCurrentStockAsync_ReturnsNull_ForAnotherBusinesssProduct()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using (var seed = TestAppDbContext.For(options, BusinessB))
        {
            seed.Products.Add(new Product { Id = 100, Name = "Business B Product", QuantityInStock = 99 });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var result = await StoreFor(db).GetCurrentStockAsync(100, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetRestockUnitCostAsync_ReturnsTheSameSuggestionAManualRestockWouldOffer()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            seed.Products.Add(new Product { Id = 100, Name = "Coke Zero", QuantityInStock = 10, CostingQuantity = 10, AverageUnitCost = 1.75m, InventoryValue = 17.5m });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var unitCost = await StoreFor(db).GetRestockUnitCostAsync(100, CancellationToken.None);

        Assert.Equal(1.75m, unitCost);
    }

    [Fact]
    public async Task GetRestockUnitCostAsync_ReturnsNull_WhenNoCostIsAvailable()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            seed.Products.Add(new Product { Id = 100, Name = "New Product", QuantityInStock = 0, CostingQuantity = 0, AverageUnitCost = 0m, InventoryValue = 0m });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var unitCost = await StoreFor(db).GetRestockUnitCostAsync(100, CancellationToken.None);

        Assert.Null(unitCost);
    }

    /// <summary>Seeds an opening costed Restock so <c>RebuildProductCost</c>'s replay has a known average cost to work from - it recomputes quantity/costing purely from adjustment history, not from whatever a test sets directly on <see cref="Product"/>.</summary>
    private static async Task SeedOpeningRestockAsync(DbContextOptions<AppDbContext> options, int businessId, long productId, int quantity, decimal unitCost)
    {
        await using var seed = TestAppDbContext.For(options, businessId);
        seed.Products.Add(new Product { Id = productId, Name = "Coke Zero", QuantityInStock = quantity });
        seed.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = productId,
            QuantityChange = quantity,
            QuantityAfter = quantity,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = unitCost,
            EffectiveAt = DateTime.UtcNow.AddDays(-1)
        });
        await seed.SaveChangesAsync();
    }

    [Fact]
    public async Task ApplyAsync_Increase_UsesRestockAndRecordsAnAuditableStockAdjustment()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedOpeningRestockAsync(options, BusinessA, 100, quantity: 17, unitCost: 2m);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var application = await StoreFor(db)
                .ApplyAsync(100, InventoryCountMovementKind.Increase, 5, 2.5m, CancellationToken.None);

            Assert.Equal(22, application.QuantityInStock);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        var product = await verify.Products.SingleAsync(p => p.Id == 100);
        Assert.Equal(22, product.QuantityInStock);

        var adjustment = await verify.StockAdjustments.SingleAsync(a => a.ProductId == 100 && a.QuantityChange == 5);
        Assert.Equal(StockAdjustmentReason.Restock, adjustment.Reason);
        Assert.Equal(22, adjustment.QuantityAfter);
        Assert.Equal(2.5m, adjustment.UnitCost);
        Assert.Equal(2, await verify.StockAdjustments.CountAsync(a => a.ProductId == 100));
    }

    [Fact]
    public async Task ApplyAsync_Decrease_UsesCorrectionAndRecordsAnAuditableStockAdjustment()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedOpeningRestockAsync(options, BusinessA, 100, quantity: 32, unitCost: 2m);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var application = await StoreFor(db)
                .ApplyAsync(100, InventoryCountMovementKind.Decrease, -4, null, CancellationToken.None);

            Assert.Equal(28, application.QuantityInStock);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        var product = await verify.Products.SingleAsync(p => p.Id == 100);
        Assert.Equal(28, product.QuantityInStock);

        var adjustment = await verify.StockAdjustments.SingleAsync(a => a.ProductId == 100 && a.QuantityChange == -4);
        Assert.Equal(StockAdjustmentReason.Correction, adjustment.Reason);
        Assert.Equal(28, adjustment.QuantityAfter);
        Assert.Equal(2, await verify.StockAdjustments.CountAsync(a => a.ProductId == 100));
    }

    [Fact]
    public async Task ApplyAsync_SurplusThenShortage_RebuildsCostingThroughTheApplicationUseCases()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedOpeningRestockAsync(options, BusinessA, 100, quantity: 10, unitCost: 2m);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            // Surplus: a positive Restock costed at the entered unit cost.
            await StoreFor(db).ApplyAsync(100, InventoryCountMovementKind.Increase, 10, 4m, CancellationToken.None);
        }

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            // Shortage: a negative Correction costed at the rebuilt average (60 / 20 = 3).
            var application = await StoreFor(db)
                .ApplyAsync(100, InventoryCountMovementKind.Decrease, -5, null, CancellationToken.None);
            Assert.Equal(15, application.QuantityInStock);
        }

        await using var verify = TestAppDbContext.For(options, BusinessA);
        var product = await verify.Products.SingleAsync(p => p.Id == 100);
        Assert.Equal(15, product.QuantityInStock);
        Assert.Equal(15, product.CostingQuantity);
        Assert.Equal(45m, product.InventoryValue);
        Assert.Equal(3m, product.AverageUnitCost);

        var shortage = await verify.StockAdjustments.SingleAsync(a => a.ProductId == 100 && a.QuantityChange == -5);
        Assert.Equal(StockAdjustmentReason.Correction, shortage.Reason);
        Assert.Equal(3m, shortage.UnitCost);
        Assert.Equal(15m, shortage.TotalCost);
        Assert.Equal(15, shortage.CostingQuantityAfter);
    }
}
