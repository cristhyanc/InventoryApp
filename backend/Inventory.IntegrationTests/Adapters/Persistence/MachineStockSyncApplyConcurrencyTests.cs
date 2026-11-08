using System.Data.Common;
using Inventory.Application.Machines;
using Inventory.Application.MachineStockSync;
using Inventory.Domain.Nayax;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Two requests applying the same Sync Restock event at the same time (issue #1127).
///
/// <para>This is the race the idempotency rule exists for. "Has this event already been applied?"
/// and "does storage hold what it reports?" are check-then-write rules, and a check-then-write rule
/// that is not serialised is not a rule: two requests both read the same <c>Unprocessed</c> event and
/// the same storage quantity, and both record a <c>MachineRefill</c> - so one physical restock
/// deducts storage twice and the event ends up naming only the second movement, with the first left
/// in the ledger as stock nobody took. So each product's batch takes
/// <c>SqliteWriteLockTransaction</c> (<c>BEGIN IMMEDIATE</c>) <em>before</em> it reads the events and
/// the product, and holds it through its commit, exactly as issue #743's pick-list refill and issue
/// #976's document attach do.</para>
///
/// <para><strong>The window is opened deliberately rather than hoped for.</strong> A barrier race
/// releases two writers together, but it cannot land one of them inside the other's
/// check-then-write gap on demand, and a regression test that only sometimes reaches the defect is
/// not a regression test. So the deterministic tests drive the competing request from a
/// <see cref="DbCommandInterceptor"/> at the first statement of the winner's locked read of the
/// product - the storage quantity its write is decided on - on its own, deliberately impatient
/// connection, so "it cannot even begin" is answered in a second rather than after SQLite's default
/// thirty. In production that second request waits and is then answered with the committed
/// <c>Already applied.</c>, which the barrier races below cover.</para>
///
/// <para>Everything runs against a real SQLite <strong>file</strong> with one connection per writer,
/// because that is the only way to have two genuine writers: a shared in-memory database is one
/// connection, and the EF InMemory provider has no locking at all.</para>
/// </summary>
public sealed class MachineStockSyncApplyConcurrencyTests : IDisposable
{
    private const int BusinessA = 1;

    /// <summary>The machine's Nayax number: what the provider's events carry.</summary>
    private const long MachineId = 900;

    /// <summary>The same machine's local id (issue #741): what the movements record.</summary>
    private const long LocalMachineId = 31;

    private const long ProductId = 200;
    private const int RefillQuantity = 4;
    private const int OpeningStock = 20;

    private static readonly MachineIdentity Machine = new(LocalMachineId, MachineId);
    private static readonly DateTime EventTime = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"inventory-sync-restock-apply-{Guid.NewGuid():N}.db");

    private readonly string _connectionString;
    private readonly string _impatientConnectionString;

    public MachineStockSyncApplyConcurrencyTests()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();
        _impatientConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            // One second, not the ADO.NET "0 means wait forever" and not SQLite's default thirty.
            DefaultTimeout = 1,
        }.ToString();

        using var db = TestAppDbContext.Unrestricted(Options());
        db.Database.Migrate();
        db.Businesses.Add(new Business
        {
            Id = BusinessA,
            Name = "Vending Co",
            Status = BusinessStatus.Active,
            SalesProvider = ExternalProvider.Nayax,
            CreatedAtUtc = EventTime,
        });
        db.Machines.Add(new LocalMachine
        {
            BusinessId = BusinessA,
            Id = LocalMachineId,
            Name = "Machine 900",
            ExternalMachineId = NayaxExternalId.From(MachineId),
        });
        db.Products.Add(new Product
        {
            Id = ProductId,
            BusinessId = BusinessA,
            ExternalProductId = NayaxExternalId.From(ProductId),
            Name = "Coke 375mL",
            Unit = "can",
            QuantityInStock = OpeningStock,
            CostingQuantity = OpeningStock,
            InventoryValue = OpeningStock * 1m,
            AverageUnitCost = 1m,
        });
        db.StockAdjustments.Add(new StockAdjustment
        {
            BusinessId = BusinessA,
            ProductId = ProductId,
            QuantityChange = OpeningStock,
            QuantityAfter = OpeningStock,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = 1m,
            EffectiveAt = EventTime.AddDays(-2),
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

    /// <summary>
    /// A second apply landing inside the first one's read-then-write gap. It cannot: the first holds
    /// SQLite's write lock from before it reads the event and the product until its commit, so the
    /// second cannot even begin, and exactly one movement is stored. Without the lock the second
    /// one's movement commits here, deducting the same physical restock twice.
    /// </summary>
    [Fact]
    public async Task A_second_apply_cannot_commit_inside_the_first_ones_read_and_write()
    {
        var eventId = await SeedUnprocessedEventAsync();
        var competing = new CompetesWhileTheFirstOneReadsTheProduct(
            () => ApplyAsync(ImpatientOptions(), eventId));

        await using (var db = TestAppDbContext.For(OptionsWith(competing), BusinessA))
        {
            var response = await new ApplyMachineStockSync(Store(db))
                .Handle(Machine, [eventId], CancellationToken.None);

            Assert.Equal(NayaxStockEventApplyOutcome.Applied, response.Results[0].Outcome);
        }

        Assert.True(competing.Ran, "The competing apply was never attempted, so nothing was proved.");
        Assert.False(competing.Applied, "A second apply recorded a movement inside the first one's transaction.");
        Assert.IsType<SqliteException>(competing.Failure);

        await AssertAppliedExactlyOnceAsync(
            eventId, NayaxDuplicateResolution.None, OpeningStock - RefillQuantity);
    }

    /// <summary>
    /// An explicit "Apply as separate restock" override holds the same lock across the same gap, so
    /// an ordinary apply of the same event cannot commit inside it either (issue #196's override is
    /// the one duplicate resolution that writes).
    /// </summary>
    [Fact]
    public async Task An_apply_cannot_commit_inside_the_duplicate_overrides_read_and_write()
    {
        var eventId = await SeedUnprocessedEventAsync();
        await SeedMatchingManualRefillAsync();
        var competing = new CompetesWhileTheFirstOneReadsTheProduct(
            () => ApplyAsync(ImpatientOptions(), eventId));

        await using (var db = TestAppDbContext.For(OptionsWith(competing), BusinessA))
        {
            var result = await new ResolveMachineStockDuplicate(Store(db)).Handle(
                Machine, eventId, NayaxDuplicateResolutionChoice.ApplyAsSeparateRestock, CancellationToken.None);

            Assert.Equal(NayaxStockEventApplyOutcome.Applied, result.Outcome);
        }

        Assert.True(competing.Ran, "The competing apply was never attempted, so nothing was proved.");
        Assert.False(competing.Applied, "An apply recorded a movement inside the override's transaction.");
        Assert.IsType<SqliteException>(competing.Failure);

        // Both refills are in the product's replayed history here: the manual one the override
        // declared separate, and the Nayax one it applied.
        await AssertAppliedExactlyOnceAsync(
            eventId, NayaxDuplicateResolution.AppliedAsSeparateRestock, OpeningStock - (2 * RefillQuantity));
    }

    /// <summary>
    /// The same pair as two genuine simultaneous requests. What this adds is the other failure mode:
    /// neither writer may answer with a provider lock error instead of a decision - the loser waits,
    /// re-reads the winner's committed state and reports the established idempotent
    /// <c>Already applied.</c> with the winner's movement id.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_applies_of_one_event_record_exactly_one_movement()
    {
        var eventId = await SeedUnprocessedEventAsync();

        var (first, second) = await RaceAsync(
            () => ApplyAsync(Options(), eventId),
            () => ApplyAsync(Options(), eventId));

        Assert.Null(first.Failure);
        Assert.Null(second.Failure);
        Assert.Equal(NayaxStockEventApplyOutcome.Applied, first.Result!.Outcome);
        Assert.Equal(NayaxStockEventApplyOutcome.Applied, second.Result!.Outcome);
        Assert.Single(new[] { first.Result.Message, second.Result.Message }, "Already applied.");

        var movementId = await AssertAppliedExactlyOnceAsync(
            eventId, NayaxDuplicateResolution.None, OpeningStock - RefillQuantity);

        // Both requests report the one movement that was recorded, never a second id and never none.
        Assert.Equal(movementId, first.Result.StockAdjustmentId);
        Assert.Equal(movementId, second.Result.StockAdjustmentId);
    }

    /// <summary>
    /// Two "Apply as separate restock" overrides of one flagged event at the same time - the
    /// double-clicked dialog action. Exactly one movement, one decrement, and the loser reports the
    /// winner's result rather than overriding the warning a second time.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_duplicate_overrides_of_one_event_record_exactly_one_movement()
    {
        var eventId = await SeedUnprocessedEventAsync();
        await SeedMatchingManualRefillAsync();

        var (first, second) = await RaceAsync(
            () => OverrideAsSeparateRestockAsync(Options(), eventId),
            () => OverrideAsSeparateRestockAsync(Options(), eventId));

        Assert.Null(first.Failure);
        Assert.Null(second.Failure);
        Assert.Equal(NayaxStockEventApplyOutcome.Applied, first.Result!.Outcome);
        Assert.Equal(NayaxStockEventApplyOutcome.Applied, second.Result!.Outcome);

        var movementId = await AssertAppliedExactlyOnceAsync(
            eventId, NayaxDuplicateResolution.AppliedAsSeparateRestock, OpeningStock - (2 * RefillQuantity));
        Assert.Equal(movementId, first.Result.StockAdjustmentId);
        Assert.Equal(movementId, second.Result.StockAdjustmentId);
    }

    /// <summary>
    /// Exactly one Nayax movement for the event, storage left at <paramref name="expectedStorage"/>
    /// - the one deduction this event may make, never two - and the event naming that one movement
    /// with the expected duplicate resolution. Returns the movement's id.
    /// </summary>
    private async Task<int> AssertAppliedExactlyOnceAsync(
        int eventId, NayaxDuplicateResolution resolution, int expectedStorage)
    {
        await using var verify = TestAppDbContext.Unrestricted(Options());

        var movement = Assert.Single(await verify.StockAdjustments.AsNoTracking()
            .Where(x => x.ProductId == ProductId
                && x.Reason == StockAdjustmentReason.MachineRefill
                && x.Source == StockAdjustmentSource.Nayax)
            .ToListAsync());
        Assert.Equal(-RefillQuantity, movement.QuantityChange);

        Assert.Equal(
            expectedStorage,
            (await verify.Products.AsNoTracking().SingleAsync(p => p.Id == ProductId)).QuantityInStock);

        var stored = await verify.NayaxMachineStockEvents.AsNoTracking().SingleAsync(e => e.Id == eventId);
        Assert.Equal(NayaxStockEventProcessingStatus.Applied, stored.ProcessingStatus);
        Assert.Equal(movement.Id, stored.StockAdjustmentId);
        Assert.Equal(resolution, stored.DuplicateResolution);

        return movement.Id;
    }

    private async Task<int> SeedUnprocessedEventAsync()
    {
        await using var db = TestAppDbContext.For(Options(), BusinessA);
        var stored = new NayaxMachineStockEvent
        {
            NayaxEventLogId = 5001,
            MachineId = MachineId,
            EventCode = NayaxMachineAlertEventCodes.StockAdjustForMachine,
            EventDateTimeGmt = EventTime,
            EventDateTimeVmc = EventTime.AddHours(10),
            RawEventData = $"Product MDB: 7 | Coke 375mL | {RefillQuantity}",
            ParsedMdb = 7,
            ParsedProductName = "Coke 375mL",
            ParsedQuantity = RefillQuantity,
            MatchedProductId = ProductId,
            MatchStatus = NayaxStockEventMatchStatus.Matched,
        };
        db.NayaxMachineStockEvents.Add(stored);
        await db.SaveChangesAsync();
        return stored.Id;
    }

    /// <summary>
    /// A manual refill of the same product, quantity and hour, which is what makes the event a
    /// flagged possible duplicate the explicit override may resolve (issue #196). It is seeded as a
    /// movement row rather than applied, because what it changes about storage is not this test's
    /// subject: the one deduction the Nayax event makes is.
    /// </summary>
    private async Task SeedMatchingManualRefillAsync()
    {
        await using var db = TestAppDbContext.For(Options(), BusinessA);
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = ProductId,
            QuantityChange = -RefillQuantity,
            QuantityAfter = OpeningStock,
            Reason = StockAdjustmentReason.MachineRefill,
            Source = StockAdjustmentSource.Manual,
            MachineId = LocalMachineId,
            EffectiveAt = EventTime,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<ApplyAnswer> ApplyAsync(DbContextOptions<AppDbContext> options, int eventId)
    {
        await using var db = TestAppDbContext.For(options, BusinessA);
        try
        {
            var response = await new ApplyMachineStockSync(Store(db))
                .Handle(Machine, [eventId], CancellationToken.None);
            return new ApplyAnswer(response.Results[0], null);
        }
        catch (Exception failure)
        {
            return new ApplyAnswer(null, failure);
        }
    }

    private static async Task<ApplyAnswer> OverrideAsSeparateRestockAsync(
        DbContextOptions<AppDbContext> options, int eventId)
    {
        await using var db = TestAppDbContext.For(options, BusinessA);
        try
        {
            var result = await new ResolveMachineStockDuplicate(Store(db)).Handle(
                Machine, eventId, NayaxDuplicateResolutionChoice.ApplyAsSeparateRestock, CancellationToken.None);
            return new ApplyAnswer(result, null);
        }
        catch (Exception failure)
        {
            return new ApplyAnswer(null, failure);
        }
    }

    private static EfMachineStockEventStore Store(AppDbContext db) =>
        new(db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db));

    /// <summary>Releases both requests at the same instant and returns what each of them answered.</summary>
    private static async Task<(ApplyAnswer First, ApplyAnswer Second)> RaceAsync(
        Func<Task<ApplyAnswer>> first, Func<Task<ApplyAnswer>> second)
    {
        using var barrier = new Barrier(2);

        var answers = new[] { first, second }
            .Select(operation => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await operation();
            }))
            .ToArray();

        var resolved = await Task.WhenAll(answers);
        return (resolved[0], resolved[1]);
    }

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options;

    private DbContextOptions<AppDbContext> ImpatientOptions() =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_impatientConnectionString).Options;

    private DbContextOptions<AppDbContext> OptionsWith(IInterceptor interceptor) =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connectionString)
            .AddInterceptors(interceptor)
            .Options;

    /// <summary>What one request answered: its per-event result, or the exception it failed with.</summary>
    private sealed record ApplyAnswer(NayaxStockEventApplyResultDto? Result, Exception? Failure);

    /// <summary>
    /// Runs a competing apply at the first read of the product the first request issues - the
    /// storage quantity its write is decided on, which happens inside its write-locked transaction.
    /// That is the instant an unserialised implementation was exposed at: with no lock held before
    /// that read, a second apply could read the same unprocessed event and the same quantity and
    /// commit its own movement first.
    /// </summary>
    private sealed class CompetesWhileTheFirstOneReadsTheProduct(Func<Task<ApplyAnswer>> competing)
        : DbCommandInterceptor
    {
        private const string ReadTable = @"""Products""";

        private bool _attempted;

        /// <summary>Whether the window was reached at all; a test that never reaches it proves nothing.</summary>
        public bool Ran { get; private set; }

        public bool Applied { get; private set; }

        public Exception? Failure { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            Compete(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Compete(string? commandText)
        {
            if (_attempted || commandText?.Contains(ReadTable, StringComparison.Ordinal) != true) return;

            _attempted = true;
            Ran = true;
            try
            {
                // Blocking is deliberate: the competing request has to be resolved inside this
                // window, and it runs on its own connection, so nothing about the first request's
                // own connection is waiting on it. `Task.Run` is what keeps blocking safe - it takes
                // the competing apply off the test runner's limited synchronization context, which
                // this thread is sitting on while it waits.
                var answer = Task.Run(competing).GetAwaiter().GetResult();
                if (answer.Failure is { } failure) Failure = failure;
                else Applied = answer.Result?.Outcome == NayaxStockEventApplyOutcome.Applied;
            }
            catch (Exception failure)
            {
                Failure = failure;
            }
        }
    }
}
