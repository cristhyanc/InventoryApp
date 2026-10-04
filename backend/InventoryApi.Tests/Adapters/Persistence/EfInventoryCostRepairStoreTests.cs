using Inventory.Domain.Exceptions;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Tenancy;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The temporary API-owned <see cref="EfInventoryCostRepairStore"/> and the costing-repair use cases
/// (issue #359) over relational SQLite. The central business query filter and the ownership stamp on
/// save (issue #64) are what keep a repair from reading or valuing another business's product, and
/// the adapter adds no business filter of its own.
///
/// The apply transaction is relational here, so the rule that matters most is proven rather than
/// assumed: a repair that leaves the cost history incomplete persists nothing at all.
///
/// Both businesses are seeded in the same state a repair exists for - a transition baseline with an
/// opening costing quantity of zero and two later completed sales the ledger cannot cost.
/// </summary>
public class EfInventoryCostRepairStoreTests
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;
    private const long ProductA = 100;
    private const long ProductB = 200;
    private const string Reason = "Machine stock at the 2026 cutover was never costed.";
    private static readonly DateTime Now = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_repair_is_stamped_with_the_callers_business_and_leaves_the_other_business_untouched()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);

        await using (var a = TestAppDbContext.For(options, BusinessA))
        {
            var preview = await TestCostingUseCases.PreviewRepair(a).Handle(new(ProductA, Day(2), 4, 2m, Reason));
            await TestCostingUseCases.ApplyRepair(a, Actor(), clock: new FakeClock(Now))
                .Handle(new(ProductA, Day(2), 4, 2m, Reason, preview.LedgerFingerprint));
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        var repair = await verify.InventoryCostRepairs.SingleAsync();
        Assert.Equal((BusinessA, ProductA, 4, 2m, 8m), (repair.BusinessId, repair.ProductId, repair.Quantity, repair.UnitCost, repair.TotalValue));
        var productA = await verify.Products.SingleAsync(x => x.Id == ProductA);
        Assert.Equal((2, 4m, 2m), (productA.CostingQuantity, productA.InventoryValue, productA.AverageUnitCost));
        Assert.Equal(10, productA.QuantityInStock);
        var productB = await verify.Products.SingleAsync(x => x.Id == ProductB);
        Assert.Null(productB.CostingQuantity);
        Assert.Null(productB.InventoryValue);
        Assert.All(
            await verify.NayaxSales.Where(x => x.BusinessId == BusinessB).ToListAsync(),
            sale => Assert.Null(sale.UnitCostAtSale));
    }

    [Fact]
    public async Task A_business_cannot_preview_apply_or_read_repairs_for_another_businesss_product()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);
        string fingerprintA;
        await using (var a = TestAppDbContext.For(options, BusinessA))
            fingerprintA = (await TestCostingUseCases.PreviewRepair(a).Handle(new(ProductA, Day(2), 4, 2m, Reason))).LedgerFingerprint;

        await using (var b = TestAppDbContext.For(options, BusinessB))
        {
            var preview = await Assert.ThrowsAsync<DomainValidationException>(() =>
                TestCostingUseCases.PreviewRepair(b).Handle(new(ProductA, Day(2), 4, 2m, Reason)));
            var apply = await Assert.ThrowsAsync<DomainValidationException>(() =>
                TestCostingUseCases.ApplyRepair(b, Actor(), clock: new FakeClock(Now))
                    .Handle(new(ProductA, Day(2), 4, 2m, Reason, fingerprintA)));
            var history = await Assert.ThrowsAsync<DomainValidationException>(() =>
                TestCostingUseCases.RepairHistory(b).Handle(ProductA));

            Assert.Equal($"Product {ProductA} does not exist.", preview.Message);
            Assert.Equal($"Product {ProductA} does not exist.", apply.Message);
            Assert.Equal($"Product {ProductA} does not exist.", history.Message);
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Empty(verify.InventoryCostRepairs);
    }

    /// <summary>
    /// A repair must not value another business's inventory: the ledger the replay and the rebuild
    /// read is business-scoped, so business B's product replays as if A's repair did not exist.
    /// </summary>
    [Fact]
    public async Task Another_businesss_repair_does_not_reach_the_callers_cost_ledger()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);
        await using (var a = TestAppDbContext.For(options, BusinessA))
        {
            a.InventoryCostRepairs.Add(Repair(ProductA, 4, 2m, Day(2)));
            a.InventoryCostRepairs.Add(Repair(ProductB, 999, 99m, Day(2)));
            await Assert.ThrowsAsync<CrossBusinessAccessException>(() => a.SaveChangesAsync());
            a.ChangeTracker.Clear();
            a.InventoryCostRepairs.Add(Repair(ProductA, 4, 2m, Day(2)));
            await a.SaveChangesAsync();
        }

        await using (var b = TestAppDbContext.For(options, BusinessB))
        {
            Assert.Empty(await new EfInventoryCostRepairStore(b).ListAsync(ProductB, CancellationToken.None));

            var preview = await TestCostingUseCases.PreviewRepair(b).Handle(new(ProductB, Day(2), 1, 7m, Reason));

            Assert.Equal((0, 0m), (preview.CostingQuantityBefore, preview.InventoryValueBefore));
            Assert.Equal((1, 7m), (preview.CostingQuantityAfter, preview.InventoryValueAfter));
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        var repair = await verify.InventoryCostRepairs.SingleAsync();
        Assert.Equal((BusinessA, ProductA), (repair.BusinessId, repair.ProductId));
    }

    /// <summary>
    /// Issue #359: a repair that explains only part of the missing costing history leaves a fatal
    /// data-quality issue, the rebuild refuses it, and the transaction takes the repair row with it.
    /// Nothing is left behind - no repair, no half-costed sale, no changed product position.
    /// </summary>
    [Fact]
    public async Task A_repair_that_leaves_the_history_incomplete_persists_nothing()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);

        await using (var a = TestAppDbContext.For(options, BusinessA))
        {
            var preview = await TestCostingUseCases.PreviewRepair(a).Handle(new(ProductA, Day(2), 1, 2m, Reason));
            Assert.Single(preview.RemainingFatalIssues);

            var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
                TestCostingUseCases.ApplyRepair(a, Actor(), clock: new FakeClock(Now))
                    .Handle(new(ProductA, Day(2), 1, 2m, Reason, preview.LedgerFingerprint)));

            Assert.StartsWith(
                "This repair does not complete the product's cost history, so nothing was saved:",
                exception.Message,
                StringComparison.Ordinal);
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Empty(verify.InventoryCostRepairs);
        var productA = await verify.Products.SingleAsync(x => x.Id == ProductA);
        Assert.Null(productA.CostingQuantity);
        Assert.Null(productA.InventoryValue);
        Assert.All(
            await verify.NayaxSales.Where(x => x.ProductName == "Coke Zero").ToListAsync(),
            sale => Assert.Equal(SaleCostingStatus.Pending, sale.CostingStatus));
    }

    /// <summary>
    /// Issue #359: applying a repair must never change <c>Product.QuantityInStock</c>, and never a
    /// stored <c>StockAdjustment</c> row (home refills and write-offs are the MachineRefill history
    /// and the physical movement audit trail).
    ///
    /// The stored physical quantity and the movement history deliberately disagree here - 7 on the
    /// product against a baseline of 13 less a 3-unit refill and a 1-unit write-off, so the replay
    /// ends at 9 - because that is the only state in which the violation is visible. A rebuild
    /// stages the replayed physical quantity onto the product and the replayed running position onto
    /// every movement; a costing repair has no authority over either, so the apply rebuilds
    /// costing-only and both stay exactly as they were, while the repair, the costing position and
    /// the sale costs the repaired history supports are saved.
    ///
    /// The write-off keeps its stored <c>UnitCost</c>/<c>TotalCost</c> of <c>null</c> for the same
    /// reason, even though the repaired replay can now cost it at 2.00: recosting a stock movement
    /// is explicitly out of scope for #359, which recosts completed sales only. See
    /// docs/architecture.md § Costing repairs.
    /// </summary>
    [Fact]
    public async Task A_repair_changes_neither_physical_stock_nor_a_stored_movement()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedPhysicalDriftAsync(connection);

        await using (var a = TestAppDbContext.For(options, BusinessA))
        {
            var preview = await TestCostingUseCases.PreviewRepair(a).Handle(new(ProductA, Day(2), 5, 2m, Reason));
            Assert.Empty(preview.RemainingFatalIssues);

            var applied = await TestCostingUseCases.ApplyRepair(a, Actor(), clock: new FakeClock(Now))
                .Handle(new(ProductA, Day(2), 5, 2m, Reason, preview.LedgerFingerprint));

            Assert.Equal((2, 4m, 2m, 2), (applied.CostingQuantity, applied.InventoryValue, applied.AverageUnitCost, applied.RecostedSaleCount));
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        var product = await verify.Products.SingleAsync(x => x.Id == ProductA);
        Assert.Equal(7, product.QuantityInStock);
        Assert.Equal((2, 4m, 2m), (product.CostingQuantity, product.InventoryValue, product.AverageUnitCost));

        var repair = await verify.InventoryCostRepairs.SingleAsync();
        Assert.Equal((BusinessA, ProductA, Day(2), 5, 2m, 10m), (repair.BusinessId, repair.ProductId, repair.EffectiveAt, repair.Quantity, repair.UnitCost, repair.TotalValue));

        var refill = await verify.StockAdjustments.SingleAsync(x => x.Reason == StockAdjustmentReason.MachineRefill);
        Assert.Equal((4, 9, 18m, 2m), (refill.QuantityAfter, refill.CostingQuantityAfter, refill.InventoryValueAfter, refill.AverageUnitCostAfter));
        Assert.Equal((1.5m, 4.5m), (refill.UnitCost, refill.TotalCost));
        var writeOff = await verify.StockAdjustments.SingleAsync(x => x.Reason == StockAdjustmentReason.Damaged);
        Assert.Equal((3, 0, 0m, (decimal?)null), (writeOff.QuantityAfter, writeOff.CostingQuantityAfter, writeOff.InventoryValueAfter, writeOff.AverageUnitCostAfter));
        Assert.Null(writeOff.UnitCost);
        Assert.Null(writeOff.TotalCost);

        Assert.All(
            await verify.NayaxSales.Where(x => x.BusinessId == BusinessA).ToListAsync(),
            sale => Assert.Equal((2m, 2m, SaleCostingStatus.Costed, SaleCostSource.InventoryLedger),
                (sale.UnitCostAtSale, sale.CostOfGoodsSold, sale.CostingStatus, sale.CostSource)));
    }

    [Fact]
    public async Task A_caller_without_a_business_reads_nothing_and_cannot_apply_a_repair()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);

        await using (var denied = TestAppDbContext.Denied(options))
        {
            var store = new EfInventoryCostRepairStore(denied);
            Assert.Null(await store.GetProductAsync(ProductA, CancellationToken.None));
            Assert.Empty(await store.ListAsync(ProductA, CancellationToken.None));
            await Assert.ThrowsAsync<DomainValidationException>(() =>
                TestCostingUseCases.PreviewRepair(denied).Handle(new(ProductA, Day(2), 4, 2m, Reason)));
            await Assert.ThrowsAsync<DomainValidationException>(() =>
                TestCostingUseCases.ApplyRepair(denied, Actor(), clock: new FakeClock(Now))
                    .Handle(new(ProductA, Day(2), 4, 2m, Reason, "any")));
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Empty(verify.InventoryCostRepairs);
    }

    private static FakeAuthenticatedActorAccessor Actor()
    {
        Assert.True(ActorIdentity.TryCreate(
            "33333333-3333-3333-3333-333333333333", "44444444-4444-4444-4444-444444444444", out var actor));
        return FakeAuthenticatedActorAccessor.Identified(actor!);
    }

    private static InventoryCostRepair Repair(long productId, int quantity, decimal unitCost, DateTime effectiveAt) =>
        new()
        {
            ProductId = productId,
            EffectiveAt = effectiveAt,
            Quantity = quantity,
            UnitCost = unitCost,
            TotalValue = quantity * unitCost,
            Reason = Reason,
            CreatedAt = Now,
            CreatedByDirectoryTenantId = "33333333-3333-3333-3333-333333333333",
            CreatedByObjectId = "44444444-4444-4444-4444-444444444444"
        };

    private static async Task<DbContextOptions<AppDbContext>> SeedTwoBusinessesAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var setup = TestAppDbContext.Unrestricted(options))
            await setup.Database.EnsureCreatedAsync();

        await SeedBusinessAsync(options, BusinessA, ProductA, "Coke Zero");
        await SeedBusinessAsync(options, BusinessB, ProductB, "Business B Product");
        return options;
    }

    private static async Task SeedBusinessAsync(
        DbContextOptions<AppDbContext> options,
        int businessId,
        long productId,
        string productName)
    {
        await using var db = TestAppDbContext.For(options, businessId);
        db.Products.Add(new Product { Id = productId, Name = productName, QuantityInStock = 10 });
        db.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
        {
            ProductId = productId,
            CutoffAt = Day(1),
            HomeStockQuantity = 10,
            OpeningCostingQuantity = 0,
            InventoryValue = 0m,
            AverageUnitCost = 0m
        });
        db.NayaxSales.AddRange(
            Sale(productId, productName, productId * 10, Day(3)),
            Sale(productId, productName, (productId * 10) + 1, Day(4)));
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// One business whose stored physical quantity (7) deliberately disagrees with what its movement
    /// history replays to (a baseline of 13, a 3-unit machine refill and a 1-unit damaged write-off,
    /// so 9), with both movements carrying the stale running position and cost an earlier rebuild of
    /// a different history left on them. A repair of 5 units at 2.00 completes the costing history
    /// for the write-off and both completed sales.
    /// </summary>
    private static async Task<DbContextOptions<AppDbContext>> SeedPhysicalDriftAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var setup = TestAppDbContext.Unrestricted(options))
            await setup.Database.EnsureCreatedAsync();

        await using var db = TestAppDbContext.For(options, BusinessA);
        db.Products.Add(new Product { Id = ProductA, Name = "Coke Zero", QuantityInStock = 7 });
        db.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
        {
            ProductId = ProductA,
            CutoffAt = Day(1),
            HomeStockQuantity = 13,
            OpeningCostingQuantity = 0,
            InventoryValue = 0m,
            AverageUnitCost = 0m
        });
        db.StockAdjustments.AddRange(
            new StockAdjustment
            {
                ProductId = ProductA,
                QuantityChange = -3,
                Reason = StockAdjustmentReason.MachineRefill,
                EffectiveAt = Day(1).AddHours(6),
                QuantityAfter = 4,
                CostingQuantityAfter = 9,
                InventoryValueAfter = 18m,
                AverageUnitCostAfter = 2m,
                UnitCost = 1.5m,
                TotalCost = 4.5m
            },
            new StockAdjustment
            {
                ProductId = ProductA,
                QuantityChange = -1,
                Reason = StockAdjustmentReason.Damaged,
                EffectiveAt = Day(2).AddHours(6),
                QuantityAfter = 3,
                CostingQuantityAfter = 0,
                InventoryValueAfter = 0m,
                AverageUnitCostAfter = null,
                UnitCost = null,
                TotalCost = null
            });
        db.NayaxSales.AddRange(
            Sale(ProductA, "Coke Zero", ProductA * 10, Day(3)),
            Sale(ProductA, "Coke Zero", (ProductA * 10) + 1, Day(4)));
        await db.SaveChangesAsync();
        return options;
    }

    private static NayaxSales Sale(long productId, string productName, long transactionId, DateTime at) =>
        new()
        {
            TransactionID = transactionId,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = 1,
            NayaxProductId = productId,
            ProductName = productName,
            MachineAuthorizationTime = at
        };

    private static DateTime Day(int day) => new(2026, 1, day, 12, 0, 0, DateTimeKind.Utc);
}
