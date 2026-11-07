using Inventory.Application.Costing;
using Inventory.Application.Machines;
using Inventory.Application.MachineStockSync;
using Inventory.Domain.Nayax;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.MachineStockSync;

/// <summary>
/// One transaction and one cost rebuild per product, not per event (issue #1127).
///
/// <para>Applying ten ticked events for one product used to replay that product's whole movement and
/// sale history ten times, in ten separate transactions, and to leave every product's replayed
/// history tracked on the request's context so each later save got slower. These tests pin the
/// behaviour that replaces it: exactly one rebuild per product that had an event applied, every
/// event still its own auditable movement, each event's stock check made against the storage its
/// predecessors left, a failure scoped to its own product, and the same per-event outcomes,
/// messages and result order the one-at-a-time apply produced.</para>
///
/// <para>The events are seeded as stored imported facts rather than fetched through
/// <c>SyncMachineStockFromNayax</c>: how an alert is parsed and matched is
/// <see cref="MachineStockSyncTests"/>'s subject, and nothing here changes it. Relational SQLite is
/// used wherever what is actually <em>committed</em> is the point, because the EF InMemory provider
/// has no transaction to roll back.</para>
/// </summary>
public class MachineStockSyncApplyBatchTests
{
    private const int SellerBusinessId = 1;

    /// <summary>The machine's Nayax number: what the provider's events carry.</summary>
    private const long MachineId = 900;

    /// <summary>The same machine's local id (issue #741): what the movements record.</summary>
    private const long LocalMachineId = 31;

    private static readonly MachineIdentity Machine = new(LocalMachineId, MachineId);
    private static readonly DateTime EventTime = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    private const long Coke = 200;
    private const long Water = 201;
    private const long Jerky = 202;

    #region One rebuild per product

    [Fact]
    public async Task Each_product_with_an_applied_event_is_rebuilt_exactly_once_however_many_events_it_has()
    {
        using var db = CreateInMemoryDb(nameof(Each_product_with_an_applied_event_is_rebuilt_exactly_once_however_many_events_it_has));
        SeedCostedProduct(db, Coke, "Coke 375mL", 30);
        SeedCostedProduct(db, Water, "Nu Pure Spring Water 600mL", 30);
        SeedCostedProduct(db, Jerky, "25g Nobby's Beef Jerky Hot", 30);
        SeedEvent(db, 1, Coke, 3);
        SeedEvent(db, 2, Coke, 4);
        SeedEvent(db, 3, Water, 5);
        SeedEvent(db, 4, Coke, 6);
        SeedEvent(db, 5, Water, 2);
        // Jerky is requested but every one of its events is refused, so it must not be rebuilt.
        SeedEvent(db, 6, Jerky, -3);
        await db.SaveChangesAsync();

        var rebuild = ObservedRebuild.Over(db);
        var result = await Apply(db, rebuild).Handle(Machine, await EventIdsAsync(db), CancellationToken.None);

        var refusedEventId = await EventIdAsync(db, 6);
        Assert.All(
            result.Results.Where(r => r.EventId != refusedEventId),
            r => Assert.Equal(NayaxStockEventApplyOutcome.Applied, r.Outcome));
        Assert.Equal(1, rebuild.Replays(Coke));
        Assert.Equal(1, rebuild.Replays(Water));
        Assert.Equal(0, rebuild.Replays(Jerky));
    }

    [Fact]
    public async Task Every_event_of_one_product_still_records_its_own_auditable_movement()
    {
        using var db = CreateInMemoryDb(nameof(Every_event_of_one_product_still_records_its_own_auditable_movement));
        SeedCostedProduct(db, Coke, "Coke 375mL", 30);
        SeedEvent(db, 5001, Coke, 3);
        SeedEvent(db, 5002, Coke, 4);
        await db.SaveChangesAsync();

        var result = await Apply(db).Handle(Machine, await EventIdsAsync(db), CancellationToken.None);

        Assert.All(result.Results, r => Assert.Equal(NayaxStockEventApplyOutcome.Applied, r.Outcome));

        var movements = await db.StockAdjustments
            .Where(x => x.Reason == StockAdjustmentReason.MachineRefill)
            .OrderBy(x => x.Id)
            .ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.All(movements, movement =>
        {
            Assert.Equal(StockAdjustmentSource.Nayax, movement.Source);
            Assert.Equal(LocalMachineId, movement.MachineId);
        });
        Assert.Equal([-3, -4], movements.Select(x => x.QuantityChange));
        Assert.Equal(
            ["Nayax Sync Restock (EventLogID 5001)", "Nayax Sync Restock (EventLogID 5002)"],
            movements.Select(x => x.Notes));

        // Each event names its own movement, so each one stays individually auditable, and the
        // response reports the same ids.
        var events = await db.NayaxMachineStockEvents.OrderBy(e => e.NayaxEventLogId).ToListAsync();
        Assert.All(events, e => Assert.Equal(NayaxStockEventProcessingStatus.Applied, e.ProcessingStatus));
        Assert.Equal(movements.Select(x => (int?)x.Id), events.Select(e => e.StockAdjustmentId));
        Assert.Equal(
            events.Select(e => e.StockAdjustmentId),
            events.Select(e => result.Results.Single(r => r.EventId == e.Id).StockAdjustmentId));
    }

    [Fact]
    public async Task An_earlier_products_rebuilt_history_is_not_tracked_once_its_batch_has_committed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteDbAsync(connection);
        await using var db = TestAppDbContext.For(options, SellerBusinessId);

        SeedCostedProduct(db, Coke, "Coke 375mL", 30);
        SeedCostedProduct(db, Water, "Nu Pure Spring Water 600mL", 30);
        SeedCostedProduct(db, Jerky, "25g Nobby's Beef Jerky Hot", 30);
        SeedEvent(db, 1, Coke, 3);
        SeedEvent(db, 2, Water, 4);
        SeedEvent(db, 3, Jerky, 5);
        await db.SaveChangesAsync();

        var ids = await EventIdsAsync(db);
        var apply = Apply(db);

        await apply.Handle(Machine, [ids[0]], CancellationToken.None);
        var trackedAfterOneProduct = db.ChangeTracker.Entries().Count();

        await apply.Handle(Machine, [ids[1], ids[2]], CancellationToken.None);
        var trackedAfterThreeProducts = db.ChangeTracker.Entries().Count();

        // The history each product's rebuild loaded is gone once that product's transaction has
        // ended, so what the next product's SaveChanges has to examine does not grow with the
        // products already applied.
        Assert.Equal(trackedAfterOneProduct, trackedAfterThreeProducts);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    #endregion

    #region The running storage balance within one product's batch

    [Fact]
    public async Task A_later_event_that_does_not_fit_what_earlier_ones_left_is_refused_while_they_stay_applied()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteDbAsync(connection);
        await using var db = TestAppDbContext.For(options, SellerBusinessId);

        SeedCostedProduct(db, Coke, "Coke 375mL", 10);
        SeedEvent(db, 1, Coke, 4);
        SeedEvent(db, 2, Coke, 4);
        SeedEvent(db, 3, Coke, 5);
        await db.SaveChangesAsync();

        var ids = await EventIdsAsync(db);
        var result = await Apply(db).Handle(Machine, ids, CancellationToken.None);

        Assert.Equal(
            [
                NayaxStockEventApplyOutcome.Applied,
                NayaxStockEventApplyOutcome.Applied,
                NayaxStockEventApplyOutcome.InsufficientStock,
            ],
            result.Results.Select(r => r.Outcome));

        // Decided against the balance at the time of the check: 10 - 4 - 4 = 2 left, not the 10 the
        // transaction started from.
        Assert.Equal(
            "Nayax reported 5 loaded but only 2 is available in storage.",
            result.Results[2].Message);

        await using var verify = TestAppDbContext.For(options, SellerBusinessId);
        Assert.Equal(2, (await verify.Products.FindAsync(Coke))!.QuantityInStock);
        Assert.Equal(
            [NayaxStockEventProcessingStatus.Applied, NayaxStockEventProcessingStatus.Applied, NayaxStockEventProcessingStatus.Unprocessed],
            await verify.NayaxMachineStockEvents.OrderBy(e => e.Id).Select(e => e.ProcessingStatus).ToListAsync());
        Assert.Equal(
            [6, 2],
            await verify.StockAdjustments
                .Where(x => x.Reason == StockAdjustmentReason.MachineRefill)
                .OrderBy(x => x.Id)
                .Select(x => x.QuantityAfter)
                .ToListAsync());
    }

    [Fact]
    public async Task A_batch_apply_matches_applying_the_same_events_one_at_a_time()
    {
        // The same fixture and the same request order twice: once as one batched request, once as
        // one request per event - which is exactly what the apply used to do internally.
        var batched = await ApplyAndReadAsync(oneRequestPerEvent: false);
        var oneAtATime = await ApplyAndReadAsync(oneRequestPerEvent: true);

        Assert.Equal(oneAtATime.Outcomes, batched.Outcomes);
        Assert.Equal(oneAtATime.Product, batched.Product);
        Assert.Equal(oneAtATime.Movements, batched.Movements);
    }

    private static async Task<(
        IReadOnlyList<NayaxStockEventApplyOutcome> Outcomes,
        (int Physical, int? Costing, decimal? Value, decimal? Average) Product,
        IReadOnlyList<(int Change, int After, decimal? UnitCost, decimal? TotalCost, int? CostingAfter, decimal? ValueAfter, decimal? AverageAfter)> Movements)>
        ApplyAndReadAsync(bool oneRequestPerEvent)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteDbAsync(connection);

        List<int> ids;
        List<NayaxStockEventApplyOutcome> outcomes = [];
        await using (var db = TestAppDbContext.For(options, SellerBusinessId))
        {
            SeedCostedProduct(db, Coke, "Coke 375mL", 10, unitCost: 1.25m);
            // A completed sale between the opening restock and the refills, so the replayed costing
            // position is a real weighted-average one rather than the opening cost unchanged.
            db.NayaxSales.Add(new NayaxSales
            {
                TransactionID = 77,
                MachineID = MachineId,
                ProductId = Coke,
                SettlementValue = 3m,
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                MachineAuthorizationTime = EventTime.AddDays(-1),
            });
            SeedEvent(db, 1, Coke, 4);
            SeedEvent(db, 2, Coke, 4);
            // Refused against the balance the two above left, in both shapes of the request.
            SeedEvent(db, 3, Coke, 5);
            SeedEvent(db, 4, Coke, 2);
            await db.SaveChangesAsync();

            ids = await EventIdsAsync(db);
            var apply = Apply(db);

            if (oneRequestPerEvent)
            {
                foreach (var id in ids)
                {
                    var single = await apply.Handle(Machine, [id], CancellationToken.None);
                    outcomes.Add(single.Results[0].Outcome);
                }
            }
            else
            {
                var batch = await apply.Handle(Machine, ids, CancellationToken.None);
                outcomes.AddRange(batch.Results.Select(r => r.Outcome));
            }
        }

        await using var verify = TestAppDbContext.For(options, SellerBusinessId);
        var product = await verify.Products.AsNoTracking().SingleAsync(p => p.Id == Coke);
        var movements = await verify.StockAdjustments.AsNoTracking()
            .Where(x => x.ProductId == Coke)
            .OrderBy(x => x.Id)
            .Select(x => new ValueTuple<int, int, decimal?, decimal?, int?, decimal?, decimal?>(
                x.QuantityChange, x.QuantityAfter, x.UnitCost, x.TotalCost,
                x.CostingQuantityAfter, x.InventoryValueAfter, x.AverageUnitCostAfter))
            .ToListAsync();

        return (
            outcomes,
            (product.QuantityInStock, product.CostingQuantity, product.InventoryValue, product.AverageUnitCost),
            movements);
    }

    #endregion

    #region A failure is scoped to its own product

    [Fact]
    public async Task A_rebuild_failure_rolls_back_that_products_whole_batch_while_the_other_product_applies()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteDbAsync(connection);
        await using var db = TestAppDbContext.For(options, SellerBusinessId);

        SeedCostedProduct(db, Coke, "Coke 375mL", 10);
        SeedCostedProduct(db, Water, "Nu Pure Spring Water 600mL", 10);
        SeedEvent(db, 1, Coke, 3);
        SeedEvent(db, 2, Water, 2);
        SeedEvent(db, 3, Water, 4);
        await db.SaveChangesAsync();

        var ids = await EventIdsAsync(db);
        var result = await Apply(db, ObservedRebuild.Over(db, failingProductId: Water))
            .Handle(Machine, ids, CancellationToken.None);

        Assert.Equal(NayaxStockEventApplyOutcome.Applied, result.Results[0].Outcome);
        Assert.All(result.Results.Skip(1), refused =>
        {
            Assert.Equal(NayaxStockEventApplyOutcome.Error, refused.Outcome);
            Assert.Equal("Failed to apply this event; no inventory movement was recorded.", refused.Message);
            Assert.Null(refused.StockAdjustmentId);
        });

        await using var verify = TestAppDbContext.For(options, SellerBusinessId);
        Assert.Equal(7, (await verify.Products.FindAsync(Coke))!.QuantityInStock);
        Assert.Equal(10, (await verify.Products.FindAsync(Water))!.QuantityInStock);
        Assert.Equal(
            [NayaxStockEventProcessingStatus.Applied, NayaxStockEventProcessingStatus.Unprocessed, NayaxStockEventProcessingStatus.Unprocessed],
            await verify.NayaxMachineStockEvents.OrderBy(e => e.Id).Select(e => e.ProcessingStatus).ToListAsync());
        Assert.Equal(
            [Coke],
            await verify.StockAdjustments
                .Where(x => x.Reason == StockAdjustmentReason.MachineRefill)
                .Select(x => x.ProductId)
                .ToListAsync());
    }

    [Fact]
    public async Task A_rebuild_failure_leaves_a_later_insufficient_stock_event_unprocessed_and_retryable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteDbAsync(connection);
        await using var db = TestAppDbContext.For(options, SellerBusinessId);

        SeedCostedProduct(db, Coke, "Coke 375mL", 10);
        SeedEvent(db, 1, Coke, 4);
        SeedEvent(db, 2, Coke, 4);
        SeedEvent(db, 3, Coke, 5);
        await db.SaveChangesAsync();

        var ids = await EventIdsAsync(db);
        var failed = await Apply(db, ObservedRebuild.Over(db, failingProductId: Coke))
            .Handle(Machine, ids, CancellationToken.None);

        // The two staged refills are discarded with the rest of the transaction; the third keeps the
        // outcome it was given against the balance at the time of its check, even though the
        // rollback has since restored that stock.
        Assert.Equal(
            [
                NayaxStockEventApplyOutcome.Error,
                NayaxStockEventApplyOutcome.Error,
                NayaxStockEventApplyOutcome.InsufficientStock,
            ],
            failed.Results.Select(r => r.Outcome));

        await using (var verify = TestAppDbContext.For(options, SellerBusinessId))
        {
            Assert.Equal(10, (await verify.Products.FindAsync(Coke))!.QuantityInStock);
            Assert.Empty(await verify.StockAdjustments.Where(x => x.Reason == StockAdjustmentReason.MachineRefill).ToListAsync());
            Assert.All(
                await verify.NayaxMachineStockEvents.ToListAsync(),
                e => Assert.Equal(NayaxStockEventProcessingStatus.Unprocessed, e.ProcessingStatus));
        }

        // Nothing about it is settled, so a retry against the restored stock simply applies it.
        var retried = await Apply(db).Handle(Machine, [ids[2]], CancellationToken.None);
        Assert.Equal(NayaxStockEventApplyOutcome.Applied, retried.Results[0].Outcome);

        await using var reread = TestAppDbContext.For(options, SellerBusinessId);
        Assert.Equal(5, (await reread.Products.FindAsync(Coke))!.QuantityInStock);
    }

    #endregion

    #region Outcomes that never reach a write

    [Fact]
    public async Task Every_non_writing_outcome_keeps_its_message_and_causes_no_rebuild()
    {
        using var db = CreateInMemoryDb(nameof(Every_non_writing_outcome_keeps_its_message_and_causes_no_rebuild));
        SeedCostedProduct(db, Coke, "Coke 375mL", 10);
        SeedCostedProduct(db, Water, "Nu Pure Spring Water 600mL", 10);

        var alreadyApplied = SeedEvent(db, 1, Coke, 2);
        alreadyApplied.ProcessingStatus = NayaxStockEventProcessingStatus.Applied;
        alreadyApplied.StockAdjustmentId = 4242;

        var reconciled = SeedEvent(db, 2, Coke, 2);
        reconciled.DuplicateResolution = NayaxDuplicateResolution.ReconciledManually;

        // A manual refill of the same product, quantity and hour: the possible-duplicate flag the
        // ordinary apply refuses until the operator resolves it.
        var unresolvedDuplicate = SeedEvent(db, 3, Coke, 3);
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = Coke,
            QuantityChange = -3,
            QuantityAfter = 7,
            Reason = StockAdjustmentReason.MachineRefill,
            Source = StockAdjustmentSource.Manual,
            MachineId = LocalMachineId,
            EffectiveAt = EventTime,
        });

        var needsReview = SeedEvent(db, 4, null, 2);
        needsReview.MatchStatus = NayaxStockEventMatchStatus.NeedsReview;
        needsReview.NeedsReviewReason = "MDB 9 is not a known position on machine 900.";

        var negative = SeedEvent(db, 5, Water, -3);
        var missingProduct = SeedEvent(db, 6, 9999, 2);
        var insufficient = SeedEvent(db, 7, Water, 11);
        await db.SaveChangesAsync();

        var rebuild = ObservedRebuild.Over(db);
        var result = await Apply(db, rebuild).Handle(
            Machine,
            [alreadyApplied.Id, reconciled.Id, unresolvedDuplicate.Id, needsReview.Id, negative.Id, missingProduct.Id, insufficient.Id, 987654],
            CancellationToken.None);

        var outcomes = result.Results.ToDictionary(r => r.EventId);
        Assert.Equal(
            (NayaxStockEventApplyOutcome.Applied, "Already applied.", 4242),
            Reading(outcomes[alreadyApplied.Id]));
        Assert.Equal(
            (NayaxStockEventApplyOutcome.Reconciled, "Already reconciled as recorded manually; no Nayax movement was applied.", null),
            Reading(outcomes[reconciled.Id]));
        Assert.Equal(
            (NayaxStockEventApplyOutcome.DuplicateRequiresResolution,
                "Flagged as a possible duplicate of a manual refill. Choose 'Already recorded manually' or 'Apply as separate restock' before applying.",
                null),
            Reading(outcomes[unresolvedDuplicate.Id]));
        Assert.Equal(
            (NayaxStockEventApplyOutcome.NotMatched, "MDB 9 is not a known position on machine 900.", null),
            Reading(outcomes[needsReview.Id]));
        Assert.Equal(
            (NayaxStockEventApplyOutcome.NotApplicable,
                "Negative adjustments are machine discrepancies, not refills, and are never applied automatically.",
                null),
            Reading(outcomes[negative.Id]));
        Assert.Equal(
            (NayaxStockEventApplyOutcome.Error, "Matched product no longer exists.", null),
            Reading(outcomes[missingProduct.Id]));
        Assert.Equal(
            (NayaxStockEventApplyOutcome.InsufficientStock,
                "Nayax reported 11 loaded but only 10 is available in storage.",
                null),
            Reading(outcomes[insufficient.Id]));
        Assert.Equal(
            (NayaxStockEventApplyOutcome.Error, "Event not found for this machine.", null),
            Reading(outcomes[987654]));

        Assert.Empty(rebuild.Products);
        Assert.Equal(1, await db.StockAdjustments.CountAsync(x => x.Reason == StockAdjustmentReason.MachineRefill));
        Assert.Equal(10, (await db.Products.FindAsync(Coke))!.QuantityInStock);
        Assert.Equal(10, (await db.Products.FindAsync(Water))!.QuantityInStock);
    }

    [Fact]
    public async Task The_response_lists_one_result_per_distinct_requested_event_id_in_request_order()
    {
        using var db = CreateInMemoryDb(nameof(The_response_lists_one_result_per_distinct_requested_event_id_in_request_order));
        SeedCostedProduct(db, Coke, "Coke 375mL", 30);
        SeedCostedProduct(db, Water, "Nu Pure Spring Water 600mL", 30);
        var first = SeedEvent(db, 1, Coke, 3);
        var second = SeedEvent(db, 2, Water, 4);
        var third = SeedEvent(db, 3, Coke, 5);
        await db.SaveChangesAsync();

        // Interleaved products, a repeated id and an id that does not exist: the response still
        // answers each distinct id once, in the order it was asked for.
        var result = await Apply(db).Handle(
            Machine, [third.Id, second.Id, 55555, first.Id, second.Id], CancellationToken.None);

        Assert.Equal([third.Id, second.Id, 55555, first.Id], result.Results.Select(r => r.EventId));
        Assert.Equal(
            [
                NayaxStockEventApplyOutcome.Applied,
                NayaxStockEventApplyOutcome.Applied,
                NayaxStockEventApplyOutcome.Error,
                NayaxStockEventApplyOutcome.Applied,
            ],
            result.Results.Select(r => r.Outcome));

        // The repeated id applied once: 30 - 5 - 3 for Coke, 30 - 4 for Water.
        Assert.Equal(22, (await db.Products.FindAsync(Coke))!.QuantityInStock);
        Assert.Equal(26, (await db.Products.FindAsync(Water))!.QuantityInStock);
    }

    #endregion

    #region Fixture

    private static (NayaxStockEventApplyOutcome Outcome, string Message, int? StockAdjustmentId) Reading(
        NayaxStockEventApplyResultDto result) =>
        (result.Outcome, result.Message, result.StockAdjustmentId);

    private static ApplyMachineStockSync Apply(AppDbContext db, IRebuildProductCost? rebuild = null) =>
        new(new EfMachineStockEventStore(
            db, TestCostingUseCases.RecordMovement(db), rebuild ?? TestCostingUseCases.Rebuild(db)));

    private static async Task<List<int>> EventIdsAsync(AppDbContext db) =>
        await db.NayaxMachineStockEvents.AsNoTracking()
            .OrderBy(e => e.Id)
            .Select(e => e.Id)
            .ToListAsync();

    private static async Task<int> EventIdAsync(AppDbContext db, long nayaxEventLogId) =>
        await db.NayaxMachineStockEvents.AsNoTracking()
            .Where(e => e.NayaxEventLogId == nayaxEventLogId)
            .Select(e => e.Id)
            .SingleAsync();

    /// <summary>One stored, matched, unprocessed Nayax Event 501 for this machine.</summary>
    private static NayaxMachineStockEvent SeedEvent(
        AppDbContext db, long nayaxEventLogId, long? productId, int quantity)
    {
        var stored = new NayaxMachineStockEvent
        {
            NayaxEventLogId = nayaxEventLogId,
            MachineId = MachineId,
            EventCode = NayaxMachineAlertEventCodes.StockAdjustForMachine,
            EventDateTimeGmt = EventTime,
            EventDateTimeVmc = EventTime.AddHours(10),
            RawEventData = $"Product MDB: 7 | Product {productId} | {quantity}",
            ParsedMdb = 7,
            ParsedProductName = $"Product {productId}",
            ParsedQuantity = quantity,
            MatchedProductId = productId,
            MatchStatus = NayaxStockEventMatchStatus.Matched,
        };
        db.NayaxMachineStockEvents.Add(stored);
        return stored;
    }

    /// <summary>
    /// A product whose stored stock agrees with its history: one costed opening restock of exactly
    /// the quantity it holds, which is what makes a replay's answer comparable with the stored one.
    /// </summary>
    private static void SeedCostedProduct(
        AppDbContext db, long id, string name, int quantityInStock, decimal unitCost = 1m)
    {
        db.Products.Add(new Product
        {
            Id = id,
            ExternalProductId = NayaxExternalId.From(id),
            Name = name,
            QuantityInStock = quantityInStock,
            CostingQuantity = quantityInStock,
            InventoryValue = quantityInStock * unitCost,
            AverageUnitCost = unitCost,
        });

        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = id,
            QuantityChange = quantityInStock,
            QuantityAfter = quantityInStock,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = unitCost,
            EffectiveAt = EventTime.AddDays(-2),
        });
    }

    private static AppDbContext CreateInMemoryDb(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options;
        SeedSellerBusiness(options);
        return TestAppDbContext.For(options, SellerBusinessId);
    }

    private static async Task<DbContextOptions<AppDbContext>> CreateSqliteDbAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var schema = TestAppDbContext.Unrestricted(options))
        {
            await schema.Database.EnsureCreatedAsync();
        }

        SeedSellerBusiness(options);
        return options;
    }

    private static void SeedSellerBusiness(DbContextOptions<AppDbContext> options)
    {
        using var setup = TestAppDbContext.Unrestricted(options);
        setup.Businesses.Add(new Business
        {
            Id = SellerBusinessId,
            Name = "Vending A",
            Status = BusinessStatus.Active,
            SalesProvider = ExternalProvider.Nayax,
            CreatedAtUtc = EventTime,
        });
        setup.Machines.Add(new LocalMachine
        {
            BusinessId = SellerBusinessId,
            Id = LocalMachineId,
            Name = "Machine 900",
            ExternalMachineId = NayaxExternalId.From(MachineId),
        });
        setup.SaveChanges();
    }

    /// <summary>
    /// The production rebuild with two things added: it counts how many times each product's history
    /// was replayed, and it can be told to fail one product's replay the way a fatal data-quality
    /// issue does. The costs it writes are therefore the real ones.
    /// </summary>
    private sealed class ObservedRebuild : IRebuildProductCost
    {
        private readonly IRebuildProductCost _inner;
        private readonly long? _failingProductId;
        private readonly Dictionary<long, int> _replays = [];

        private ObservedRebuild(IRebuildProductCost inner, long? failingProductId)
        {
            _inner = inner;
            _failingProductId = failingProductId;
        }

        public static ObservedRebuild Over(AppDbContext db, long? failingProductId = null) =>
            new(TestCostingUseCases.Rebuild(db), failingProductId);

        /// <summary>Every product whose history this request replayed at all.</summary>
        public IReadOnlyCollection<long> Products => _replays.Keys;

        public int Replays(long productId) => _replays.GetValueOrDefault(productId);

        public Task<InventoryCostRebuildResult> RebuildAsync(
            long productId,
            DateTime? recostCompletedSalesFrom = null,
            bool dryRun = false,
            CancellationToken cancellationToken = default)
        {
            _replays[productId] = _replays.GetValueOrDefault(productId) + 1;

            return productId == _failingProductId
                ? throw new InventoryCostDataQualityException($"Simulated fatal history for product {productId}.")
                : _inner.RebuildAsync(productId, recostCompletedSalesFrom, dryRun, cancellationToken);
        }

        public Task<InventoryCostRebuildResult> RebuildCostingOnlyAsync(
            long productId, DateTime recostCompletedSalesFrom, CancellationToken cancellationToken = default) =>
            _inner.RebuildCostingOnlyAsync(productId, recostCompletedSalesFrom, cancellationToken);

        public Task<decimal?> GetAverageUnitCostAtAsync(
            long productId, DateTime saleTime, long? saleTransactionId = null, CancellationToken cancellationToken = default) =>
            _inner.GetAverageUnitCostAtAsync(productId, saleTime, saleTransactionId, cancellationToken);
    }

    #endregion
}
