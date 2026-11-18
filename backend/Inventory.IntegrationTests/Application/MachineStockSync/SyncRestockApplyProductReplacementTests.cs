using Inventory.Application.Costing;
using Inventory.Application.Machines;
using Inventory.Application.MachineStockSync;
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

namespace InventoryApi.Tests.Application.MachineStockSync;

/// <summary>
/// Applying a confirmed Nayax product replacement (issue #1326), over relational SQLite and the real
/// <see cref="EfMachineStockEventStore"/>, <c>RecordInventoryMovement</c> and
/// <c>RebuildProductCost</c>: what each outgoing choice writes to storage, to the machine-stock
/// ledger and to costing; what every refusal leaves behind; and that one event can neither
/// half-apply nor apply twice.
///
/// <para>Both products are seeded as costed products - one opening restock at $1.00 a unit - so
/// costing quantity, inventory value and the cost of a write-off are the production cost replay's
/// own answers rather than values this fixture asserts into existence.</para>
/// </summary>
public class SyncRestockApplyProductReplacementTests
{
    private const int BusinessId = 1;
    private const int OtherBusinessId = 2;
    private const long NayaxMachineId = 900;
    private const long LocalMachineId = 31;
    private const int Mdb = 58;

    private static readonly MachineIdentity Machine = new(LocalMachineId, NayaxMachineId);

    /// <summary>The replacement's own instant: what the machine-stock ledger replays it at.</summary>
    private static readonly DateTime ReplacedAt = new(2026, 11, 15, 4, 33, 46, DateTimeKind.Utc);

    private const long IncomingId = 300;
    private const string IncomingName = "Red Bull Red Edition 250mL";
    private const long OutgoingId = 301;
    private const string OutgoingName = "V Energy Drink Sugarfree Original 250ML";

    /// <summary>A product of this business the operator has retired.</summary>
    private const long RetiredId = 302;

    /// <summary>Another business's product, which this business may not name.</summary>
    private const long ForeignId = 303;

    private const int IncomingStock = 40;

    /// <summary>
    /// The outgoing product's storage quantity. It is deliberately <em>less</em> than its costing
    /// quantity below, because the machine is holding the difference: a refill moves units out of
    /// the storeroom without consuming any costing inventory, which is exactly the state a
    /// replacement's outgoing units come out of.
    /// </summary>
    private const int OutgoingStock = 12;

    /// <summary>How many of the outgoing product's units are in the machine before the replacement.</summary>
    private const int OutgoingInMachine = 20;

    private const int OutgoingCosting = OutgoingStock + OutgoingInMachine;

    /// <summary>When those units were refilled into the machine.</summary>
    private static readonly DateTime RefilledAt = ReplacedAt.AddDays(-1);

    /// <summary>The machine movement that earlier refill is, which every ledger assertion starts from.</summary>
    private static readonly (MachineStockMovementKind, long, int, DateTime) EarlierRefill =
        (MachineStockMovementKind.Refill, OutgoingId, OutgoingInMachine, RefilledAt);

    #region One choice at a time

    [Fact]
    public async Task No_outgoing_choice_writes_the_incoming_refill_alone()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        var eventId = await SeedReplacementAsync(options);

        await using (var db = Scoped(options))
        {
            var result = await Apply(db).Handle(Machine, Request(eventId, 6), CancellationToken.None);

            Assert.Equal(NayaxStockEventApplyOutcome.Applied, result.Outcome);
            Assert.Equal("Product replacement recorded: 6 units loaded.", result.Message);
            Assert.NotNull(result.StockAdjustmentId);
        }

        await using var verify = Scoped(options);

        // Storage: the incoming product's units left the storeroom; the outgoing product is untouched.
        Assert.Equal(IncomingStock - 6, (await verify.Products.FindAsync(IncomingId))!.QuantityInStock);
        Assert.Equal(OutgoingStock, (await verify.Products.FindAsync(OutgoingId))!.QuantityInStock);

        // Costing: a refill is an internal transfer, so nothing about cost moved at all.
        await AssertCostingUnchangedAsync(verify, IncomingId, IncomingStock);
        await AssertCostingUnchangedAsync(verify, OutgoingId, OutgoingCosting);

        var movement = Assert.Single(await verify.StockAdjustments
            .Where(x => x.Source == StockAdjustmentSource.Nayax)
            .ToListAsync());
        Assert.Equal(StockAdjustmentReason.MachineRefill, movement.Reason);
        Assert.Equal(-6, movement.QuantityChange);
        Assert.Equal(IncomingId, movement.ProductId);
        Assert.Equal(LocalMachineId, movement.MachineId);
        Assert.Equal(ReplacedAt, movement.MachineLedgerEffectiveAt);
        Assert.Contains("EventLogID 1", movement.Notes, StringComparison.Ordinal);

        // The machine ledger: six units went into the machine, at the event's own instant.
        Assert.Equal(
            [EarlierRefill, (MachineStockMovementKind.Refill, IncomingId, 6, ReplacedAt)],
            await MachineMovementsAsync(verify));

        await AssertRecordedChoiceAsync(verify, eventId, 6, NayaxReplacementOutgoingDisposition.None, null, null);
    }

    [Fact]
    public async Task Returned_to_storage_puts_the_outgoing_units_back_and_costs_nothing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        var eventId = await SeedReplacementAsync(options);

        await using (var db = Scoped(options))
        {
            var result = await Apply(db).Handle(
                Machine,
                Request(eventId, 6, NayaxReplacementOutgoingDisposition.ReturnedToStorage, OutgoingId, 4),
                CancellationToken.None);

            Assert.Equal(NayaxStockEventApplyOutcome.Applied, result.Outcome);
            Assert.Equal(
                "Product replacement recorded: 6 units loaded, 4 units returned to storage.",
                result.Message);
        }

        await using var verify = Scoped(options);

        Assert.Equal(IncomingStock - 6, (await verify.Products.FindAsync(IncomingId))!.QuantityInStock);
        Assert.Equal(OutgoingStock + 4, (await verify.Products.FindAsync(OutgoingId))!.QuantityInStock);

        // A return is the reverse of a refill, so it is just as costing-neutral: the business still
        // owns the units, they are simply in the storeroom again.
        await AssertCostingUnchangedAsync(verify, IncomingId, IncomingStock);
        await AssertCostingUnchangedAsync(verify, OutgoingId, OutgoingCosting);

        var returned = Assert.Single(await verify.StockAdjustments
            .Where(x => x.Source == StockAdjustmentSource.Nayax && x.ProductId == OutgoingId)
            .ToListAsync());
        Assert.Equal(StockAdjustmentReason.MachineRefill, returned.Reason);
        Assert.Equal(4, returned.QuantityChange);
        Assert.Equal(LocalMachineId, returned.MachineId);

        // The machine ledger reads it as the return it is: four units left the machine.
        Assert.Equal(
            [
                EarlierRefill,
                (MachineStockMovementKind.Refill, IncomingId, 6, ReplacedAt),
                (MachineStockMovementKind.ReturnToStorage, OutgoingId, -4, ReplacedAt),
            ],
            await MachineMovementsAsync(verify));

        var stored = await verify.NayaxMachineStockEvents.SingleAsync(e => e.Id == eventId);
        Assert.Equal(returned.Id, stored.ReplacementOutgoingReturnStockAdjustmentId);
        Assert.Null(stored.ReplacementOutgoingWriteOffStockAdjustmentId);
        await AssertRecordedChoiceAsync(
            verify, eventId, 6, NayaxReplacementOutgoingDisposition.ReturnedToStorage, OutgoingId, 4);
    }

    [Theory]
    [InlineData(NayaxReplacementOutgoingDisposition.Damaged, StockAdjustmentReason.Damaged, "damaged")]
    [InlineData(NayaxReplacementOutgoingDisposition.Expired, StockAdjustmentReason.Expired, "expired")]
    public async Task A_disposal_returns_the_units_and_writes_them_off_so_storage_ends_unchanged(
        NayaxReplacementOutgoingDisposition disposition,
        StockAdjustmentReason expectedReason,
        string expectedWord)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        var eventId = await SeedReplacementAsync(options);

        await using (var db = Scoped(options))
        {
            var result = await Apply(db).Handle(
                Machine, Request(eventId, 6, disposition, OutgoingId, 4), CancellationToken.None);

            Assert.Equal(NayaxStockEventApplyOutcome.Applied, result.Outcome);
            Assert.Equal(
                $"Product replacement recorded: 6 units loaded, 4 units returned to storage and written off as {expectedWord}.",
                result.Message);
        }

        await using var verify = Scoped(options);

        // Storage ends exactly where it started for the outgoing product: the units were never in
        // the storeroom, so the return and the write-off cancel out.
        Assert.Equal(IncomingStock - 6, (await verify.Products.FindAsync(IncomingId))!.QuantityInStock);
        var outgoing = (await verify.Products.FindAsync(OutgoingId))!;
        Assert.Equal(OutgoingStock, outgoing.QuantityInStock);

        // But the loss is real and costed: four units left the business's costing inventory at the
        // product's own average unit cost.
        Assert.Equal(OutgoingCosting - 4, outgoing.CostingQuantity);
        Assert.Equal(OutgoingCosting - 4, outgoing.InventoryValue);
        Assert.Equal(1m, outgoing.AverageUnitCost);
        await AssertCostingUnchangedAsync(verify, IncomingId, IncomingStock);

        var writeOff = Assert.Single(await verify.StockAdjustments
            .Where(x => x.Reason == expectedReason)
            .ToListAsync());
        Assert.Equal(-4, writeOff.QuantityChange);
        Assert.Equal(OutgoingId, writeOff.ProductId);
        Assert.Equal(StockAdjustmentSource.Nayax, writeOff.Source);
        Assert.Equal(1m, writeOff.UnitCost);
        Assert.Equal(4m, writeOff.TotalCost);

        // The write-off is not a refill, so the machine ledger never reads it: the machine's own
        // fall of four units is the return's, and it is counted exactly once.
        Assert.Null(writeOff.MachineLedgerEffectiveAt);
        Assert.Equal(
            [
                EarlierRefill,
                (MachineStockMovementKind.Refill, IncomingId, 6, ReplacedAt),
                (MachineStockMovementKind.ReturnToStorage, OutgoingId, -4, ReplacedAt),
            ],
            await MachineMovementsAsync(verify));

        // No new adjustment reason: the three movements use the two reasons that already existed.
        Assert.Equal(
            [StockAdjustmentReason.MachineRefill, StockAdjustmentReason.MachineRefill, expectedReason],
            await verify.StockAdjustments
                .Where(x => x.Source == StockAdjustmentSource.Nayax)
                .OrderBy(x => x.Id)
                .Select(x => x.Reason)
                .ToListAsync());

        var stored = await verify.NayaxMachineStockEvents.SingleAsync(e => e.Id == eventId);
        Assert.Equal(writeOff.Id, stored.ReplacementOutgoingWriteOffStockAdjustmentId);
        Assert.NotNull(stored.ReplacementOutgoingReturnStockAdjustmentId);
        await AssertRecordedChoiceAsync(verify, eventId, 6, disposition, OutgoingId, 4);
    }

    [Fact]
    public async Task A_disposal_of_more_units_than_storage_holds_is_still_written_because_they_come_from_the_machine()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        var eventId = await SeedReplacementAsync(options);

        // The storeroom holds only 12 of the outgoing product, and all 20 units in the machine came
        // out damaged. The return puts those 20 in before the write-off takes them out, so neither
        // storage nor costing inventory is ever made negative - and the whole loss is costed.
        await using (var db = Scoped(options))
        {
            var result = await Apply(db).Handle(
                Machine,
                Request(eventId, 0, NayaxReplacementOutgoingDisposition.Damaged, OutgoingId, OutgoingInMachine),
                CancellationToken.None);

            Assert.Equal(NayaxStockEventApplyOutcome.Applied, result.Outcome);
            Assert.Equal(
                "Product replacement recorded: 20 units returned to storage and written off as damaged.",
                result.Message);

            // Nothing was loaded, so the event records no incoming movement at all.
            Assert.Null(result.StockAdjustmentId);
        }

        await using var verify = Scoped(options);
        var outgoing = (await verify.Products.FindAsync(OutgoingId))!;
        Assert.Equal(OutgoingStock, outgoing.QuantityInStock);
        Assert.Equal(OutgoingCosting - OutgoingInMachine, outgoing.CostingQuantity);
        Assert.Equal(OutgoingCosting - OutgoingInMachine, outgoing.InventoryValue);
        Assert.Equal(IncomingStock, (await verify.Products.FindAsync(IncomingId))!.QuantityInStock);

        // The machine is empty of it again, counted once, by the return alone.
        Assert.Equal(
            [EarlierRefill, (MachineStockMovementKind.ReturnToStorage, OutgoingId, -OutgoingInMachine, ReplacedAt)],
            await MachineMovementsAsync(verify));

        var stored = await verify.NayaxMachineStockEvents.SingleAsync(e => e.Id == eventId);
        Assert.Equal(NayaxStockEventProcessingStatus.Applied, stored.ProcessingStatus);
        Assert.Null(stored.StockAdjustmentId);
        await AssertRecordedChoiceAsync(
            verify, eventId, 0, NayaxReplacementOutgoingDisposition.Damaged, OutgoingId, OutgoingInMachine);
    }

    [Fact]
    public async Task Nothing_loaded_and_no_outgoing_choice_records_the_replacement_with_no_movement()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        var eventId = await SeedReplacementAsync(options);

        await using (var db = Scoped(options))
        {
            var result = await Apply(db).Handle(Machine, Request(eventId, 0), CancellationToken.None);

            Assert.Equal(NayaxStockEventApplyOutcome.Applied, result.Outcome);
            Assert.Equal("Product replacement recorded; no units were moved.", result.Message);
            Assert.Null(result.StockAdjustmentId);
        }

        await using var verify = Scoped(options);
        Assert.Empty(await verify.StockAdjustments.Where(x => x.Source == StockAdjustmentSource.Nayax).ToListAsync());
        Assert.Equal(IncomingStock, (await verify.Products.FindAsync(IncomingId))!.QuantityInStock);
        await AssertCostingUnchangedAsync(verify, IncomingId, IncomingStock);

        // It is settled all the same, so it leaves the Sync Restock working list.
        var stored = await verify.NayaxMachineStockEvents.SingleAsync(e => e.Id == eventId);
        Assert.Equal(NayaxStockEventProcessingStatus.Applied, stored.ProcessingStatus);
        Assert.Empty((await Store(verify).GetUnprocessedEventsAsync(NayaxMachineId, CancellationToken.None)).Events);
    }

    [Fact]
    public async Task One_product_on_both_sides_is_rebuilt_once_and_nets_out_correctly()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        var eventId = await SeedReplacementAsync(options);

        await using (var db = Scoped(options))
        {
            var rebuild = new CountingRebuild(TestCostingUseCases.Rebuild(db));
            var store = new EfMachineStockEventStore(db, TestCostingUseCases.RecordMovement(db), rebuild);

            var result = await new ApplyMachineStockProductReplacement(store, new RecordingMachineStockSyncLog())
                .Handle(
                    Machine,
                    Request(eventId, 6, NayaxReplacementOutgoingDisposition.ReturnedToStorage, IncomingId, 4),
                    CancellationToken.None);

            Assert.Equal(NayaxStockEventApplyOutcome.Applied, result.Outcome);
            Assert.Equal([IncomingId], rebuild.Products);
        }

        await using var verify = Scoped(options);
        Assert.Equal(IncomingStock - 6 + 4, (await verify.Products.FindAsync(IncomingId))!.QuantityInStock);
    }

    #endregion

    #region Refusals, each leaving the event pending

    [Fact]
    public async Task More_units_than_storage_holds_is_refused_and_writes_nothing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        var eventId = await SeedReplacementAsync(options);

        await using (var db = Scoped(options))
        {
            var result = await Apply(db).Handle(
                Machine, Request(eventId, IncomingStock + 1), CancellationToken.None);

            Assert.Equal(NayaxStockEventApplyOutcome.InsufficientStock, result.Outcome);
            Assert.Equal(
                NayaxProductReplacementWritePolicy.InsufficientStorageMessage(IncomingStock + 1, IncomingStock),
                result.Message);
        }

        await AssertNothingHappenedAsync(options, eventId);
    }

    [Fact]
    public async Task A_retired_foreign_or_missing_incoming_product_is_refused_with_one_sentence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        var eventId = await SeedReplacementAsync(options);

        foreach (var productId in new[] { RetiredId, ForeignId, 999_999L })
        {
            await using var db = Scoped(options);
            var result = await Apply(db).Handle(
                Machine,
                new NayaxApplyProductReplacementRequestDto(
                    eventId, productId, 2, NayaxReplacementOutgoingDisposition.None),
                CancellationToken.None);

            Assert.Equal(NayaxStockEventApplyOutcome.NotApplicable, result.Outcome);
            Assert.Equal(NayaxProductReplacementWritePolicy.IncomingProductUnavailableMessage, result.Message);
        }

        await AssertNothingHappenedAsync(options, eventId);
    }

    [Fact]
    public async Task A_retired_foreign_or_missing_outgoing_product_is_refused_with_its_own_sentence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        var eventId = await SeedReplacementAsync(options);

        foreach (var productId in new[] { RetiredId, ForeignId, 999_999L })
        {
            await using var db = Scoped(options);
            var result = await Apply(db).Handle(
                Machine,
                Request(eventId, 2, NayaxReplacementOutgoingDisposition.Expired, productId, 3),
                CancellationToken.None);

            Assert.Equal(NayaxStockEventApplyOutcome.NotApplicable, result.Outcome);
            Assert.Equal(NayaxProductReplacementWritePolicy.OutgoingProductUnavailableMessage, result.Message);
        }

        await AssertNothingHappenedAsync(options, eventId);
    }

    [Fact]
    public async Task A_choice_that_does_not_make_sense_is_refused_and_writes_nothing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        var eventId = await SeedReplacementAsync(options);

        await using (var db = Scoped(options))
        {
            var apply = Apply(db);

            Assert.Equal(
                NayaxProductReplacementWritePolicy.IncomingQuantityMessage,
                (await apply.Handle(Machine, Request(eventId, -1), CancellationToken.None)).Message);

            Assert.Equal(
                NayaxProductReplacementWritePolicy.OutgoingQuantityMessage,
                (await apply.Handle(
                    Machine,
                    Request(eventId, 2, NayaxReplacementOutgoingDisposition.Damaged, OutgoingId, 0),
                    CancellationToken.None)).Message);

            Assert.Equal(
                NayaxProductReplacementWritePolicy.OutgoingProductRequiredMessage,
                (await apply.Handle(
                    Machine,
                    Request(eventId, 2, NayaxReplacementOutgoingDisposition.ReturnedToStorage, null, 3),
                    CancellationToken.None)).Message);

            Assert.Equal(
                NayaxProductReplacementWritePolicy.NoOutgoingDetailsMessage,
                (await apply.Handle(
                    Machine,
                    Request(eventId, 2, NayaxReplacementOutgoingDisposition.None, OutgoingId, 3),
                    CancellationToken.None)).Message);
        }

        await AssertNothingHappenedAsync(options, eventId);
    }

    [Fact]
    public async Task A_501_stock_adjustment_is_refused_by_this_action_and_an_unknown_event_is_not_found()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        int adjustmentEventId;

        await using (var seed = Scoped(options))
        {
            var row = StockAdjustmentEvent();
            seed.NayaxMachineStockEvents.Add(row);
            await seed.SaveChangesAsync();
            adjustmentEventId = row.Id;
        }

        await using (var db = Scoped(options))
        {
            var refused = await Apply(db).Handle(
                Machine, Request(adjustmentEventId, 3), CancellationToken.None);
            Assert.Equal(NayaxStockEventApplyOutcome.NotApplicable, refused.Outcome);
            Assert.Equal(ApplyMachineStockProductReplacement.NotAReplacementMessage, refused.Message);

            var missing = await Apply(db).Handle(Machine, Request(987_654, 3), CancellationToken.None);
            Assert.Equal(NayaxStockEventApplyOutcome.Error, missing.Outcome);
        }

        await using var verify = Scoped(options);
        Assert.Empty(await verify.StockAdjustments.Where(x => x.Source == StockAdjustmentSource.Nayax).ToListAsync());
        var stored = await verify.NayaxMachineStockEvents.SingleAsync(e => e.Id == adjustmentEventId);
        Assert.Equal(NayaxStockEventProcessingStatus.Unprocessed, stored.ProcessingStatus);
        Assert.Null(stored.ReplacementIncomingProductId);
    }

    [Fact]
    public async Task An_event_already_recorded_manually_is_refused_as_a_conflict()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        var eventId = await SeedReplacementAsync(options);

        await using (var db = Scoped(options))
        {
            await new ResolveMachineStockEventsAsAlreadyRecorded(Store(db))
                .Handle(Machine, [eventId], CancellationToken.None);
        }

        await using (var db = Scoped(options))
        {
            var result = await Apply(db).Handle(Machine, Request(eventId, 6), CancellationToken.None);

            Assert.Equal(NayaxStockEventApplyOutcome.ResolutionConflict, result.Outcome);
            Assert.Null(result.StockAdjustmentId);
        }

        await using var verify = Scoped(options);
        Assert.Empty(await verify.StockAdjustments.Where(x => x.Source == StockAdjustmentSource.Nayax).ToListAsync());
        Assert.Equal(IncomingStock, (await verify.Products.FindAsync(IncomingId))!.QuantityInStock);
    }

    #endregion

    #region It cannot half-apply or apply twice

    [Fact]
    public async Task A_failing_second_movement_rolls_the_first_one_back_and_leaves_the_event_pending()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        var eventId = await SeedReplacementAsync(options);
        var log = new RecordingMachineStockSyncLog();

        await using (var db = Scoped(options))
        {
            // The incoming refill is staged, and the outgoing units' return then fails. Nothing may
            // survive: neither the first movement nor the applied state that names it.
            var movements = FailsOnMovement.Number(2, TestCostingUseCases.RecordMovement(db));
            var store = new EfMachineStockEventStore(db, movements, TestCostingUseCases.Rebuild(db));

            var result = await new ApplyMachineStockProductReplacement(store, log).Handle(
                Machine,
                Request(eventId, 6, NayaxReplacementOutgoingDisposition.Damaged, OutgoingId, 4),
                CancellationToken.None);

            Assert.Equal(NayaxStockEventApplyOutcome.Error, result.Outcome);
            Assert.Equal(ApplyMachineStockSync.FailedMessage, result.Message);
            Assert.Null(result.StockAdjustmentId);
        }

        // One log line, so a replacement that never landed is not invisible (issue #1240).
        var logged = Assert.Single(log.Failures);
        Assert.Equal(eventId, logged.EventId);
        Assert.Null(logged.Reason);

        await AssertNothingHappenedAsync(options, eventId);

        // And it can simply be applied again.
        await using (var retry = Scoped(options))
        {
            var again = await Apply(retry).Handle(
                Machine,
                Request(eventId, 6, NayaxReplacementOutgoingDisposition.Damaged, OutgoingId, 4),
                CancellationToken.None);
            Assert.Equal(NayaxStockEventApplyOutcome.Applied, again.Outcome);
        }

        await using var reread = Scoped(options);
        Assert.Equal(IncomingStock - 6, (await reread.Products.FindAsync(IncomingId))!.QuantityInStock);
        Assert.Equal(OutgoingCosting - 4, (await reread.Products.FindAsync(OutgoingId))!.CostingQuantity);
    }

    [Fact]
    public async Task A_failing_write_off_rolls_back_the_return_that_preceded_it()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        var eventId = await SeedReplacementAsync(options);

        await using (var db = Scoped(options))
        {
            // The third movement is the write-off, so the return is already staged when it fails:
            // the outgoing product must not be left holding units it no longer has.
            var movements = FailsOnMovement.Number(3, TestCostingUseCases.RecordMovement(db));
            var store = new EfMachineStockEventStore(db, movements, TestCostingUseCases.Rebuild(db));

            var result = await new ApplyMachineStockProductReplacement(store, new RecordingMachineStockSyncLog())
                .Handle(
                    Machine,
                    Request(eventId, 6, NayaxReplacementOutgoingDisposition.Expired, OutgoingId, 4),
                    CancellationToken.None);

            Assert.Equal(NayaxStockEventApplyOutcome.Error, result.Outcome);
        }

        await AssertNothingHappenedAsync(options, eventId);
    }

    [Fact]
    public async Task Applying_the_same_event_again_writes_nothing_and_reports_the_movement_it_recorded()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        var eventId = await SeedReplacementAsync(options);
        int? firstMovement;

        await using (var db = Scoped(options))
        {
            var first = await Apply(db).Handle(
                Machine,
                Request(eventId, 6, NayaxReplacementOutgoingDisposition.Damaged, OutgoingId, 4),
                CancellationToken.None);
            Assert.Equal(NayaxStockEventApplyOutcome.Applied, first.Outcome);
            firstMovement = first.StockAdjustmentId;
        }

        await using (var db = Scoped(options))
        {
            // A different, larger choice the second time: the settled event is replayed, never
            // re-decided, so none of it is written.
            var replay = await Apply(db).Handle(
                Machine,
                Request(eventId, 9, NayaxReplacementOutgoingDisposition.Expired, OutgoingId, 7),
                CancellationToken.None);

            Assert.Equal(NayaxStockEventApplyOutcome.Applied, replay.Outcome);
            Assert.Equal("Already applied.", replay.Message);
            Assert.Equal(firstMovement, replay.StockAdjustmentId);
        }

        await using var verify = Scoped(options);
        Assert.Equal(3, await verify.StockAdjustments.CountAsync(x => x.Source == StockAdjustmentSource.Nayax));
        Assert.Equal(IncomingStock - 6, (await verify.Products.FindAsync(IncomingId))!.QuantityInStock);
        var outgoing = (await verify.Products.FindAsync(OutgoingId))!;
        Assert.Equal(OutgoingStock, outgoing.QuantityInStock);
        Assert.Equal(OutgoingCosting - 4, outgoing.CostingQuantity);
        await AssertRecordedChoiceAsync(
            verify, eventId, 6, NayaxReplacementOutgoingDisposition.Damaged, OutgoingId, 4);
    }

    /// <summary>
    /// Two applies of one event started together, on two connections to a real SQLite <strong>file</strong> -
    /// which is the only way two writers and SQLite's own locking exist at all. Whichever takes the
    /// write lock first is genuinely up to SQLite; what is asserted is that the outcome does not
    /// depend on the answer.
    /// </summary>
    [Fact]
    public async Task The_loser_of_two_concurrent_applies_writes_nothing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sync-restock-replacement-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Default Timeout=30";

        try
        {
            await using var keepAlive = new SqliteConnection(connectionString);
            var options = await CreateDatabaseAsync(keepAlive, connectionString);
            var eventId = await SeedReplacementAsync(options);

            await using var first = Scoped(options);
            await using var second = Scoped(options);

            var both = await Task.WhenAll(
                Task.Run(() => Apply(first).Handle(Machine, Request(eventId, 6), CancellationToken.None)),
                Task.Run(() => Apply(second).Handle(Machine, Request(eventId, 6), CancellationToken.None)));

            Assert.All(both, result => Assert.Equal(NayaxStockEventApplyOutcome.Applied, result.Outcome));

            // The loser waited, re-read the winner's committed state inside its own lock, and
            // reported the established replay rather than deducting storage a second time.
            Assert.Contains(MachineStockResolutionMessagesAlreadyApplied, both.Select(result => result.Message));

            await using var verify = Scoped(options);
            var movement = Assert.Single(await verify.StockAdjustments
                .Where(x => x.Source == StockAdjustmentSource.Nayax)
                .ToListAsync());
            Assert.Equal(-6, movement.QuantityChange);
            Assert.Equal(IncomingStock - 6, (await verify.Products.FindAsync(IncomingId))!.QuantityInStock);
            await AssertRecordedChoiceAsync(verify, eventId, 6, NayaxReplacementOutgoingDisposition.None, null, null);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    /// <summary>The established replay sentence, which is internal to the Application layer.</summary>
    private const string MachineStockResolutionMessagesAlreadyApplied = "Already applied.";

    #endregion

    #region What an apply must never touch

    [Fact]
    public async Task Applying_a_replacement_changes_no_product_link_and_no_stored_match()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        var eventId = await SeedReplacementAsync(options);

        await using (var db = Scoped(options))
        {
            await Apply(db).Handle(
                Machine,
                Request(eventId, 6, NayaxReplacementOutgoingDisposition.ReturnedToStorage, OutgoingId, 4),
                CancellationToken.None);
        }

        await using var verify = Scoped(options);

        // A slot's product link is Product.ExternalProductId, and none of them moved: which product
        // an MDB position holds keeps being read live from Nayax.
        Assert.Equal(
            [
                (IncomingId, NayaxExternalId.From(IncomingId)),
                (OutgoingId, null),
                (RetiredId, null),
            ],
            await verify.Products
                .OrderBy(p => p.Id)
                .Select(p => ValueTuple.Create(p.Id, p.ExternalProductId))
                .ToListAsync());

        // The replacement's own match columns are left exactly as the import left them: an applied
        // replacement records what moved, and is not a stored match anything else reads.
        var stored = await verify.NayaxMachineStockEvents.SingleAsync(e => e.Id == eventId);
        Assert.Null(stored.MatchedProductId);
        Assert.Equal(NayaxStockEventMatchStatus.NeedsReview, stored.MatchStatus);
        Assert.Equal(Mdb, stored.ParsedMdb);
        Assert.Equal(IncomingName, stored.ParsedProductName);
        Assert.Equal(OutgoingName, stored.ParsedOutgoingProductName);
    }

    [Fact]
    public async Task Another_business_sees_none_of_an_applied_replacement()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        var eventId = await SeedReplacementAsync(options);

        await using (var db = Scoped(options))
        {
            await Apply(db).Handle(
                Machine,
                Request(eventId, 6, NayaxReplacementOutgoingDisposition.Damaged, OutgoingId, 4),
                CancellationToken.None);
        }

        await using var other = TestAppDbContext.For(options, OtherBusinessId);
        Assert.Empty(await other.NayaxMachineStockEvents.ToListAsync());
        Assert.Empty(await other.StockAdjustments.Where(x => x.Source == StockAdjustmentSource.Nayax).ToListAsync());
        Assert.Equal([ForeignId], await other.Products.OrderBy(p => p.Id).Select(p => p.Id).ToListAsync());
    }

    #endregion

    #region Fixture

    private static NayaxApplyProductReplacementRequestDto Request(
        int eventId,
        int incomingQuantity,
        NayaxReplacementOutgoingDisposition disposition = NayaxReplacementOutgoingDisposition.None,
        long? outgoingProductId = null,
        int? outgoingQuantity = null) =>
        new(eventId, IncomingId, incomingQuantity, disposition, outgoingProductId, outgoingQuantity);

    private static EfMachineStockEventStore Store(AppDbContext db) =>
        new(db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db));

    private static ApplyMachineStockProductReplacement Apply(AppDbContext db) =>
        new(Store(db), new RecordingMachineStockSyncLog());

    private static AppDbContext Scoped(DbContextOptions<AppDbContext> options) =>
        TestAppDbContext.For(options, BusinessId);

    /// <summary>The imported 510 row, exactly as issue #1325's import leaves it.</summary>
    private static NayaxMachineStockEvent ReplacementEvent() => new()
    {
        NayaxEventLogId = 1,
        MachineId = NayaxMachineId,
        EventCode = NayaxMachineAlertEventCodes.MachineProductChanged,
        EventDateTimeGmt = ReplacedAt,
        EventDateTimeVmc = ReplacedAt.AddHours(11),
        RawEventData =
            $"[operator] 'has replaced product {OutgoingName} with product {IncomingName} | Product MDB: {Mdb}",
        ParsedMdb = Mdb,
        ParsedProductName = IncomingName,
        ParsedOutgoingProductName = OutgoingName,
        MatchStatus = NayaxStockEventMatchStatus.NeedsReview,
    };

    /// <summary>An ordinary 501 row, which this action must refuse.</summary>
    private static NayaxMachineStockEvent StockAdjustmentEvent() => new()
    {
        NayaxEventLogId = 2,
        MachineId = NayaxMachineId,
        EventCode = NayaxMachineAlertEventCodes.StockAdjustForMachine,
        EventDateTimeGmt = ReplacedAt,
        RawEventData = $"Product MDB: {Mdb} | {IncomingName} | 6",
        ParsedMdb = Mdb,
        ParsedProductName = IncomingName,
        ParsedQuantity = 6,
        MatchedProductId = IncomingId,
        MatchStatus = NayaxStockEventMatchStatus.Matched,
    };

    private static async Task<int> SeedReplacementAsync(DbContextOptions<AppDbContext> options)
    {
        await using var seed = Scoped(options);
        var row = ReplacementEvent();
        seed.NayaxMachineStockEvents.Add(row);
        await seed.SaveChangesAsync();
        return row.Id;
    }

    /// <summary>
    /// Every machine-stock movement the ledger reads for this machine, as the one Domain rule
    /// normalises it: its kind, product, change to the machine's own units, and the instant it is
    /// replayed at.
    /// </summary>
    private static async Task<List<(MachineStockMovementKind Kind, long ProductId, int MachineQuantityChange, DateTime EffectiveAtUtc)>>
        MachineMovementsAsync(AppDbContext db)
    {
        var facts = await new EfMachineStockLedgerStore(db)
            .GetLedgerFactsAsync(Machine, [], CancellationToken.None);

        return facts.Refills
            .Select(MachineStockMovementPolicy.FromRefill)
            .OrderBy(movement => movement.SourceId)
            .Select(movement => (
                movement.Kind, movement.ProductId, movement.MachineQuantityChange, movement.EffectiveAtUtc))
            .ToList();
    }

    private static async Task AssertCostingUnchangedAsync(AppDbContext db, long productId, int opening)
    {
        var product = (await db.Products.FindAsync(productId))!;
        Assert.Equal(opening, product.CostingQuantity);
        Assert.Equal(opening, product.InventoryValue);
        Assert.Equal(1m, product.AverageUnitCost);
    }

    private static async Task AssertRecordedChoiceAsync(
        AppDbContext db,
        int eventId,
        int incomingQuantity,
        NayaxReplacementOutgoingDisposition disposition,
        long? outgoingProductId,
        int? outgoingQuantity)
    {
        var stored = await db.NayaxMachineStockEvents.SingleAsync(e => e.Id == eventId);

        Assert.Equal(NayaxStockEventProcessingStatus.Applied, stored.ProcessingStatus);
        Assert.NotNull(stored.ProcessedAt);
        Assert.Equal(IncomingId, stored.ReplacementIncomingProductId);
        Assert.Equal(incomingQuantity, stored.ReplacementIncomingQuantity);
        Assert.Equal(disposition, stored.ReplacementOutgoingDisposition);
        Assert.Equal(outgoingProductId, stored.ReplacementOutgoingProductId);
        Assert.Equal(outgoingQuantity, stored.ReplacementOutgoingQuantity);
    }

    /// <summary>
    /// The event is still pending, nothing was written and neither product's storage, costing
    /// quantity or inventory value moved - the whole of what a refused or failed apply must leave.
    /// </summary>
    private static async Task AssertNothingHappenedAsync(DbContextOptions<AppDbContext> options, int eventId)
    {
        await using var verify = Scoped(options);

        Assert.Empty(await verify.StockAdjustments.Where(x => x.Source == StockAdjustmentSource.Nayax).ToListAsync());
        Assert.Equal(IncomingStock, (await verify.Products.FindAsync(IncomingId))!.QuantityInStock);
        Assert.Equal(OutgoingStock, (await verify.Products.FindAsync(OutgoingId))!.QuantityInStock);
        await AssertCostingUnchangedAsync(verify, IncomingId, IncomingStock);
        await AssertCostingUnchangedAsync(verify, OutgoingId, OutgoingCosting);

        var stored = await verify.NayaxMachineStockEvents.SingleAsync(e => e.Id == eventId);
        Assert.Equal(NayaxStockEventProcessingStatus.Unprocessed, stored.ProcessingStatus);
        Assert.Null(stored.ProcessedAt);
        Assert.Null(stored.StockAdjustmentId);
        Assert.Null(stored.ReplacementIncomingProductId);
        Assert.Null(stored.ReplacementIncomingQuantity);
        Assert.Null(stored.ReplacementOutgoingDisposition);
        Assert.Null(stored.ReplacementOutgoingProductId);
        Assert.Null(stored.ReplacementOutgoingQuantity);
        Assert.Null(stored.ReplacementOutgoingReturnStockAdjustmentId);
        Assert.Null(stored.ReplacementOutgoingWriteOffStockAdjustmentId);
    }

    /// <summary>
    /// The schema, two businesses, the machine and the costed products. <paramref name="connectionString"/>
    /// is given only by the concurrency case, which needs a file every writer can open its own
    /// connection to; every other case shares one in-memory connection.
    /// </summary>
    private static async Task<DbContextOptions<AppDbContext>> CreateDatabaseAsync(
        SqliteConnection connection, string? connectionString = null)
    {
        await connection.OpenAsync();
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        var options = (connectionString is null
            ? builder.UseSqlite(connection)
            : builder.UseSqlite(connectionString)).Options;

        await using (var schema = TestAppDbContext.Unrestricted(options))
        {
            await schema.Database.EnsureCreatedAsync();
            schema.Businesses.AddRange(Business(BusinessId, "Vending A"), Business(OtherBusinessId, "Vending B"));
            schema.Machines.Add(new LocalMachine
            {
                BusinessId = BusinessId,
                Id = LocalMachineId,
                Name = "Avenue Hotel",
                ExternalMachineId = NayaxExternalId.From(NayaxMachineId),
            });
            await schema.SaveChangesAsync();
        }

        await using (var seed = Scoped(options))
        {
            // The outgoing product's history is the state a replacement's outgoing units come out
            // of: bought once, then refilled into this machine. Storage therefore holds less than
            // costing inventory does, because the machine is holding the difference and a refill
            // consumes no costing inventory at all.
            seed.Products.AddRange(
                CostedProduct(IncomingId, IncomingName, IncomingStock, NayaxExternalId.From(IncomingId)),
                CostedProduct(OutgoingId, OutgoingName, OutgoingStock, costingQuantity: OutgoingCosting),
                CostedProduct(RetiredId, "Mother 500mL", 5, isActive: false));
            seed.StockAdjustments.AddRange(
                Opening(IncomingId, IncomingStock),
                Opening(OutgoingId, OutgoingCosting),
                Opening(RetiredId, 5),
                ManualRefillIntoTheMachine(OutgoingId, OutgoingInMachine));
            await seed.SaveChangesAsync();
        }

        await using (var foreign = TestAppDbContext.For(options, OtherBusinessId))
        {
            foreign.Products.Add(CostedProduct(ForeignId, "Another business's drink", 50));
            await foreign.SaveChangesAsync();
        }

        return options;
    }

    private static Business Business(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Status = BusinessStatus.Active,
        SalesProvider = ExternalProvider.Nayax,
        CreatedAtUtc = ReplacedAt.AddDays(-30),
    };

    /// <summary>
    /// A product whose stored stock agrees with its history: one costed opening restock at $1.00 a
    /// unit. <paramref name="costingQuantity"/> differs from the storage quantity only for a product
    /// some of whose units are in a machine.
    /// </summary>
    private static Product CostedProduct(
        long id,
        string name,
        int quantityInStock,
        string? externalProductId = null,
        bool isActive = true,
        int? costingQuantity = null) => new()
        {
            Id = id,
            Name = name,
            ExternalProductId = externalProductId,
            IsActive = isActive,
            QuantityInStock = quantityInStock,
            CostingQuantity = costingQuantity ?? quantityInStock,
            InventoryValue = costingQuantity ?? quantityInStock,
            AverageUnitCost = 1m,
        };

    private static StockAdjustment Opening(long productId, int quantity) => new()
    {
        ProductId = productId,
        QuantityChange = quantity,
        QuantityAfter = quantity,
        Reason = StockAdjustmentReason.Restock,
        UnitCost = 1m,
        EffectiveAt = ReplacedAt.AddDays(-2),
    };

    /// <summary>
    /// An operator's own earlier refill of this machine: storage down, machine up, costing untouched.
    /// It is what puts the outgoing product's units in the machine for the replacement to take out.
    /// </summary>
    private static StockAdjustment ManualRefillIntoTheMachine(long productId, int quantity) => new()
    {
        ProductId = productId,
        QuantityChange = -quantity,
        QuantityAfter = OutgoingCosting - quantity,
        Reason = StockAdjustmentReason.MachineRefill,
        Source = StockAdjustmentSource.Manual,
        MachineId = LocalMachineId,
        EffectiveAt = RefilledAt,
    };

    /// <summary>
    /// The real rebuild, recording every product one apply replayed, in order - so "once per product
    /// whose stock moved" is asserted rather than assumed.
    /// </summary>
    private sealed class CountingRebuild(IRebuildProductCost inner) : IRebuildProductCost
    {
        private readonly List<long> _products = [];

        public IReadOnlyList<long> Products => _products;

        public Task<InventoryCostRebuildResult> RebuildAsync(
            long productId,
            DateTime? recostCompletedSalesFrom = null,
            bool dryRun = false,
            CancellationToken cancellationToken = default)
        {
            _products.Add(productId);
            return inner.RebuildAsync(productId, recostCompletedSalesFrom, dryRun, cancellationToken);
        }

        public Task<InventoryCostRebuildResult> RebuildCostingOnlyAsync(
            long productId, DateTime recostCompletedSalesFrom, CancellationToken cancellationToken = default) =>
            inner.RebuildCostingOnlyAsync(productId, recostCompletedSalesFrom, cancellationToken);

        public Task<decimal?> GetAverageUnitCostAtAsync(
            long productId,
            DateTime saleTime,
            long? saleTransactionId = null,
            CancellationToken cancellationToken = default) =>
            inner.GetAverageUnitCostAtAsync(productId, saleTime, saleTransactionId, cancellationToken);

        public Task<decimal?> GetLatestPurchaseUnitCostBeforeAsync(
            long productId,
            DateTime saleTime,
            long? saleTransactionId = null,
            CancellationToken cancellationToken = default) =>
            inner.GetLatestPurchaseUnitCostBeforeAsync(productId, saleTime, saleTransactionId, cancellationToken);
    }

    /// <summary>
    /// The real movement recorder, except that the nth call fails with a provider error - the shape
    /// of a movement that cannot be recorded at all.
    /// </summary>
    private sealed class FailsOnMovement : IRecordInventoryMovement
    {
        private readonly int _failingCall;
        private readonly IRecordInventoryMovement _inner;
        private int _calls;

        private FailsOnMovement(int failingCall, IRecordInventoryMovement inner)
        {
            _failingCall = failingCall;
            _inner = inner;
        }

        public static FailsOnMovement Number(int failingCall, IRecordInventoryMovement inner) =>
            new(failingCall, inner);

        public Task<IStagedInventoryMovement> RecordAsync(
            InventoryMovement movement, CancellationToken cancellationToken) =>
            ++_calls == _failingCall
                ? throw new SqliteException("SQLite Error 5: 'database is locked'.", 5)
                : _inner.RecordAsync(movement, cancellationToken);
    }

    #endregion
}
