using Inventory.Application.Machines;
using Inventory.Application.MachineStockSync;
using Inventory.Application.Stock;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.MachineStock;
using Inventory.Domain.Nayax;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DomainStock = Inventory.Domain.Stock;

namespace InventoryApi.Tests.Application.MachineStockSync;

/// <summary>
/// Where a Nayax-sourced refill lands in the machine-stock ledger, and where it does not land
/// anywhere else (issue #1243, finding INV-06 of audit #1222).
///
/// <para>Applying an event late used to invent machine stock. The ledger replays by effective
/// instant and a count sets the level from its own instant onward, so a 10 November event for 20
/// units applied on 13 November was replayed <em>after</em> a 12 November count of 15 - expected
/// machine stock became 35 instead of 15, and the next comparison reported 20 units of shrinkage
/// that never happened. The machine ledger now places such a refill at the event's own
/// <c>EventDateTimeGMT</c>, while the storage movement the cost replay and Stock History read keeps
/// the apply time, so storage cannot go negative at an earlier instant.</para>
///
/// <para>Relational SQLite and the real adapters throughout, because the subject is the relationship
/// between three persisted things at once - the stored movement, the stored count and the stored
/// event - read back through the same two stores production reads them through.</para>
/// </summary>
public class SyncRestockRefillLedgerDateTests
{
    private const int BusinessId = 1;

    /// <summary>The machine's Nayax number: what its stock events carry.</summary>
    private const long MachineId = 900;

    /// <summary>The same machine's local id (issue #741): what its refills and counts carry.</summary>
    private const long LocalMachineId = 31;

    private const long Coke = 200;
    private const decimal UnitCost = 1.25m;

    private static readonly MachineIdentity Machine = new(LocalMachineId, MachineId);

    /// <summary>The issue's own example, to the day.</summary>
    private static readonly DateTime EventAt = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CountedAt = new(2026, 11, 12, 5, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PurchasedAt = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The issue's example: an event before a count, applied after it. The count is the authority on
    /// what the machine was holding at its own instant, so the refill it already absorbed must not be
    /// added on top of it a second time.
    /// </summary>
    [Fact]
    public async Task An_event_before_a_count_applied_after_it_leaves_expected_machine_stock_at_the_count()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedAsync(connection, storageQuantity: 50);
        await SeedCountAsync(options, countedQuantity: 15, CountedAt);
        await SeedEventAsync(options, nayaxEventLogId: 7001, quantity: 20, EventAt);

        await ApplyEveryEventAsync(options);

        var ledger = await ProjectLedgerAsync(options);
        Assert.Equal(15, ledger.ExpectedQuantity);
        Assert.Equal(MachineStockBaselineSource.CountedReconciliation, ledger.BaselineSource);
        Assert.Equal(CountedAt, ledger.BaselineAtUtc);

        // The refill is before the baseline, so it is inside the counted level rather than a movement
        // since it: the count reset the totals, exactly as it does for a manual refill before it.
        Assert.Equal(0, ledger.RefilledQuantity);
        Assert.Empty(ledger.Issues);
    }

    /// <summary>
    /// With no count between the event and the apply, there is nothing to absorb the refill, so the
    /// machine's expected stock is what it has always been: the count plus the units the event
    /// delivered.
    /// </summary>
    [Fact]
    public async Task An_event_applied_with_no_count_in_between_adds_its_units_exactly_as_before()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedAsync(connection, storageQuantity: 50);
        await SeedCountAsync(options, countedQuantity: 15, EventAt.AddDays(-1));
        await SeedEventAsync(options, nayaxEventLogId: 7002, quantity: 20, EventAt);

        await ApplyEveryEventAsync(options);

        var ledger = await ProjectLedgerAsync(options);
        Assert.Equal(35, ledger.ExpectedQuantity);
        Assert.Equal(20, ledger.RefilledQuantity);
        Assert.Equal(MachineStockBaselineSource.CountedReconciliation, ledger.BaselineSource);
        Assert.Empty(ledger.Issues);
    }

    /// <summary>
    /// The same-instant tie-break, pinned rather than changed: <c>MachineStockLedger.ReplayOrder</c>
    /// already sorts by instant and then refill → return → sale → count, so a count is replayed last
    /// at its own instant and absorbs a refill that happened there. This test exists so that ordering
    /// cannot be loosened for a Nayax refill without a failure.
    /// </summary>
    [Fact]
    public async Task A_nayax_refill_whose_event_time_equals_a_counts_time_is_absorbed_by_the_count()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedAsync(connection, storageQuantity: 50);
        await SeedCountAsync(options, countedQuantity: 15, CountedAt);
        await SeedEventAsync(options, nayaxEventLogId: 7003, quantity: 20, CountedAt);

        await ApplyEveryEventAsync(options);

        var ledger = await ProjectLedgerAsync(options);
        Assert.Equal(15, ledger.ExpectedQuantity);
        Assert.Equal(0, ledger.RefilledQuantity);
        Assert.Equal(CountedAt, ledger.BaselineAtUtc);
    }

    /// <summary>
    /// The storage side is untouched by all of this, and that is the point of keeping two instants:
    /// the movement the cost replay and Stock History read is still dated at the apply time.
    ///
    /// <para>The product deliberately holds nothing until a purchase <em>after</em> the event
    /// instant. A refill dated at the event time would therefore replay against zero storage and make
    /// physical stock negative on 10 November, which <c>NegativePhysicalStock</c> refuses as a fatal
    /// history - so this test fails loudly if the storage movement is ever moved back to the event
    /// time. The storage, costing and sale-cost figures asserted here are the ones the apply produced
    /// before this change, for the same events.</para>
    /// </summary>
    [Fact]
    public async Task The_storage_movement_and_the_cost_replay_keep_the_apply_time()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedAsync(connection, storageQuantity: 0);
        await SeedPurchaseAsync(options, quantity: 50, EventAt.AddDays(2));
        await SeedSaleAsync(options, transactionId: 5001, soldAt: EventAt.AddDays(3));
        await SeedEventAsync(options, nayaxEventLogId: 7004, quantity: 20, EventAt);

        var appliedFrom = DateTime.UtcNow;
        var results = await ApplyEveryEventAsync(options);
        var appliedTo = DateTime.UtcNow;
        Assert.Equal(NayaxStockEventApplyOutcome.Applied, Assert.Single(results.Results).Outcome);

        await using var verify = TestAppDbContext.Unrestricted(options);
        var movement = await verify.StockAdjustments
            .AsNoTracking()
            .SingleAsync(adjustment => adjustment.Reason == StockAdjustmentReason.MachineRefill);

        Assert.Equal(StockAdjustmentSource.Nayax, movement.Source);
        Assert.InRange(movement.EffectiveAt, appliedFrom, appliedTo);
        Assert.True(
            movement.EffectiveAt > EventAt,
            $"The storage movement is dated {movement.EffectiveAt:O}, at or before the event instant "
                + $"{EventAt:O}. The cost replay and Stock History read this column and must keep the "
                + "apply time; only the machine ledger uses the event's own time.");

        // Costing is exactly what it was: the refill is an internal transfer, so only the sale
        // consumed a unit, and it is costed from the purchase that preceded it.
        var product = await verify.Products.AsNoTracking().SingleAsync(candidate => candidate.Id == Coke);
        Assert.Equal(30, product.QuantityInStock);
        Assert.Equal(49, product.CostingQuantity);
        Assert.Equal(49 * UnitCost, product.InventoryValue);
        Assert.Equal(UnitCost, product.AverageUnitCost);

        var sale = await verify.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Equal(UnitCost, sale.UnitCostAtSale);
        Assert.Equal(UnitCost, sale.CostOfGoodsSold);
        Assert.Equal(SaleCostingStatus.Costed, sale.CostingStatus);
        Assert.Equal(SaleCostSource.InventoryLedger, sale.CostSource);
    }

    /// <summary>
    /// A manual refill carries no event, so nothing changes for it: the machine ledger reads its
    /// recorded <c>EffectiveAt</c>, as it always has.
    /// </summary>
    [Fact]
    public async Task A_manual_machine_refill_is_placed_at_its_own_effective_time_as_before()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedAsync(connection, storageQuantity: 50);
        await SeedCountAsync(options, countedQuantity: 15, CountedAt);

        var recordedFrom = DateTime.UtcNow;
        await using (var db = TestAppDbContext.For(options, BusinessId))
        {
            await new EfStockAdjustmentStore(db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db))
                .ApplyAsync(
                    Coke,
                    new ManualStockAdjustmentInput(
                        -20, DomainStock.StockAdjustmentReason.MachineRefill, "Refilled by hand",
                        LocalMachineId, EatBefore: null, UnitCost: null),
                    CancellationToken.None);
        }

        // Recorded after the count, so the manual refill is a movement since the baseline and adds to
        // it - the behaviour a manual refill has always had.
        var ledger = await ProjectLedgerAsync(options);
        Assert.Equal(35, ledger.ExpectedQuantity);
        Assert.Equal(20, ledger.RefilledQuantity);

        await using var verify = TestAppDbContext.Unrestricted(options);
        var movement = await verify.StockAdjustments
            .AsNoTracking()
            .SingleAsync(adjustment => adjustment.Reason == StockAdjustmentReason.MachineRefill);
        Assert.Equal(StockAdjustmentSource.Manual, movement.Source);
        Assert.InRange(movement.EffectiveAt, recordedFrom, DateTime.UtcNow);
    }

    #region Fixture

    /// <summary>
    /// The two instants of one applied refill, as the two sides read them: what the machine ledger
    /// replays it at, and what the storage movement is dated.
    /// </summary>
    [Fact]
    public async Task The_machine_ledger_reads_the_event_time_while_the_movement_keeps_the_apply_time()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedAsync(connection, storageQuantity: 50);
        await SeedEventAsync(options, nayaxEventLogId: 7005, quantity: 20, EventAt);

        await ApplyEveryEventAsync(options);

        await using (var db = TestAppDbContext.For(options, BusinessId))
        {
            var facts = await new EfMachineStockLedgerStore(db)
                .GetLedgerFactsAsync(Machine, [], CancellationToken.None);
            var refill = Assert.Single(facts.Refills);

            Assert.Equal(EventAt, refill.EffectiveAtUtc);
            Assert.Equal(-20, refill.StorageQuantityChange);
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        var movement = await verify.StockAdjustments
            .AsNoTracking()
            .SingleAsync(adjustment => adjustment.Reason == StockAdjustmentReason.MachineRefill);
        Assert.NotEqual(EventAt, movement.EffectiveAt);
    }

    private static async Task<MachineStockProductLedger> ProjectLedgerAsync(DbContextOptions<AppDbContext> options)
    {
        await using var db = TestAppDbContext.For(options, BusinessId);
        var facts = await new EfMachineStockLedgerStore(db)
            .GetLedgerFactsAsync(Machine, [], CancellationToken.None);

        return Assert.Single(MachineStockLedger.Project(
            new MachineStockLedgerInputs(facts.Refills, facts.Sales, facts.Counts, facts.OpeningBaselines),
            facts.ProductNames));
    }

    private static async Task<NayaxMachineStockApplyResponseDto> ApplyEveryEventAsync(DbContextOptions<AppDbContext> options)
    {
        await using var db = TestAppDbContext.For(options, BusinessId);
        var eventIds = await db.NayaxMachineStockEvents.AsNoTracking()
            .OrderBy(stockEvent => stockEvent.Id)
            .Select(stockEvent => stockEvent.Id)
            .ToListAsync();

        var useCase = TestMachineStockSyncUseCases.Apply(
            new EfMachineStockEventStore(db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db)),
            new EfNayaxProductLinkStore(db),
            new RecordingMachineStockSyncLog());

        return await useCase.Handle(Machine, eventIds, CancellationToken.None);
    }

    private static async Task SeedCountAsync(
        DbContextOptions<AppDbContext> options, int countedQuantity, DateTime countedAtUtc)
    {
        await using var db = TestAppDbContext.For(options, BusinessId);
        db.MachineStockCounts.Add(new MachineStockCount
        {
            MachineId = LocalMachineId,
            ProductId = Coke,
            CountedQuantity = countedQuantity,
            CountedAtUtc = countedAtUtc,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>One stored, matched, unprocessed Nayax Event 501 for this machine.</summary>
    private static async Task SeedEventAsync(
        DbContextOptions<AppDbContext> options, long nayaxEventLogId, int quantity, DateTime eventAtUtc)
    {
        await using var db = TestAppDbContext.For(options, BusinessId);
        db.NayaxMachineStockEvents.Add(new NayaxMachineStockEvent
        {
            NayaxEventLogId = nayaxEventLogId,
            MachineId = MachineId,
            EventCode = NayaxMachineAlertEventCodes.StockAdjustForMachine,
            EventDateTimeGmt = eventAtUtc,
            EventDateTimeVmc = eventAtUtc.AddHours(11),
            RawEventData = $"Adjusted Stock, Product MDB: 7 | Coke 375mL | {quantity}",
            ParsedMdb = 7,
            ParsedProductName = "Coke 375mL",
            ParsedQuantity = quantity,
            MatchedProductId = Coke,
            MatchStatus = NayaxStockEventMatchStatus.Matched,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A costed purchase, recorded with its own effective instant.</summary>
    private static async Task SeedPurchaseAsync(
        DbContextOptions<AppDbContext> options, int quantity, DateTime effectiveAtUtc)
    {
        await using var db = TestAppDbContext.For(options, BusinessId);
        var product = await db.Products.SingleAsync(candidate => candidate.Id == Coke);
        product.QuantityInStock += quantity;
        product.CostingQuantity = (product.CostingQuantity ?? 0) + quantity;
        product.InventoryValue = (product.InventoryValue ?? 0m) + (quantity * UnitCost);
        product.AverageUnitCost = UnitCost;

        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = Coke,
            QuantityChange = quantity,
            QuantityAfter = product.QuantityInStock,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = UnitCost,
            TotalCost = quantity * UnitCost,
            EffectiveAt = effectiveAtUtc,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>One completed, costed sale on this machine.</summary>
    private static async Task SeedSaleAsync(
        DbContextOptions<AppDbContext> options, long transactionId, DateTime soldAt)
    {
        await using var db = TestAppDbContext.For(options, BusinessId);
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = transactionId,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = MachineId,
            ProductId = Coke,
            NayaxProductId = Coke,
            ProductName = "Coke 375mL",
            SettlementValue = 3m,
            MachineAuthorizationTime = soldAt,
        });
        await db.SaveChangesAsync();

        Assert.Equal(1, await TestCostingUseCases.CostPendingSales(db).Handle());
    }

    private static async Task<DbContextOptions<AppDbContext>> SeedAsync(
        SqliteConnection connection, int storageQuantity)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var schema = TestAppDbContext.Unrestricted(options))
            await schema.Database.EnsureCreatedAsync();

        await using (var setup = TestAppDbContext.Unrestricted(options))
        {
            setup.Businesses.Add(new Business
            {
                Id = BusinessId,
                Name = "Vending A",
                Status = BusinessStatus.Active,
                SalesProvider = ExternalProvider.Nayax,
                CreatedAtUtc = PurchasedAt,
            });
            await setup.SaveChangesAsync();
        }

        await using (var seed = TestAppDbContext.For(options, BusinessId))
        {
            seed.Machines.Add(new LocalMachine
            {
                Id = LocalMachineId,
                Name = "Machine 900",
                ExternalMachineId = NayaxExternalId.From(MachineId),
            });
            seed.Products.Add(new Product
            {
                Id = Coke,
                ExternalProductId = NayaxExternalId.From(Coke),
                Name = "Coke 375mL",
                QuantityInStock = storageQuantity,
                CostingQuantity = storageQuantity,
                InventoryValue = storageQuantity * UnitCost,
                AverageUnitCost = storageQuantity == 0 ? 0m : UnitCost,
            });

            if (storageQuantity > 0)
            {
                // One costed opening restock of exactly what the product holds, so the stored stock
                // agrees with its movement history and a replay's answer is comparable with it.
                seed.StockAdjustments.Add(new StockAdjustment
                {
                    ProductId = Coke,
                    QuantityChange = storageQuantity,
                    QuantityAfter = storageQuantity,
                    Reason = StockAdjustmentReason.Restock,
                    UnitCost = UnitCost,
                    TotalCost = storageQuantity * UnitCost,
                    EffectiveAt = PurchasedAt,
                });
            }

            await seed.SaveChangesAsync();
        }

        return options;
    }

    #endregion
}
