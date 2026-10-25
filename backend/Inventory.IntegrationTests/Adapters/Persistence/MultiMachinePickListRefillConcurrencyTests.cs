using Inventory.Application.PickList;
using Inventory.Domain.Stock;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

// Both layers declare this enum (the shared wire vocabulary documented on
// StockAdjustmentResponseMapper); the persisted rows this test writes and reads carry the
// persistence one.
using StockAdjustmentReason = Inventory.Infrastructure.Models.StockAdjustmentReason;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The multi-machine refill batch under real concurrency (issue #897).
///
/// <para>This is the test the design exists for. "Is there enough in storage for every machine this
/// product is going to?", "is this machine still in service?" and "has this key already recorded a
/// refill?" are all check-then-write rules, and a check-then-write rule that is not serialised is not
/// a rule: two pickers both read "enough stock" and both deduct, leaving storage reporting units that
/// are not there, and two copies of one submission both read "no refill under this key" and both
/// record one, deducting the shelf twice for one run. Neither outcome is cosmetic - they are the two
/// ways a refill endpoint silently corrupts an inventory quantity.</para>
///
/// <para>It therefore runs against a real SQLite <strong>file</strong> with independent connections,
/// because that is the only way to have genuine concurrent writers: a shared in-memory database is one
/// connection, and InMemory has no locking at all. The requests are released together at a barrier, so
/// the window the rules have to close is actually opened. It is the same shape as
/// <see cref="PickListRefillConcurrencyTests"/> (issue #743) and
/// <see cref="BusinessMembershipWriteTransactionTests"/> (issue #522), for the same reason. The
/// database is built by <c>Migrate()</c>, so these tests also run on the schema the migration history
/// produces rather than on a model snapshot.</para>
/// </summary>
public class MultiMachinePickListRefillConcurrencyTests : IDisposable
{
    private const int BusinessA = 1;
    private const long ProductId = 100;
    private const long MachineA = 7;
    private const long MachineB = 8;

    private static readonly DateTime RecordedAt = new(2026, 10, 25, 2, 30, 0, DateTimeKind.Utc);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"inventory-multi-machine-refills-{Guid.NewGuid():N}.db");

    private readonly string _connectionString;

    public MultiMachinePickListRefillConcurrencyTests()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

        using var db = TestAppDbContext.Unrestricted(Options());
        db.Database.Migrate();
        db.Businesses.Add(new Business
        {
            Id = BusinessA,
            Name = "Vending Co",
            Status = BusinessStatus.Active,
            CreatedAtUtc = RecordedAt,
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        GC.SuppressFinalize(this);
    }

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options;

    /// <summary>
    /// Seeds the product with an opening costed Restock, so the cost rebuild each allocation triggers
    /// has a real history to replay, and the two machines a run visits.
    /// </summary>
    private void Seed(int quantity)
    {
        using var db = TestAppDbContext.For(Options(), BusinessA);
        db.Products.Add(new Product { Id = ProductId, Name = "Coke Zero", QuantityInStock = quantity });
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = ProductId,
            QuantityChange = quantity,
            QuantityAfter = quantity,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = 2m,
            EffectiveAt = RecordedAt.AddDays(-1),
        });
        db.Machines.Add(new LocalMachine { Id = MachineA, Name = "Machine A", IsActive = true });
        db.Machines.Add(new LocalMachine { Id = MachineB, Name = "Machine B", IsActive = true });
        db.SaveChanges();
    }

    private static RecordMultiMachinePickListRefill UseCaseFor(AppDbContext db) =>
        new(
            new EfMultiMachinePickListRefillStore(
                db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db)),
            new EfPickListStorageStockStore(db),
            new EfLocalMachineStore(db),
            new FakeClock(RecordedAt));

    private static RecordPickListRefill SingleMachineUseCaseFor(AppDbContext db) =>
        new(
            new EfPickListRefillStore(
                db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db)),
            new EfPickListStorageStockStore(db),
            new EfLocalMachineStore(db),
            new FakeClock(RecordedAt));

    /// <summary>What one racing operation did: its outcome, its first refusal, or its exception.</summary>
    private sealed record Answer(PickListRefillOutcome? Outcome, string? Refusal, Exception? Failure);

    /// <summary>
    /// Runs the operations released at the same instant, each on its own context and its own
    /// connection - the way concurrent HTTP requests arrive - and returns what each one answered.
    /// </summary>
    private async Task<Answer[]> RaceAsync(params Func<AppDbContext, Task<Answer>>[] operations)
    {
        using var barrier = new Barrier(operations.Length);

        var running = operations
            .Select(operation => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                try
                {
                    await using var db = TestAppDbContext.For(Options(), BusinessA);
                    return await operation(db);
                }
                catch (Exception exception)
                {
                    return new Answer(null, null, exception);
                }
            }))
            .ToArray();

        return await Task.WhenAll(running);
    }

    /// <summary>One multi-machine batch, as a racing operation.</summary>
    private static Func<AppDbContext, Task<Answer>> Batch(
        string key, params (long MachineId, int Quantity)[] allocations) =>
        async db =>
        {
            var attempt = await UseCaseFor(db).Handle(
                new MultiMachinePickListRefillInput(
                    key,
                    null,
                    [.. allocations.Select(allocation => new MultiMachineRefillAllocation(
                        allocation.MachineId, ProductId, allocation.Quantity))]),
                CancellationToken.None);

            return new Answer(
                attempt.Outcome, attempt.Rejection?.Allocations.FirstOrDefault()?.Message, null);
        };

    /// <summary>One single-machine refill of issue #743, as a racing operation.</summary>
    private static Func<AppDbContext, Task<Answer>> SingleMachineRefill(string key, int quantity) =>
        async db =>
        {
            var attempt = await SingleMachineUseCaseFor(db).Handle(
                new PickListRefillInput(
                    key, MachineA, null, [new PickListRefillRequestRow(ProductId, quantity)]),
                CancellationToken.None);

            return new Answer(attempt.Outcome, attempt.Rejection?.Rows.FirstOrDefault()?.Message, null);
        };

    private (int QuantityInStock, int RefillCount, int LineCount, int MovementCount) Read()
    {
        using var db = TestAppDbContext.For(Options(), BusinessA);
        return (
            db.Products.AsNoTracking().Single(product => product.Id == ProductId).QuantityInStock,
            db.PickListRefills.AsNoTracking().Count(),
            db.PickListRefillLines.AsNoTracking().Count(),
            db.StockAdjustments.AsNoTracking()
                .Count(adjustment => adjustment.Reason == StockAdjustmentReason.MachineRefill));
    }

    /// <summary>
    /// Two copies of one submission - a client that retried before the first response arrived - deduct
    /// the shelf once. One of them records it and the other is answered with the recorded result;
    /// which is which is a race, and both answers describe the same batch.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_submissions_of_one_key_record_once()
    {
        const string key = "one-run-sent-twice";
        Seed(10);

        var answers = await RaceAsync(
            Batch(key, (MachineA, 3), (MachineB, 2)),
            Batch(key, (MachineA, 3), (MachineB, 2)));

        Assert.All(answers, answer => Assert.Null(answer.Failure));
        Assert.Single(answers.Where(answer => answer.Outcome == PickListRefillOutcome.Recorded));
        Assert.Single(answers.Where(answer => answer.Outcome == PickListRefillOutcome.Replayed));

        var (quantityInStock, refills, lines, movements) = Read();
        Assert.Equal(5, quantityInStock);
        Assert.Equal(1, refills);
        Assert.Equal(2, lines);
        Assert.Equal(2, movements);
    }

    /// <summary>
    /// The acceptance criterion with teeth: two different runs that each pass the storage check on
    /// their own, but together take more than the shelf holds. Exactly one may succeed, and the loser
    /// has to be told the same thing it would have been told sequentially - a total against the
    /// quantity the winner left - and must leave nothing behind, not even its first allocation.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_batches_that_together_exceed_storage_leave_exactly_one()
    {
        Seed(10);

        var answers = await RaceAsync(
            Batch("first-run", (MachineA, 4), (MachineB, 4)),
            Batch("second-run", (MachineA, 4), (MachineB, 4)));

        Assert.All(answers, answer => Assert.Null(answer.Failure));
        Assert.Single(answers.Where(answer => answer.Outcome == PickListRefillOutcome.Recorded));

        var rejected = Assert.Single(
            answers.Where(answer => answer.Outcome == PickListRefillOutcome.Rejected));

        // Refused against the quantity the winner actually left, not the one it started from.
        Assert.Equal(
            MultiMachinePickListRefillPolicy.NotEnoughStockForTotalMessage(8, 2), rejected.Refusal);

        var (quantityInStock, refills, lines, movements) = Read();
        Assert.Equal(2, quantityInStock);
        Assert.Equal(1, refills);
        Assert.Equal(2, lines);
        Assert.Equal(2, movements);
    }

    /// <summary>
    /// Two runs that both fit are both recorded: the write lock serialises them, it does not refuse
    /// one. A picker waiting for another picker's transaction is not an error.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_batches_that_both_fit_are_both_recorded()
    {
        Seed(10);

        var answers = await RaceAsync(
            Batch("first-run", (MachineA, 2), (MachineB, 2)),
            Batch("second-run", (MachineA, 3), (MachineB, 3)));

        Assert.All(answers, answer => Assert.Null(answer.Failure));
        Assert.All(answers, answer => Assert.Equal(PickListRefillOutcome.Recorded, answer.Outcome));

        var (quantityInStock, refills, lines, movements) = Read();
        Assert.Equal(0, quantityInStock);
        Assert.Equal(2, refills);
        Assert.Equal(4, lines);
        Assert.Equal(4, movements);
    }

    /// <summary>
    /// The two endpoints serialise against each other, not only against their own kind: they take the
    /// same write lock over the same shelf, so a batch and a single-machine refill that together
    /// exceed storage leave exactly one of them recorded. An endpoint added beside an existing one
    /// that kept its own lock would be a second check nobody else waits on.
    /// </summary>
    [Fact]
    public async Task A_batch_and_a_single_machine_refill_that_together_exceed_storage_leave_exactly_one()
    {
        Seed(10);

        var answers = await RaceAsync(
            Batch("the-batch", (MachineA, 4), (MachineB, 4)),
            SingleMachineRefill("the-single-refill", 8));

        Assert.All(answers, answer => Assert.Null(answer.Failure));
        Assert.Single(answers.Where(answer => answer.Outcome == PickListRefillOutcome.Recorded));
        Assert.Single(answers.Where(answer => answer.Outcome == PickListRefillOutcome.Rejected));

        var (quantityInStock, refills, _, _) = Read();
        Assert.Equal(2, quantityInStock);
        Assert.Equal(1, refills);
    }

    /// <summary>
    /// A machine taken out of service while a run is being saved: whichever of the two lands first,
    /// the result is consistent. Either the batch committed and the whole run is recorded, or it was
    /// refused and nothing at all was written - never a batch with some of its allocations, and never
    /// a storage quantity that moved without a movement behind it.
    /// </summary>
    [Fact]
    public async Task A_competing_machine_deactivation_never_leaves_a_half_recorded_batch()
    {
        Seed(10);

        var answers = await RaceAsync(
            Batch("the-run", (MachineA, 3), (MachineB, 2)),
            async db =>
            {
                var attempt = await new EfLocalMachineStore(db)
                    .SetActiveAsync(MachineB, isActive: false, CancellationToken.None);
                return new Answer(null, attempt.Refusal, null);
            });

        Assert.All(answers, answer => Assert.Null(answer.Failure));

        var (quantityInStock, refills, lines, movements) = Read();
        if (answers[0].Outcome == PickListRefillOutcome.Recorded)
        {
            Assert.Equal(5, quantityInStock);
            Assert.Equal(1, refills);
            Assert.Equal(2, lines);
            Assert.Equal(2, movements);
        }
        else
        {
            Assert.Equal(PickListRefillOutcome.Rejected, answers[0].Outcome);
            Assert.Equal(PickListRefillPolicy.MachineMustBeActiveMessage, answers[0].Refusal);
            Assert.Equal(10, quantityInStock);
            Assert.Equal(0, refills);
            Assert.Equal(0, lines);
            Assert.Equal(0, movements);
        }
    }
}
