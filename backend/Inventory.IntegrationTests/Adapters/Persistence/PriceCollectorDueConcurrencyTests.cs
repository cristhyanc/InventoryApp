using System.Data;
using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Suppliers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Subscriptions;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Two <c>schedule=due</c> calls under real concurrency (issue #1108).
///
/// <para>This is the test the atomicity requirement exists for. "Is this pair due?" is a
/// check-then-write rule: the pair's latest answer and whether it is already in flight are read, and
/// work is then written from that answer. A rule like that which is not serialised is not a rule -
/// two calls started together both read "nothing pending" and both issue, and the collector searches
/// and <strong>pays for</strong> the same product at the same supplier twice. That is the failure the
/// issue names, and nothing in the schema can refuse it: a second work item for the same pair in a
/// different run is a perfectly legal row.</para>
///
/// <para>It therefore runs against a real SQLite <strong>file</strong> with two independent
/// connections, because that is the only way to have two genuine writers: a shared in-memory
/// database is one connection, and InMemory has no locking at all. The two calls are released
/// together at a barrier, so the window the rule has to close is actually opened. It is the same
/// shape as <see cref="LocalMachineDeletionConcurrencyTests"/> (issue #896) and
/// <see cref="PickListRefillConcurrencyTests"/> (issue #743), for the same reason.</para>
/// </summary>
public class PriceCollectorDueConcurrencyTests : IDisposable
{
    private const int BusinessA = 1;

    /// <summary>
    /// How many due pairs the race is run over.
    ///
    /// <para>It is deliberately not one. With a single pair both calls finish in microseconds and
    /// their critical sections need never overlap at all, so the test would pass against an
    /// unsynchronised implementation and prove nothing. A couple of hundred pairs makes the winner's
    /// transaction long enough that the loser genuinely arrives inside the window - and it is also
    /// the shape the rule has to hold in, because the real catalogue is many products at several
    /// suppliers rather than one.</para>
    /// </summary>
    private const int DuePairCount = 200;

    /// <summary>Wednesday 4 November 2026, 10:00 Sydney.</summary>
    private static readonly DateTime IssuedAt = new(2026, 11, 3, 23, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly Today = new(2026, 11, 4);

    private static readonly DateTime ScheduleEffectiveFrom =
        new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"inventory-collector-due-{Guid.NewGuid():N}.db");

    private readonly string _connectionString;

    private readonly int _supplierPresetId;
    private readonly List<long> _masterProductIds = [];

    public PriceCollectorDueConcurrencyTests()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

        using var db = TestAppDbContext.Unrestricted(Options());
        db.Database.Migrate();

        db.Businesses.Add(new Business
        {
            Id = BusinessA,
            Name = "Vending Co",
            Status = BusinessStatus.Active,
            CreatedAtUtc = ScheduleEffectiveFrom,
        });

        // The only supplier anything collects: every shipped preset's route is cleared, so this test
        // owns the supplier list exactly as the in-memory fixture's own helper arranges it.
        foreach (var shipped in db.SupplierPresets.ToList())
        {
            shipped.PriceConnector = null;
            shipped.PriceConnectorKey = null;
        }

        var preset = new SupplierPreset
        {
            Name = "Alpha Grocer",
            NormalisedName = "ALPHA GROCER",
            IsEnabled = true,
            SearchPrices = true,
            ServesNationwide = true,
            PriceConnector = SupplierPriceConnector.Kmart,
            SearchFrequency = SupplierSearchFrequency.Daily,
            SearchDaysOfWeek = string.Empty,
            ScheduleEffectiveFromUtc = ScheduleEffectiveFrom,
            PriceCheckFreshnessHours = SupplierSearchSchedulePolicy.FreshnessHoursFor(
                SupplierSearchSchedulePolicy.Daily),
            CreatedAtUtc = ScheduleEffectiveFrom,
            UpdatedAtUtc = ScheduleEffectiveFrom,
        };
        db.SupplierPresets.Add(preset);

        var masters = new List<MasterProduct>();

        for (var index = 0; index < DuePairCount; index++)
        {
            var name = $"Variant {index:D4}";

            var master = new MasterProduct
            {
                Brand = "Coca-Cola",
                Name = name,
                SizeValue = 375m,
                SizeUnit = MasterProductSizeUnit.Millilitre,
                NormalisedKey = MasterProductPolicy.NormalisedKey(
                    "Coca-Cola",
                    name,
                    375m,
                    MasterProductSizeUnit.Millilitre),
                CreatedAtUtc = ScheduleEffectiveFrom,
                UpdatedAtUtc = ScheduleEffectiveFrom,
            };

            masters.Add(master);
            db.MasterProducts.Add(master);
            db.Products.Add(new Product
            {
                Id = 100 + index,
                BusinessId = BusinessA,
                Name = $"Coke {index:D4}",
            });
        }

        db.SaveChanges();

        for (var index = 0; index < DuePairCount; index++)
        {
            db.ProductPriceTrackings.Add(new ProductPriceTracking
            {
                BusinessId = BusinessA,
                ProductId = 100 + index,
                MasterProductId = masters[index].Id,
                Status = ProductPriceTrackingStatus.Matched,
                MatchSource = ProductPriceTrackingMatchSource.SuperAdmin,
                WaitingKey = PriceCatalogueWaitingKey.For($"Coke {index:D4}", "375mL"),
                SearchName = $"Coke {index:D4}",
                UnitLabel = "375mL",
                CreatedAtUtc = ScheduleEffectiveFrom,
                UpdatedAtUtc = ScheduleEffectiveFrom,
            });
        }

        db.SaveChanges();

        _supplierPresetId = preset.Id;
        _masterProductIds.AddRange(masters.Select(master => master.Id));
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
    /// Two <c>due</c> calls released together issue every due pair exactly once between them. One of
    /// them gets the run; the other arrives inside its window, finds every pair already in flight,
    /// issues nothing and creates no run at all.
    ///
    /// <para>Neither is allowed to fail either: a lock error would be the race turned into a
    /// <c>500</c> rather than into a decision, which is the other half of what the write lock is for.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_due_calls_started_together_never_issue_the_same_pair_twice()
    {
        var answers = await RaceAsync(IssueDueAsync, IssueDueAsync);

        Assert.All(answers, answer => Assert.Null(answer.Failure));

        var issued = answers
            .Select(answer => answer.Answer!)
            .ToList();

        // One of the two issued a run; the other published no work at all.
        Assert.Single(issued.Where(workList => workList.RunId is not null));
        Assert.Empty(issued
            .Where(workList => workList.RunId is null)
            .SelectMany(workList => workList.MatchedProducts));

        await using var db = TestAppDbContext.Unrestricted(Options());

        var workItems = await db.PriceCollectorWorkItems.AsNoTracking().ToListAsync();

        // Every pair exactly once, which is the claim: no pair searched - and paid for - twice.
        Assert.Equal(DuePairCount, workItems.Count);
        Assert.Equal(
            _masterProductIds.Order(),
            workItems.Select(item => item.MasterProductId!.Value).Order());
        Assert.All(workItems, item => Assert.Equal(_supplierPresetId, item.SupplierPresetId));

        // And exactly one run exists, still open: the loser wrote nothing, so it neither issued work
        // nor expired the winner's run out from under it.
        var run = Assert.Single(await db.PriceCollectorRuns.AsNoTracking().ToListAsync());
        Assert.All(workItems, item => Assert.Equal(run.Id, item.PriceCollectorRunId));
        Assert.Equal(PriceCollectorRunStatus.Running, run.Status);
    }

    /// <summary>
    /// <strong>The authoritative read happens after the write lock is held</strong>, which is the
    /// guarantee the two calls above rely on and the one a barrier cannot prove: two calls released
    /// together each finish in milliseconds, so on most runs the second never arrives inside the
    /// first's window at all and the assertion would hold against an implementation with no
    /// synchronisation whatsoever.
    ///
    /// <para>So this test opens the window itself and changes the state inside it. It takes SQLite's
    /// write lock, starts one <c>due</c> call - which cannot have read anything yet, because the lock
    /// it needs is held here - then issues every pair from <em>inside</em> that lock and commits. A
    /// call that re-reads under the lock sees them in flight and issues nothing; a call that had
    /// already decided from a read taken before the lock would issue all of them a second time, which
    /// is the double search and the double charge this design exists to prevent.</para>
    /// </summary>
    [Fact]
    public async Task A_due_call_reads_each_pairs_state_after_it_holds_the_write_lock()
    {
        await using var blocking = new SqliteConnection(_connectionString);
        await blocking.OpenAsync();

        // deferred: false is BEGIN IMMEDIATE - SQLite's write lock, held by this test.
        var held = blocking.BeginTransaction(IsolationLevel.Serializable, deferred: false);

        var issuing = Task.Run(IssueDueAsync);

        // It is waiting for the lock rather than deciding anything: a call that had already read the
        // pairs' state would have finished by now.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(
            issuing.IsCompleted,
            "The due call completed while the write lock was held elsewhere, so its check and its "
                + "issue are not one operation.");

        // Another writer issues every pair inside the window and commits.
        var runId = await IssueEveryPairAsync(blocking, held);
        await held.CommitAsync();
        await held.DisposeAsync();

        var workList = await issuing;

        // It re-read under the lock, found every pair in flight, and issued nothing at all.
        Assert.Null(workList.RunId);
        Assert.Empty(workList.MatchedProducts);

        await using var db = TestAppDbContext.Unrestricted(Options());

        var run = Assert.Single(await db.PriceCollectorRuns.AsNoTracking().ToListAsync());
        Assert.Equal(runId, run.Id);
        Assert.Equal(
            DuePairCount,
            await db.PriceCollectorWorkItems.AsNoTracking().CountAsync());
    }

    /// <summary>
    /// And with every pair already answered, both calls issue nothing and neither creates a run - so
    /// two scheduled invocations that overlap on a day with nothing to do leave nothing behind for
    /// anybody to finalise.
    /// </summary>
    [Fact]
    public async Task Two_due_calls_with_nothing_due_both_create_no_run()
    {
        await AnswerEveryPairAsync();

        var answers = await RaceAsync(IssueDueAsync, IssueDueAsync);

        Assert.All(answers, answer => Assert.Null(answer.Failure));
        Assert.All(answers, answer => Assert.Null(answer.Answer!.RunId));

        await using var db = TestAppDbContext.Unrestricted(Options());

        // Only the answered run this setup wrote, and only its own work items.
        Assert.Single(await db.PriceCollectorRuns.AsNoTracking().ToListAsync());
        Assert.Equal(
            DuePairCount,
            await db.PriceCollectorWorkItems.AsNoTracking().CountAsync());
    }

    /// <summary>
    /// One open, unexpired run with a pending work item for every pair, written with SQL on the
    /// caller's own connection and transaction so it happens <em>inside</em> the window that
    /// transaction holds. It answers the run's key.
    /// </summary>
    private async Task<long> IssueEveryPairAsync(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        await using var insertRun = connection.CreateCommand();
        insertRun.Transaction = transaction;
        insertRun.CommandText =
            "INSERT INTO PriceCollectorRuns "
                + "(StartedAtUtc, CompletedAtUtc, ExpiresAtUtc, Status, ListingsCheckedCount, "
                + " ObservationsRecordedCount, FailureCount, CompleteSearchCount, "
                + " TruncatedSearchCount, NoMatchesSearchCount, FailedSearchCount, NotSearchedCount) "
                + "VALUES ('2026-11-03 22:30:00', NULL, '2026-11-04 05:00:00', 1, 0, 0, 0, 0, 0, 0, 0, 0); "
                + "SELECT last_insert_rowid();";

        var runId = Convert.ToInt64(
            await insertRun.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);

        foreach (var masterProductId in _masterProductIds)
        {
            await using var insertWork = connection.CreateCommand();
            insertWork.Transaction = transaction;
            insertWork.CommandText =
                "INSERT INTO PriceCollectorWorkItems "
                    + "(PriceCollectorRunId, SupplierPresetId, MasterProductId, WaitingKey, "
                    + " IssuedAtUtc, Outcome, OutcomeAtUtc, FailureReason, SubmittedRowCount) "
                    + $"VALUES ({runId}, {_supplierPresetId}, {masterProductId}, NULL, "
                    + "'2026-11-03 22:30:00', NULL, NULL, NULL, NULL);";

            await insertWork.ExecuteNonQueryAsync();
        }

        return runId;
    }

    /// <summary>
    /// Every pair answered in a closed run, which is the state "these pairs are covered for today".
    /// </summary>
    private async Task AnswerEveryPairAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(Options());

        var run = new PriceCollectorRun
        {
            StartedAtUtc = IssuedAt.AddHours(-1),
            CompletedAtUtc = IssuedAt.AddMinutes(-30),
            ExpiresAtUtc = IssuedAt.AddHours(5),
            Status = PriceCollectorRunStatus.Succeeded,
        };
        db.PriceCollectorRuns.Add(run);
        await db.SaveChangesAsync();

        foreach (var masterProductId in _masterProductIds)
        {
            db.PriceCollectorWorkItems.Add(new PriceCollectorWorkItem
            {
                PriceCollectorRunId = run.Id,
                SupplierPresetId = _supplierPresetId,
                MasterProductId = masterProductId,
                IssuedAtUtc = IssuedAt.AddHours(-1),
                Outcome = PriceCollectorWorkItemOutcome.Complete,
                OutcomeAtUtc = IssuedAt.AddMinutes(-45),
                SubmittedRowCount = 1,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// One <c>due</c> call on its own context and its own connection, which is what makes the two
    /// genuine writers.
    /// </summary>
    private async Task<PriceCollectorWorkList> IssueDueAsync()
    {
        await using var db = TestAppDbContext.Denied(Options());

        return await new IssuePriceCollectorWorkList(
                new EfPriceCatalogueWorkListStore(
                    db,
                    FakeSubscriptionEnforcementFlag.Disabled,
                    new EfSubscriptionGrandfatheringStore(db),
                    new FakeClock(IssuedAt)),
                new EfPriceCollectorRunStore(db),
                new FixedRunSettings(PriceCollectorRunPolicy.DefaultExpiryHours),
                new FakeClock(IssuedAt),
                new FakeSupplierSearchScheduleCalendar(Today))
            .Handle(PriceCollectorWorkSchedule.Due, CancellationToken.None);
    }

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options;

    /// <summary>Runs the given operations released at the same instant and returns what each answered.</summary>
    private static async Task<(T? Answer, Exception? Failure)[]> RaceAsync<T>(
        params Func<Task<T>>[] operations)
    {
        using var barrier = new Barrier(operations.Length);

        var answers = operations
            .Select(operation => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                try
                {
                    return (await operation(), (Exception?)null);
                }
                catch (Exception exception)
                {
                    return ((T?)default, exception);
                }
            }))
            .ToArray();

        return await Task.WhenAll(answers);
    }

    private sealed class FixedRunSettings(int runExpiryHours) : IPriceCollectorRunSettings
    {
        public int RunExpiryHours { get; } = runExpiryHours;
    }
}
