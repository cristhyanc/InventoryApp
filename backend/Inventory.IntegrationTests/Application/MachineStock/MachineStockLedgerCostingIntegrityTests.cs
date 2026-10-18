using Inventory.Application.MachineStock;
using Inventory.Application.Stock;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.MachineStock;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using DomainStock = Inventory.Domain.Stock;

namespace InventoryApi.Tests.Application.MachineStock;

/// <summary>
/// The ledger-integrity and no-double-counting regressions for the machine-stock ledger (issue #56),
/// over relational SQLite and through the real adapters, because what is being proved is the
/// relationship between three persisted things at once: physical storage stock, business-owned
/// costing inventory, and the machine's own expected stock.
///
/// The invariants under test are the ones AGENTS.md § Inventory and historical costing invariants
/// states:
/// <list type="bullet">
/// <item>a machine refill moves storage stock and leaves <c>CostingQuantity</c>/<c>InventoryValue</c>
/// untouched, creating no COGS;</item>
/// <item>a completed sale reduces costing inventory and records COGS exactly once, however many times
/// costing is re-run;</item>
/// <item>recording a machine count changes none of those four values at all;</item>
/// <item>and the machine's expected stock follows the refill and the sale exactly once each.</item>
/// </list>
/// </summary>
public class MachineStockLedgerCostingIntegrityTests
{
    private const int BusinessA = 1;
    private const long ProductId = 100;
    private const long MachineId = 42;
    /// <summary>
    /// The instants are anchored to this run rather than to a fixed calendar date, because the
    /// movements below are applied through the real stock adapter, which stamps a movement's
    /// <c>EffectiveAt</c> with the instant it is recorded. The count and the sale therefore have to
    /// be after the movements for the ledger order under test to be the intended one; a fixed past
    /// date would put every movement after them and test a different history.
    /// </summary>
    private static readonly DateTime RecordedAt = DateTime.UtcNow;
    private static readonly DateTime CountedAt = RecordedAt.AddMinutes(10);
    private static readonly DateTime SoldAt = RecordedAt.AddMinutes(20);

    [Fact]
    public async Task A_refill_a_count_and_a_sale_each_move_exactly_what_they_are_allowed_to()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedAsync(connection);

        // A costed purchase: ten units in storage, ten owned by the business at $2.
        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            await StockStore(db).ApplyAsync(
                ProductId,
                new ManualStockAdjustmentInput(
                    10, DomainStock.StockAdjustmentReason.Restock, "Opening purchase",
                    MachineId: null, EatBefore: null, UnitCost: 2m),
                CancellationToken.None);
        }

        await AssertProductAsync(options, quantityInStock: 10, costingQuantity: 10, inventoryValue: 20m);

        // The refill: six units leave storage for the machine. Business-owned costing inventory is
        // unchanged, because the business still owns all ten units.
        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            await StockStore(db).ApplyAsync(
                ProductId,
                new ManualStockAdjustmentInput(
                    -6, DomainStock.StockAdjustmentReason.MachineRefill, "Refill",
                    MachineId, EatBefore: null, UnitCost: null),
                CancellationToken.None);
        }

        await AssertProductAsync(options, quantityInStock: 4, costingQuantity: 10, inventoryValue: 20m);
        await AssertNoSaleCostAsync(options);

        // The operator counts the machine: six units, exactly what was refilled. A count writes
        // nothing but itself.
        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var count = await new RecordMachineStockCount(new EfMachineStockLedgerStore(db), new FakeClock(CountedAt))
                .Handle(MachineId, new MachineStockCountRequestDto(ProductId, 6, "Counted at refill"), CancellationToken.None);
            Assert.NotNull(count);
            Assert.Equal(CountedAt, count!.CountedAtUtc);
        }

        await AssertProductAsync(options, quantityInStock: 4, costingQuantity: 10, inventoryValue: 20m);
        await AssertNoSaleCostAsync(options);

        // One completed sale on that machine, then the costing run and the ledger rebuild the sales
        // sync performs.
        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            db.NayaxSales.Add(CompletedSale());
            await db.SaveChangesAsync();

            Assert.Equal(1, await TestCostingUseCases.CostPendingSales(db).Handle());
            await TestCostingUseCases.Rebuild(db).RebuildAsync(ProductId, cancellationToken: CancellationToken.None);
            await db.SaveChangesAsync();
        }

        // The sale consumed one unit of costing inventory at the $2 average and recorded its COGS.
        // Physical storage stock is untouched: the unit left the machine, not the storeroom.
        await AssertProductAsync(options, quantityInStock: 4, costingQuantity: 9, inventoryValue: 18m);
        await using (var verify = TestAppDbContext.Unrestricted(options))
        {
            var sale = await verify.NayaxSales.SingleAsync();
            Assert.Equal(2m, sale.UnitCostAtSale);
            Assert.Equal(2m, sale.CostOfGoodsSold);
            Assert.Equal(SaleCostingStatus.Costed, sale.CostingStatus);
            Assert.Equal(SaleCostSource.InventoryLedger, sale.CostSource);
        }

        // Re-running costing and the rebuild is idempotent: the sale is costed once and the costing
        // position does not drift, which is what makes the ledger safe to re-derive.
        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            Assert.Equal(0, await TestCostingUseCases.CostPendingSales(db).Handle());
            await TestCostingUseCases.Rebuild(db).RebuildAsync(ProductId, cancellationToken: CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await AssertProductAsync(options, quantityInStock: 4, costingQuantity: 9, inventoryValue: 18m);
        await using (var verify = TestAppDbContext.Unrestricted(options))
        {
            var sale = await verify.NayaxSales.SingleAsync();
            Assert.Equal(2m, sale.CostOfGoodsSold);
            Assert.Equal(2m, (await verify.NayaxSales.ToListAsync()).Sum(s => s.CostOfGoodsSold ?? 0m));
        }

        // The machine's own ledger: counted six, sold one, so five are expected - the refill before
        // the count is already inside the counted level, and the sale is deducted exactly once.
        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var reconciliation = await Reconciliation(db, observedPar: 10, observedMissing: 5)
                .Handle(MachineId, CancellationToken.None);

            var row = Assert.Single(reconciliation.Products);
            Assert.Equal(5, row.ExpectedQuantity);
            Assert.Equal(5, row.ObservedQuantity);
            Assert.Equal(0, row.DiscrepancyQuantity);
            Assert.Equal(MachineStockReconciliationStatus.Balanced, row.Status);
            Assert.Equal(MachineStockBaselineSource.CountedReconciliation, row.BaselineSource);
            Assert.Equal(CountedAt, row.BaselineAtUtc);
            Assert.Equal(1, row.SoldQuantity);
            Assert.Equal(0, row.RefilledQuantity);
            Assert.Empty(row.DataQuality);
        }

        // The same facts against a machine holding two fewer units: a shortage of two, reported as
        // shrinkage and never written back as a correction.
        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var reconciliation = await Reconciliation(db, observedPar: 10, observedMissing: 7)
                .Handle(MachineId, CancellationToken.None);

            var row = Assert.Single(reconciliation.Products);
            Assert.Equal(5, row.ExpectedQuantity);
            Assert.Equal(3, row.ObservedQuantity);
            Assert.Equal(-2, row.DiscrepancyQuantity);
            Assert.Equal(2, row.ShrinkageQuantity);
            Assert.Equal(MachineStockReconciliationStatus.Shortage, row.Status);
        }

        // Nothing the reconciliation did changed a quantity, a cost or the count history.
        await AssertProductAsync(options, quantityInStock: 4, costingQuantity: 9, inventoryValue: 18m);
        await using (var verify = TestAppDbContext.Unrestricted(options))
        {
            Assert.Equal(6, (await verify.MachineStockCounts.SingleAsync()).CountedQuantity);
            Assert.Equal(2m, (await verify.NayaxSales.SingleAsync()).CostOfGoodsSold);
        }
    }

    /// <summary>
    /// The reverse transfer: units taken back out of the machine into storage. It is the same
    /// costing-neutral <c>MachineRefill</c> movement with a positive storage quantity, so storage
    /// goes up, the machine's expected stock goes down, and business-owned costing inventory stays
    /// exactly where it was.
    /// </summary>
    [Fact]
    public async Task A_return_from_the_machine_to_storage_moves_no_costing_inventory()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedAsync(connection);

        // The machine is counted empty first, so both transfers below replay on top of that
        // baseline rather than being absorbed by a later count.
        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            await new RecordMachineStockCount(
                    new EfMachineStockLedgerStore(db), new FakeClock(RecordedAt.AddMinutes(-10)))
                .Handle(MachineId, new MachineStockCountRequestDto(ProductId, 0, null), CancellationToken.None);
        }

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var store = StockStore(db);
            await store.ApplyAsync(
                ProductId,
                new ManualStockAdjustmentInput(
                    10, DomainStock.StockAdjustmentReason.Restock, "Opening purchase",
                    MachineId: null, EatBefore: null, UnitCost: 2m),
                CancellationToken.None);
            await store.ApplyAsync(
                ProductId,
                new ManualStockAdjustmentInput(
                    -6, DomainStock.StockAdjustmentReason.MachineRefill, "Refill",
                    MachineId, EatBefore: null, UnitCost: null),
                CancellationToken.None);
            await store.ApplyAsync(
                ProductId,
                new ManualStockAdjustmentInput(
                    2, DomainStock.StockAdjustmentReason.MachineRefill, "Pulled two back out",
                    MachineId, EatBefore: null, UnitCost: null),
                CancellationToken.None);
        }

        await AssertProductAsync(options, quantityInStock: 6, costingQuantity: 10, inventoryValue: 20m);
        await AssertNoSaleCostAsync(options);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var row = Assert.Single(
                (await Reconciliation(db, observedPar: 10, observedMissing: 6).Handle(MachineId, CancellationToken.None))
                    .Products);

            Assert.Equal(4, row.ExpectedQuantity);
            Assert.Equal(6, row.RefilledQuantity);
            Assert.Equal(2, row.ReturnedToStorageQuantity);
            Assert.Equal(0, row.SoldQuantity);
            Assert.Equal(MachineStockReconciliationStatus.Balanced, row.Status);
        }
    }

    private static EfStockAdjustmentStore StockStore(AppDbContext db) =>
        new(db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db));

    private static GetMachineStockReconciliation Reconciliation(
        AppDbContext db, int? observedPar, int? observedMissing)
    {
        var observations = new Mock<IMachineStockObservationSource>();
        observations.Setup(x => x.GetObservationAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineStockObservation(
                SoldAt, [new MachineStockObservationSlot(ProductId, observedPar, observedMissing)]));
        return new GetMachineStockReconciliation(
            new EfMachineStockLedgerStore(db), observations.Object, new FakeClock(SoldAt));
    }

    private static async Task AssertProductAsync(
        DbContextOptions<AppDbContext> options, int quantityInStock, int costingQuantity, decimal inventoryValue)
    {
        await using var verify = TestAppDbContext.Unrestricted(options);
        var product = await verify.Products.SingleAsync(p => p.Id == ProductId);

        Assert.Equal(quantityInStock, product.QuantityInStock);
        Assert.Equal(costingQuantity, product.CostingQuantity);
        Assert.Equal(inventoryValue, product.InventoryValue);
    }

    private static async Task AssertNoSaleCostAsync(DbContextOptions<AppDbContext> options)
    {
        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.All(
            await verify.NayaxSales.ToListAsync(),
            sale =>
            {
                Assert.Null(sale.CostOfGoodsSold);
                Assert.Null(sale.UnitCostAtSale);
            });
    }

    private static NayaxSales CompletedSale() => new()
    {
        TransactionID = 5001,
        TransactionStatusId = NayaxTransactionStatusIds.Completed,
        MachineID = MachineId,
        ProductId = ProductId,
        NayaxProductId = ProductId,
        ProductName = "Coke Zero",
        SettlementValue = 3m,
        MachineAuthorizationTime = SoldAt,
    };

    private static async Task<DbContextOptions<AppDbContext>> SeedAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var setup = TestAppDbContext.Unrestricted(options))
            await setup.Database.EnsureCreatedAsync();

        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            seed.Products.Add(new Product
            {
                Id = ProductId,
                Name = "Coke Zero",
                QuantityInStock = 0,
                CostingQuantity = 0,
                InventoryValue = 0m,
                AverageUnitCost = 0m,
                CreatedAt = RecordedAt,
                UpdatedAt = RecordedAt,
            });
            await seed.SaveChangesAsync();
        }

        return options;
    }
}
