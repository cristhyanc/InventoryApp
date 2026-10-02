using Inventory.Application.Costing;
using Inventory.Domain.FinancialConfiguration;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DomainStock = Inventory.Domain.Stock;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Two-business isolation for the temporary API-owned costing adapters (issue #296):
/// <see cref="EfInventoryMovementStore"/> and <see cref="EfInventoryCostLedgerStore"/>. Relational
/// (SQLite) because the central business query filter and the ownership stamp on save (issue #64)
/// are what keep a movement or a cost rebuild from reading or writing another business's product,
/// movements or sales; neither adapter adds a business filter of its own.
/// </summary>
public class EfInventoryCostingAdaptersTenancyTests
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;
    private const long ProductA = 100;
    private const long ProductB = 200;
    private static readonly DateTime OpeningAt = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Recording_a_movement_cannot_see_or_change_another_businesss_product()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            Assert.Null(await new EfInventoryMovementStore(db).GetCostStateAsync(ProductB, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => TestCostingUseCases.RecordMovement(db).RecordAsync(
                new InventoryMovement(ProductB, -1, DomainStock.StockAdjustmentReason.Correction, "Cross-business"),
                CancellationToken.None));
            await db.SaveChangesAsync();
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        var productB = await verify.Products.SingleAsync(p => p.Id == ProductB);
        Assert.Equal(5, productB.QuantityInStock);
        Assert.Equal(1, await verify.StockAdjustments.CountAsync(a => a.ProductId == ProductB));
    }

    [Fact]
    public async Task A_recorded_movement_and_its_rebuild_stay_inside_the_callers_business()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var staged = await TestCostingUseCases.RecordMovement(db).RecordAsync(
                new InventoryMovement(ProductA, -4, DomainStock.StockAdjustmentReason.Correction, "Count"),
                CancellationToken.None);
            await db.SaveChangesAsync();
            await TestCostingUseCases.Rebuild(db).RebuildAsync(ProductA, OpeningAt);
            await db.SaveChangesAsync();
            Assert.True(staged.Id > 0);
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        var recorded = await verify.StockAdjustments.SingleAsync(a => a.ProductId == ProductA && a.QuantityChange == -4);
        Assert.Equal(BusinessA, recorded.BusinessId);
        Assert.Equal(2m, recorded.UnitCost);

        var productA = await verify.Products.SingleAsync(p => p.Id == ProductA);
        Assert.Equal(6, productA.QuantityInStock);
        Assert.Equal(6, productA.CostingQuantity);
        Assert.Equal(12m, productA.InventoryValue);

        // Business B's completed sale names business A's product ID; it must be neither consumed by
        // business A's replay nor recosted by it.
        var saleB = await verify.NayaxSales.SingleAsync();
        Assert.Equal(BusinessB, saleB.BusinessId);
        Assert.Null(saleB.UnitCostAtSale);
        Assert.Equal(SaleCostingStatus.Pending, saleB.CostingStatus);

        var productB = await verify.Products.SingleAsync(p => p.Id == ProductB);
        Assert.Equal(5, productB.QuantityInStock);
        Assert.Null(productB.CostingQuantity);
    }

    [Fact]
    public async Task The_cost_ledger_reads_only_the_callers_business()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfInventoryCostLedgerStore(db);

        var ledger = await store.LoadAsync(ProductA, forUpdate: false, CancellationToken.None);
        Assert.NotNull(ledger);
        Assert.Single(ledger!.Adjustments);
        Assert.Empty(ledger.Sales);

        var asOf = await store.LoadAsOfAsync(ProductA, OpeningAt.AddDays(5), CancellationToken.None);
        Assert.NotNull(asOf);
        Assert.Empty(asOf!.Sales);

        Assert.Null(await store.LoadAsync(ProductB, forUpdate: true, CancellationToken.None));
        Assert.Null(await store.LoadAsOfAsync(ProductB, OpeningAt.AddDays(5), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => TestCostingUseCases.Rebuild(db).RebuildAsync(ProductB));
        Assert.Null(await TestCostingUseCases.Rebuild(db).GetAverageUnitCostAtAsync(ProductB, OpeningAt.AddDays(5)));
        Assert.Equal(2m, await TestCostingUseCases.Rebuild(db).GetAverageUnitCostAtAsync(ProductA, OpeningAt.AddDays(5)));
    }

    [Fact]
    public async Task A_caller_without_a_business_reads_no_ledger_and_records_no_movement()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);

        await using var db = TestAppDbContext.Denied(options);

        Assert.Null(await new EfInventoryCostLedgerStore(db).LoadAsync(ProductA, forUpdate: true, CancellationToken.None));
        Assert.Null(await new EfInventoryMovementStore(db).GetCostStateAsync(ProductA, CancellationToken.None));
    }

    private static async Task<DbContextOptions<AppDbContext>> SeedTwoBusinessesAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var setup = TestAppDbContext.Unrestricted(options))
            await setup.Database.EnsureCreatedAsync();

        await using (var a = TestAppDbContext.For(options, BusinessA))
        {
            a.Products.Add(new Product { Id = ProductA, Name = "Coke Zero", QuantityInStock = 10 });
            a.StockAdjustments.Add(Restock(ProductA, 10, 2m));
            await a.SaveChangesAsync();
        }

        await using (var b = TestAppDbContext.For(options, BusinessB))
        {
            b.Products.Add(new Product { Id = ProductB, Name = "Business B Product", QuantityInStock = 5 });
            b.StockAdjustments.Add(Restock(ProductB, 5, 9m));
            b.NayaxSales.Add(new NayaxSales
            {
                TransactionID = 5001,
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                MachineID = 1,
                NayaxProductId = ProductA,
                ProductName = "Coke Zero",
                SettlementValue = 3m,
                MachineAuthorizationTime = OpeningAt.AddDays(1),
            });
            await b.SaveChangesAsync();
        }

        return options;
    }

    private static StockAdjustment Restock(long productId, int quantity, decimal unitCost) => new()
    {
        ProductId = productId,
        QuantityChange = quantity,
        QuantityAfter = quantity,
        Reason = StockAdjustmentReason.Restock,
        UnitCost = unitCost,
        EffectiveAt = OpeningAt,
    };
}
