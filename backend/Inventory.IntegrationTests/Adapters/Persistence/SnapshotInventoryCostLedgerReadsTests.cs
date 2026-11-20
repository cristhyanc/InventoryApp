using Inventory.Application.Costing;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// <see cref="SnapshotInventoryCostLedgerReads"/> (issue #1304) against the real
/// <see cref="EfInventoryCostLedgerStore"/> on SQLite: every as-of read it answers from its one read
/// of the product's ledger is the read the database itself answers, and every cost it leads to is the
/// cost the ordinary per-row read leads to - including at the exact instant of an event, one tick
/// either side, at sub-second instants, before everything and after everything, and across a
/// transition baseline that is cut off after the instant asked about (a product has at most one).
/// </summary>
public class SnapshotInventoryCostLedgerReadsTests
{
    private const int Business = 1;
    private const long Product = 100;
    private const long OtherProduct = 200;
    private static readonly DateTime Opening = new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Every_as_of_read_is_the_databases_own_and_every_cost_is_the_same()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedAsync(connection, withBaselines: false);

        await using var db = TestAppDbContext.For(options, Business);
        var direct = new EfInventoryCostLedgerStore(db);
        var snapshot = new SnapshotInventoryCostLedgerReads(direct);

        foreach (var asOf in await InstantsAsync(db))
            await AssertSameLedgerAsync(direct, snapshot, Product, asOf);

        await AssertSameCostsAsync(db, direct);
    }

    [Fact]
    public async Task A_baseline_cut_off_after_the_instant_is_read_from_the_database()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedAsync(connection, withBaselines: true);

        await using var db = TestAppDbContext.For(options, Business);
        var direct = new EfInventoryCostLedgerStore(db);
        var snapshot = new SnapshotInventoryCostLedgerReads(direct);

        foreach (var asOf in await InstantsAsync(db))
            await AssertSameLedgerAsync(direct, snapshot, Product, asOf);

        // Before the baseline's cutoff no baseline is in range, which the full read - carrying the
        // baseline - cannot say by itself: the answer must be the database's, with none.
        var before = await snapshot.LoadAsOfAsync(Product, Opening.AddDays(3), CancellationToken.None);
        Assert.Null(before!.Baseline);
        var after = await snapshot.LoadAsOfAsync(Product, Opening.AddDays(6), CancellationToken.None);
        Assert.Equal(Opening.AddDays(5), after!.Baseline!.CutoffAt);

        await AssertSameCostsAsync(db, direct);
    }

    [Fact]
    public async Task A_products_ledger_is_read_once_however_many_instants_are_asked_about()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedAsync(connection, withBaselines: false);

        await using var db = TestAppDbContext.For(options, Business);
        var counting = new CountingLedgerStore(new EfInventoryCostLedgerStore(db));
        var snapshot = new SnapshotInventoryCostLedgerReads(counting);

        foreach (var asOf in await InstantsAsync(db))
        {
            await snapshot.LoadAsOfAsync(Product, asOf, CancellationToken.None);
            await snapshot.LoadAsOfAsync(OtherProduct, asOf, CancellationToken.None);
        }

        Assert.Equal(2, counting.FullReads);
        Assert.Equal(0, counting.AsOfReads);
    }

    [Fact]
    public async Task A_product_the_caller_cannot_see_is_absent_as_it_is_from_the_database()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedAsync(connection, withBaselines: false);

        await using var db = TestAppDbContext.For(options, Business + 1);
        var snapshot = new SnapshotInventoryCostLedgerReads(new EfInventoryCostLedgerStore(db));

        Assert.Null(await snapshot.LoadAsOfAsync(Product, Opening.AddYears(1), CancellationToken.None));
        Assert.Null(await snapshot.LoadAsOfAsync(9999, Opening.AddYears(1), CancellationToken.None));
    }

    private static async Task AssertSameLedgerAsync(
        IInventoryCostLedgerStore direct, IInventoryCostLedgerStore snapshot, long productId, DateTime asOf)
    {
        var expected = await direct.LoadAsOfAsync(productId, asOf, CancellationToken.None);
        var actual = await snapshot.LoadAsOfAsync(productId, asOf, CancellationToken.None);

        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected!.Product, actual!.Product);
        Assert.Equal(expected.Baseline, actual.Baseline);
        Assert.Equal(
            expected.Adjustments.OrderBy(x => x.Id).ToList(), actual.Adjustments.OrderBy(x => x.Id).ToList());
        Assert.Equal(
            expected.Sales.OrderBy(x => x.TransactionId).ToList(),
            actual.Sales.OrderBy(x => x.TransactionId).ToList());
        Assert.Equal(expected.Repairs.OrderBy(x => x.Id).ToList(), actual.Repairs.OrderBy(x => x.Id).ToList());
    }

    /// <summary>
    /// The cost the sale-time path gives every stored sale, through the ordinary per-row read and
    /// through one snapshot for all of them.
    /// </summary>
    private static async Task AssertSameCostsAsync(AppDbContext db, IInventoryCostLedgerStore direct)
    {
        var perRow = new RebuildProductCost(direct);
        var batched = new RebuildProductCost(new SnapshotInventoryCostLedgerReads(direct));
        var sales = await db.NayaxSales.AsNoTracking().Where(s => s.ProductId == Product).ToListAsync();
        Assert.NotEmpty(sales);

        foreach (var sale in sales)
        {
            Assert.Equal(
                await perRow.GetAverageUnitCostAtAsync(Product, sale.MachineAuthorizationTime, sale.TransactionID),
                await batched.GetAverageUnitCostAtAsync(Product, sale.MachineAuthorizationTime, sale.TransactionID));
            Assert.Equal(
                await perRow.GetLatestPurchaseUnitCostBeforeAsync(Product, sale.MachineAuthorizationTime, sale.TransactionID),
                await batched.GetLatestPurchaseUnitCostBeforeAsync(Product, sale.MachineAuthorizationTime, sale.TransactionID));
        }

        // A sale not stored yet, as a new row of a batch is, at each instant asked about.
        foreach (var at in await InstantsAsync(db))
            Assert.Equal(
                await perRow.GetAverageUnitCostAtAsync(Product, at, 990_000),
                await batched.GetAverageUnitCostAtAsync(Product, at, 990_000));
    }

    /// <summary>
    /// Every instant an event of the product happens at, one tick before and after each, and the
    /// ends of time the history sits between.
    /// </summary>
    private static async Task<List<DateTime>> InstantsAsync(AppDbContext db)
    {
        var events = new List<DateTime>();
        events.AddRange(await db.StockAdjustments.AsNoTracking().Select(x => x.EffectiveAt).ToListAsync());
        events.AddRange(await db.NayaxSales.AsNoTracking().Select(x => x.MachineAuthorizationTime).ToListAsync());
        events.AddRange(await db.InventoryCostRepairs.AsNoTracking().Select(x => x.EffectiveAt).ToListAsync());
        events.AddRange(await db.InventoryCostTransitionBaselines.AsNoTracking().Select(x => x.CutoffAt).ToListAsync());

        var instants = events
            .SelectMany(x => new[] { x.AddTicks(-1), x, x.AddTicks(1) })
            .Append(Opening.AddYears(-1))
            .Append(Opening.AddYears(1))
            .Distinct()
            .OrderBy(x => x)
            .ToList();
        return instants;
    }

    private static async Task<DbContextOptions<AppDbContext>> SeedAsync(
        SqliteConnection connection, bool withBaselines)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var setup = TestAppDbContext.Unrestricted(options))
            await setup.Database.EnsureCreatedAsync();

        await using var db = TestAppDbContext.For(options, Business);
        db.Products.Add(new Product { Id = Product, Name = "Coke Zero", QuantityInStock = 22 });
        db.Products.Add(new Product { Id = OtherProduct, Name = "Mars Bar", QuantityInStock = 3 });

        db.StockAdjustments.AddRange(
            Movement(Product, Opening, 10, StockAdjustmentReason.Restock, 2m),
            // Sub-second instants, one of them exactly where a sale also happens.
            Movement(Product, Opening.AddDays(2).AddMilliseconds(450), 12, StockAdjustmentReason.Restock, 2.6m),
            Movement(Product, Opening.AddDays(4).AddTicks(5), -1, StockAdjustmentReason.Correction, null),
            Movement(Product, Opening.AddDays(6), 6, StockAdjustmentReason.Restock, 3.1m),
            Movement(OtherProduct, Opening, 3, StockAdjustmentReason.Restock, 1.5m));

        var transactionId = 5000L;
        foreach (var at in new[]
                 {
                     Opening.AddHours(3),
                     Opening.AddDays(1).AddMilliseconds(500),
                     Opening.AddDays(2).AddMilliseconds(450),
                     Opening.AddDays(2).AddMilliseconds(450),
                     Opening.AddDays(3),
                     Opening.AddDays(5).AddTicks(1),
                     Opening.AddDays(7),
                 })
        {
            db.NayaxSales.Add(Sale(transactionId++, Product, at));
        }

        db.NayaxSales.Add(Sale(transactionId, OtherProduct, Opening.AddDays(1)));

        db.InventoryCostRepairs.Add(new InventoryCostRepair
        {
            ProductId = Product,
            EffectiveAt = Opening.AddDays(4),
            Quantity = 2,
            UnitCost = 2.2m,
            TotalValue = 4.4m,
            Reason = "Missing purchase",
            CreatedAt = Opening.AddDays(8),
            CreatedByDirectoryTenantId = "tenant",
            CreatedByObjectId = "object",
        });

        if (withBaselines)
        {
            db.InventoryCostTransitionBaselines.Add(Baseline(Opening.AddDays(5), 20, 20, 50m));
        }

        await db.SaveChangesAsync();
        return options;
    }

    private static StockAdjustment Movement(
        long productId, DateTime at, int quantity, StockAdjustmentReason reason, decimal? unitCost) => new()
        {
            ProductId = productId,
            QuantityChange = quantity,
            QuantityAfter = quantity,
            Reason = reason,
            UnitCost = unitCost,
            EffectiveAt = at,
        };

    private static NayaxSales Sale(long transactionId, long productId, DateTime at) => new()
    {
        TransactionID = transactionId,
        TransactionStatusId = NayaxTransactionStatusIds.Completed,
        MachineID = 1,
        ProductId = productId,
        NayaxProductId = productId,
        ProductName = "Product",
        SettlementValue = 3m,
        MachineAuthorizationTime = at,
    };

    private static InventoryCostTransitionBaseline Baseline(
        DateTime cutoff, int home, int costing, decimal value) => new()
        {
            ProductId = Product,
            CutoffAt = cutoff,
            HomeStockQuantity = home,
            OpeningCostingQuantity = costing,
            InventoryValue = value,
            AverageUnitCost = value / costing,
            CreatedAt = Opening.AddDays(8),
        };

    private sealed class CountingLedgerStore : IInventoryCostLedgerStore
    {
        private readonly IInventoryCostLedgerStore _inner;

        public CountingLedgerStore(IInventoryCostLedgerStore inner) => _inner = inner;

        public int FullReads { get; private set; }

        public int AsOfReads { get; private set; }

        public Task<InventoryCostLedger?> LoadAsync(long productId, bool forUpdate, CancellationToken cancellationToken)
        {
            FullReads++;
            return _inner.LoadAsync(productId, forUpdate, cancellationToken);
        }

        public Task<InventoryCostLedger?> LoadAsOfAsync(long productId, DateTime asOf, CancellationToken cancellationToken)
        {
            AsOfReads++;
            return _inner.LoadAsOfAsync(productId, asOf, cancellationToken);
        }

        public void StageReplay(
            InventoryCostLedger ledger,
            IReadOnlyCollection<Inventory.Domain.Costing.CostReplayAdjustmentOutcome> adjustments,
            IReadOnlyCollection<Inventory.Domain.Costing.CostReplaySaleCost> recostedSales) =>
            _inner.StageReplay(ledger, adjustments, recostedSales);

        public void StageProductPosition(InventoryCostLedger ledger, ProductCostPosition position) =>
            _inner.StageProductPosition(ledger, position);
    }
}
