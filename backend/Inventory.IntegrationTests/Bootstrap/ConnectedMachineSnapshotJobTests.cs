using Inventory.Application.ConnectedMachines;
using Inventory.Domain.ConnectedMachines;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using InventoryApi.Bootstrap;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Bootstrap;

/// <summary>
/// The daily connected-machine snapshot job of issue #978 - the data half of
/// <c>snapshot-connected-machines</c> - against a real relational database.
///
/// <para>What this file has to show is the job's own four properties, because every per-business
/// decision belongs to the use case it invokes and is tested there: it considers the active
/// businesses in a stable order, it is bounded and says so when it stops at its bound, one business's
/// failure is one business's failure, and it refuses to run at all against a schema that is not at
/// the expected shape.</para>
/// </summary>
public class ConnectedMachineSnapshotJobTests : IAsyncLifetime
{
    private static readonly DateTime Created = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DbContextOptions<AppDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        await using var db = TestAppDbContext.Unrestricted(_options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    /// <summary>
    /// Every active business is considered, in business-id order, and each one's outcome is reported
    /// as what it was: a snapshot recorded, a business skipped before any call, or a business already
    /// counted today.
    /// </summary>
    [Fact]
    public async Task Considers_every_active_business_in_order_and_reports_each_outcome()
    {
        var first = await AddBusinessAsync("Vending Co");
        var second = await AddBusinessAsync("Second Vending Co");
        var third = await AddBusinessAsync("Third Vending Co");

        var recorders = new Dictionary<int, StubRecorder>
        {
            [first] = StubRecorder.Recorded(),
            [second] = StubRecorder.Skipped(ConnectedMachineSnapshotOutcome.PlanDoesNotCount),
            [third] = StubRecorder.Skipped(ConnectedMachineSnapshotOutcome.AlreadyRecorded),
        };

        var report = await RunAsync(recorders);

        Assert.True(report.Succeeded);
        Assert.Equal([first, second, third], report.Businesses.Select(entry => entry.BusinessId));
        Assert.Equal(1, report.Recorded);
        Assert.Equal(2, report.Skipped);
        Assert.Equal(0, report.Failures);

        // Only the counted business cost a Nayax call; the two skipped ones were decided without one.
        Assert.Equal(1, report.FleetReads);
        Assert.False(report.CapReached);
    }

    /// <summary>
    /// A pending application has nothing to count and a deactivated business is not trading, so
    /// neither is considered at all - their existing snapshots stay exactly as they are and their
    /// credentials are not spent.
    /// </summary>
    [Fact]
    public async Task Only_active_businesses_are_considered()
    {
        var active = await AddBusinessAsync("Vending Co");
        await AddBusinessAsync("Coastal Vending", BusinessStatus.Pending);
        await AddBusinessAsync("Offboarded Vending", BusinessStatus.Deactivated);
        await AddBusinessAsync("Inland Vending", BusinessStatus.Rejected);

        var report = await RunAsync(new Dictionary<int, StubRecorder>
        {
            [active] = StubRecorder.Recorded(),
        });

        Assert.Equal(active, Assert.Single(report.Businesses).BusinessId);
    }

    /// <summary>
    /// One business's failure is one business's failure: it is reported, nothing is stored for it, and
    /// the run carries on to the next business rather than abandoning the rest of the fleet.
    /// </summary>
    [Fact]
    public async Task One_businesss_failure_does_not_stop_the_run()
    {
        var first = await AddBusinessAsync("Vending Co");
        var second = await AddBusinessAsync("Second Vending Co");
        var third = await AddBusinessAsync("Third Vending Co");

        var log = new List<string>();
        var report = await RunAsync(
            new Dictionary<int, StubRecorder>
            {
                [first] = StubRecorder.Recorded(),
                [second] = StubRecorder.Failing(new TimeoutException("Nayax is away.")),
                [third] = StubRecorder.Recorded(),
            },
            log);

        Assert.True(report.Succeeded);
        Assert.Equal(2, report.Recorded);
        Assert.Equal(1, report.Failures);
        Assert.Equal(3, report.FleetReads);

        var failed = Assert.Single(report.Businesses, entry => entry.Failed);
        Assert.Equal(second, failed.BusinessId);
        Assert.Null(failed.Outcome);

        // The failure is logged with the business and the reason, and with none of its data.
        var line = Assert.Single(log, entry => entry.Contains("failed for business", StringComparison.Ordinal));
        Assert.Contains(nameof(TimeoutException), line, StringComparison.Ordinal);
        Assert.DoesNotContain("Nayax is away", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The run is bounded: it stops at its cap of fleet reads, reports that it did, and logs it -
    /// because a run nobody watched must not be able to hit its bound silently. The businesses it did
    /// not reach are simply not in the report, and they keep their last complete snapshot.
    /// </summary>
    [Fact]
    public async Task The_run_stops_at_its_cap_and_says_so()
    {
        var recorders = new Dictionary<int, StubRecorder>();

        for (var index = 0; index <= ConnectedMachineSnapshotRunLimits.MaxFleetReadsPerRun; index++)
        {
            recorders[await AddBusinessAsync($"Vending Co {index}")] = StubRecorder.Recorded();
        }

        var log = new List<string>();
        var report = await RunAsync(recorders, log);

        Assert.True(report.CapReached);
        Assert.Equal(ConnectedMachineSnapshotRunLimits.MaxFleetReadsPerRun, report.FleetReads);
        Assert.Equal(ConnectedMachineSnapshotRunLimits.MaxFleetReadsPerRun, report.Businesses.Count);
        Assert.Contains(log, entry => entry.Contains("stopped at its cap", StringComparison.Ordinal));

        // And the business beyond the cap was never asked, so no credential of its was spent.
        var unreached = recorders.Values.Count(recorder => recorder.Attempts == 0);
        Assert.Equal(1, unreached);
    }

    /// <summary>
    /// A schema with pending migrations may not have the snapshot table, or may not have it at the
    /// expected shape, so the run refuses before it reads or writes anything at all.
    /// </summary>
    [Fact]
    public async Task A_schema_with_pending_migrations_refuses_the_run()
    {
        await using var pendingConnection = new SqliteConnection("Data Source=:memory:");
        await pendingConnection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(pendingConnection).Options;

        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.GetService<IMigrator>().MigrateAsync("20261029120000_AllowPurchaseWithoutDocument");

        await using var tenancyDb = TestAppDbContext.Denied(options);
        var report = await new ConnectedMachineSnapshotJob(
                tenancyDb,
                _ => throw new InvalidOperationException("No business may be read at all."),
                _ => { })
            .RunAsync(CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.NotNull(report.Refusal);
        Assert.Empty(report.Businesses);
        Assert.Equal(0, report.FleetReads);
    }

    private async Task<ConnectedMachineSnapshotJobReport> RunAsync(
        Dictionary<int, StubRecorder> recorders,
        List<string>? log = null)
    {
        await using var tenancyDb = TestAppDbContext.Denied(_options);

        return await new ConnectedMachineSnapshotJob(
                tenancyDb,
                businessId => recorders[businessId],
                line => log?.Add(line))
            .RunAsync(CancellationToken.None);
    }

    private async Task<int> AddBusinessAsync(string name, BusinessStatus status = BusinessStatus.Active)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);

        var business = new Business
        {
            Name = name,
            TimeZoneId = "Australia/Sydney",
            Status = status,
            CreatedAtUtc = Created,
        };

        db.Businesses.Add(business);
        await db.SaveChangesAsync();

        return business.Id;
    }

    /// <summary>
    /// One business's recorder, as the job sees it: what the attempt answered, or how it failed.
    /// Everything the real use case decides - the plan, the connection, the day's claim and the
    /// Nayax call - is its own and is tested in <c>RecordConnectedMachineSnapshotTests</c>.
    /// </summary>
    private sealed class StubRecorder : IConnectedMachineSnapshotRecorder
    {
        private readonly ConnectedMachineSnapshotAttempt? _attempt;
        private readonly Exception? _failure;

        private StubRecorder(ConnectedMachineSnapshotAttempt? attempt, Exception? failure)
        {
            _attempt = attempt;
            _failure = failure;
        }

        public int Attempts { get; private set; }

        public static StubRecorder Recorded() =>
            new(
                new ConnectedMachineSnapshotAttempt(
                    ConnectedMachineSnapshotOutcome.Recorded,
                    FleetWasRead: true),
                failure: null);

        public static StubRecorder Skipped(ConnectedMachineSnapshotOutcome outcome) =>
            new(new ConnectedMachineSnapshotAttempt(outcome, FleetWasRead: false), failure: null);

        public static StubRecorder Failing(Exception failure) => new(attempt: null, failure);

        public Task<ConnectedMachineSnapshotOutcome> RecordFromFleetReadAsync(
            ConnectedMachineFleetRead read,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The job never records from somebody else's read.");

        public Task<ConnectedMachineSnapshotAttempt> ReadFleetAndRecordAsync(
            CancellationToken cancellationToken)
        {
            Attempts++;

            return _failure is not null
                ? throw _failure
                : Task.FromResult(_attempt!);
        }
    }
}
