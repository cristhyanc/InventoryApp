using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Nayax;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// <see cref="EfMachineStockLedgerStore"/> (issue #56) over relational SQLite, because what it reads
/// is three tables at once with the completed-sale predicate and the central tenant query filter
/// applied, and what it writes is one appended row that must leave everything else alone.
///
/// The isolation assertions matter as much as the arithmetic: a machine id is a remote Nayax
/// identity, so two businesses can legitimately be told about the same machine number, and the
/// ledger of one must never contain a movement, a sale, a count or a name from the other.
/// </summary>
public class EfMachineStockLedgerStoreTests
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;
    private const long ProductA = 100;
    private const long ProductB = 200;
    private const long MachineId = 42;
    private static readonly DateTime OpeningAt = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task It_reads_this_machines_refills_sales_counts_and_opening_baseline()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var facts = await new EfMachineStockLedgerStore(db)
            .GetLedgerFactsAsync(MachineId, [], CancellationToken.None);

        var refill = Assert.Single(facts.Refills);
        Assert.Equal(ProductA, refill.ProductId);
        Assert.Equal(-6, refill.StorageQuantityChange);

        var sale = Assert.Single(facts.Sales);
        Assert.Equal(5001, sale.TransactionId);
        Assert.Equal(ProductA, sale.ProductId);

        var count = Assert.Single(facts.Counts);
        Assert.Equal(6, count.CountedQuantity);
        Assert.Equal(DateTimeKind.Utc, count.CountedAtUtc.Kind);

        var baseline = Assert.Single(facts.OpeningBaselines);
        Assert.Equal(ProductA, baseline.ProductId);
        Assert.Equal(4, baseline.StockQuantity);
        Assert.Equal(OpeningAt, baseline.CutoffAtUtc);

        Assert.Equal("Coke Zero", facts.ProductNames[ProductA]);
        Assert.Equal(1, facts.PendingNayaxStockEventCount);
        Assert.Equal(1, facts.UnattributedCompletedSaleCount);
    }

    /// <summary>
    /// Only refills of this machine, and only <c>MachineRefill</c> movements, are machine stock. A
    /// storage-only correction and a refill of another machine are not in this machine's ledger.
    /// </summary>
    [Fact]
    public async Task It_reads_neither_another_machines_refill_nor_a_storage_only_movement()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);

        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            seed.StockAdjustments.Add(new StockAdjustment
            {
                ProductId = ProductA,
                QuantityChange = -2,
                Reason = StockAdjustmentReason.MachineRefill,
                MachineId = 99,
                EffectiveAt = OpeningAt.AddDays(2),
            });
            seed.StockAdjustments.Add(new StockAdjustment
            {
                ProductId = ProductA,
                QuantityChange = -3,
                Reason = StockAdjustmentReason.Correction,
                EffectiveAt = OpeningAt.AddDays(2),
            });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var facts = await new EfMachineStockLedgerStore(db)
            .GetLedgerFactsAsync(MachineId, [], CancellationToken.None);

        Assert.Equal(-6, Assert.Single(facts.Refills).StorageQuantityChange);
    }

    /// <summary>
    /// A sale that is not an approved transaction (status 12) never dispensed a unit, so it is not in
    /// the ledger - the same completed-sale predicate every other sales query filters on.
    /// </summary>
    [Fact]
    public async Task It_reads_only_completed_sales()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);

        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            seed.NayaxSales.Add(new NayaxSales
            {
                TransactionID = 5002,
                TransactionStatusId = 55,
                MachineID = MachineId,
                NayaxProductId = ProductA,
                ProductName = "Coke Zero",
                MachineAuthorizationTime = OpeningAt.AddDays(1),
            });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var facts = await new EfMachineStockLedgerStore(db)
            .GetLedgerFactsAsync(MachineId, [], CancellationToken.None);

        Assert.Equal(5001, Assert.Single(facts.Sales).TransactionId);
    }

    [Fact]
    public async Task It_resolves_the_names_of_the_observed_products_the_caller_owns()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var facts = await new EfMachineStockLedgerStore(db)
            .GetLedgerFactsAsync(MachineId, [ProductA, ProductB, 999], CancellationToken.None);

        Assert.Equal("Coke Zero", facts.ProductNames[ProductA]);
        Assert.DoesNotContain(ProductB, facts.ProductNames.Keys);
        Assert.DoesNotContain(999, facts.ProductNames.Keys);
    }

    [Fact]
    public async Task One_businesss_ledger_holds_nothing_of_the_others_even_on_the_same_machine_id()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);

        await using var db = TestAppDbContext.For(options, BusinessB);
        var facts = await new EfMachineStockLedgerStore(db)
            .GetLedgerFactsAsync(MachineId, [ProductA], CancellationToken.None);

        Assert.Equal(ProductB, Assert.Single(facts.Refills).ProductId);
        Assert.Equal(3, Assert.Single(facts.Counts).CountedQuantity);
        Assert.Empty(facts.OpeningBaselines);
        Assert.Equal(0, facts.PendingNayaxStockEventCount);
        Assert.Equal("Business B Product", facts.ProductNames[ProductB]);
        Assert.DoesNotContain(ProductA, facts.ProductNames.Keys);

        // Business B's own completed sale on the same machine number names business A's product id,
        // which business B cannot see: unattributed, and never attributed to business A's product.
        Assert.Empty(facts.Sales);
        Assert.Equal(1, facts.UnattributedCompletedSaleCount);
    }

    [Fact]
    public async Task A_caller_without_a_business_reads_nothing_at_all()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);

        await using var db = TestAppDbContext.Denied(options);
        var facts = await new EfMachineStockLedgerStore(db)
            .GetLedgerFactsAsync(MachineId, [ProductA], CancellationToken.None);

        Assert.Empty(facts.Refills);
        Assert.Empty(facts.Sales);
        Assert.Empty(facts.Counts);
        Assert.Empty(facts.OpeningBaselines);
        Assert.Empty(facts.ProductNames);
        Assert.Equal(0, facts.PendingNayaxStockEventCount);
        Assert.Equal(0, facts.UnattributedCompletedSaleCount);
    }

    [Fact]
    public async Task Recording_a_count_appends_one_owned_row_and_changes_nothing_else()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);
        var countedAt = OpeningAt.AddDays(3);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var record = await new EfMachineStockLedgerStore(db)
                .RecordCountAsync(MachineId, ProductA, 11, countedAt, "Counted after refill", CancellationToken.None);

            Assert.NotNull(record);
            Assert.Equal("Coke Zero", record!.ProductName);
            Assert.Equal(11, record.CountedQuantity);
            Assert.Equal(countedAt, record.CountedAtUtc);
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        var stored = await verify.MachineStockCounts.OrderByDescending(count => count.Id).FirstAsync();
        Assert.Equal(BusinessA, stored.BusinessId);
        Assert.Equal(MachineId, stored.MachineId);
        Assert.Equal(ProductA, stored.ProductId);
        Assert.Equal(11, stored.CountedQuantity);
        Assert.Equal("Counted after refill", stored.Notes);

        // The count moved no quantity and no cost: that is what makes it an observation.
        var product = await verify.Products.SingleAsync(p => p.Id == ProductA);
        Assert.Equal(4, product.QuantityInStock);
        Assert.Equal(10, product.CostingQuantity);
        Assert.Equal(20m, product.InventoryValue);
        Assert.All(
            await verify.NayaxSales.ToListAsync(),
            sale => Assert.Null(sale.CostOfGoodsSold));
    }

    [Fact]
    public async Task A_count_for_another_businesss_product_records_nothing()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var record = await new EfMachineStockLedgerStore(db)
                .RecordCountAsync(MachineId, ProductB, 5, OpeningAt.AddDays(3), null, CancellationToken.None);

            Assert.Null(record);
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Equal(ProductA, (await verify.MachineStockCounts.SingleAsync(c => c.BusinessId == BusinessA)).ProductId);
        Assert.Equal(ProductB, (await verify.MachineStockCounts.SingleAsync(c => c.BusinessId == BusinessB)).ProductId);
    }

    [Fact]
    public async Task Successive_counts_are_all_kept_so_the_history_stays_auditable()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var store = new EfMachineStockLedgerStore(db);
            await store.RecordCountAsync(MachineId, ProductA, 5, OpeningAt.AddDays(3), null, CancellationToken.None);
            await store.RecordCountAsync(MachineId, ProductA, 2, OpeningAt.AddDays(4), null, CancellationToken.None);
        }

        await using var db2 = TestAppDbContext.For(options, BusinessA);
        var facts = await new EfMachineStockLedgerStore(db2)
            .GetLedgerFactsAsync(MachineId, [], CancellationToken.None);

        Assert.Equal([6, 5, 2], facts.Counts.OrderBy(count => count.CountedAtUtc).Select(count => count.CountedQuantity));
    }

    private static async Task<DbContextOptions<AppDbContext>> SeedTwoBusinessesAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var setup = TestAppDbContext.Unrestricted(options))
            await setup.Database.EnsureCreatedAsync();

        await using (var a = TestAppDbContext.For(options, BusinessA))
        {
            a.Products.Add(new Product
            {
                Id = ProductA,
                Name = "Coke Zero",
                QuantityInStock = 4,
                CostingQuantity = 10,
                InventoryValue = 20m,
                AverageUnitCost = 2m,
            });
            a.StockAdjustments.Add(new StockAdjustment
            {
                ProductId = ProductA,
                QuantityChange = -6,
                QuantityAfter = 4,
                Reason = StockAdjustmentReason.MachineRefill,
                MachineId = MachineId,
                EffectiveAt = OpeningAt.AddHours(1),
            });
            a.NayaxSales.Add(new NayaxSales
            {
                TransactionID = 5001,
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                MachineID = MachineId,
                NayaxProductId = ProductA,
                ProductName = "Coke Zero",
                MachineAuthorizationTime = OpeningAt.AddDays(1),
            });

            // A completed sale of a product this business does not have: reported as unattributed,
            // never spread across the products that are in the machine.
            a.NayaxSales.Add(new NayaxSales
            {
                TransactionID = 5003,
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                MachineID = MachineId,
                NayaxProductId = 777,
                ProductName = "Not in this catalogue",
                MachineAuthorizationTime = OpeningAt.AddDays(1),
            });
            a.MachineStockCounts.Add(new MachineStockCount
            {
                MachineId = MachineId,
                ProductId = ProductA,
                CountedQuantity = 6,
                CountedAtUtc = OpeningAt.AddDays(2),
            });
            a.NayaxMachineStockEvents.Add(new NayaxMachineStockEvent
            {
                NayaxEventLogId = 70001,
                MachineId = MachineId,
                EventCode = 501,
                EventDateTimeGmt = OpeningAt.AddDays(2),
                EventDateTimeVmc = OpeningAt.AddDays(2),
                RawEventData = "MDB 1 Coke Zero 4",
                ProcessingStatus = NayaxStockEventProcessingStatus.Unprocessed,
            });

            var baseline = new InventoryCostTransitionBaseline
            {
                ProductId = ProductA,
                CutoffAt = OpeningAt,
                HomeStockQuantity = 4,
                MachineStockQuantity = 4,
                OpeningCostingQuantity = 8,
                AverageUnitCost = 2m,
                InventoryValue = 16m,
                CostSource = InventoryCostBaselineSource.ManualAuthoritative,
            };
            baseline.MachineStocks.Add(new InventoryCostTransitionMachineStock
            {
                MachineId = MachineId,
                MachineName = "Machine 42",
                StockQuantity = 4,
            });
            a.InventoryCostTransitionBaselines.Add(baseline);
            await a.SaveChangesAsync();
        }

        await using (var b = TestAppDbContext.For(options, BusinessB))
        {
            b.Products.Add(new Product { Id = ProductB, Name = "Business B Product", QuantityInStock = 5 });
            b.StockAdjustments.Add(new StockAdjustment
            {
                ProductId = ProductB,
                QuantityChange = -2,
                QuantityAfter = 5,
                Reason = StockAdjustmentReason.MachineRefill,
                MachineId = MachineId,
                EffectiveAt = OpeningAt.AddHours(1),
            });
            b.NayaxSales.Add(new NayaxSales
            {
                TransactionID = 5001,
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                MachineID = MachineId,
                NayaxProductId = ProductA,
                ProductName = "Coke Zero",
                MachineAuthorizationTime = OpeningAt.AddDays(1),
            });
            b.MachineStockCounts.Add(new MachineStockCount
            {
                MachineId = MachineId,
                ProductId = ProductB,
                CountedQuantity = 3,
                CountedAtUtc = OpeningAt.AddDays(2),
            });
            await b.SaveChangesAsync();
        }

        return options;
    }
}
