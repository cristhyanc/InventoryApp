using Inventory.Application.ConnectedMachines;
using Inventory.Domain.ConnectedMachines;
using Inventory.Domain.Nayax;
using Inventory.Domain.Subscriptions;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Subscriptions;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.ConnectedMachines;

/// <summary>
/// The one writer of a connected-machine snapshot (issue #978), without EF Core or a web host.
///
/// <para>The rules themselves belong to the Domain and are tested there. What this file has to show
/// is the use case's own properties: that it takes no business, date, series or count from a caller;
/// that every skip is decided <em>before</em> the Nayax call, so a business that is not counted costs
/// no call; that an incomplete read stores nothing; and that losing the daily claim is discarded
/// without an error and without a retry.</para>
/// </summary>
public class RecordConnectedMachineSnapshotTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 6, 30, 0, DateTimeKind.Utc);

    private static readonly DateTime Today = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A complete fleet read the application performed anyway becomes the day's snapshot - the
    /// refresh rule of #978 - and what is stored is derived entirely from the read, the clock and the
    /// business's own connection.
    /// </summary>
    [Fact]
    public async Task Records_the_days_snapshot_from_a_complete_fleet_read()
    {
        var snapshots = new FakeConnectedMachineSnapshotStore();

        var outcome = await UseCase(snapshots: snapshots, accountSeries: 4)
            .RecordFromFleetReadAsync(Read(103, 101, 102), CancellationToken.None);

        Assert.Equal(ConnectedMachineSnapshotOutcome.Recorded, outcome);

        var stored = Assert.Single(snapshots.Snapshots);
        Assert.Equal(Today, stored.DateUtc);
        Assert.Equal(4, stored.AccountSeries);
        Assert.Equal(3, stored.MachineCount);
        Assert.Equal(new long[] { 101, 102, 103 }, stored.ProviderMachineIds);
        Assert.Equal(Now, stored.TakenAtUtc);
        Assert.True(stored.ReadWasComplete);
    }

    /// <summary>
    /// An incomplete read stores nothing and reads nothing at all: a fleet nobody finished reading is
    /// not evidence, so the connection, the day's claim and the plan are never even consulted.
    /// </summary>
    [Fact]
    public async Task An_incomplete_read_stores_nothing()
    {
        var snapshots = new FakeConnectedMachineSnapshotStore();
        var connections = Connection(NayaxConnectionStatus.Ready);

        var outcome = await UseCase(snapshots: snapshots, connections: connections)
            .RecordFromFleetReadAsync(
                ConnectedMachineFleetRead.Incomplete([101, 102]),
                CancellationToken.None);

        Assert.Equal(ConnectedMachineSnapshotOutcome.IncompleteRead, outcome);
        Assert.Empty(snapshots.Snapshots);
        Assert.Equal(0, snapshots.ExistenceChecks);
        Assert.Equal(0, connections.ConnectionReads);
    }

    /// <summary>
    /// A business with no stored credentials, and one whose credentials need attention, record
    /// nothing: there is no account this application may count.
    /// </summary>
    [Theory]
    [InlineData(NayaxConnectionStatus.NeedsAttention)]
    [InlineData(null)]
    public async Task A_business_with_no_usable_connection_records_nothing(NayaxConnectionStatus? status)
    {
        var snapshots = new FakeConnectedMachineSnapshotStore();
        var connections = status is { } stored ? Connection(stored) : new FakeNayaxConnectionStore();

        var outcome = await UseCase(snapshots: snapshots, connections: connections)
            .RecordFromFleetReadAsync(Read(101), CancellationToken.None);

        Assert.Equal(ConnectedMachineSnapshotOutcome.ConnectionUnavailable, outcome);
        Assert.Empty(snapshots.Snapshots);
    }

    /// <summary>
    /// A business whose plan does not have connected machines counted records nothing - and nothing
    /// is withheld from it either.
    /// </summary>
    [Fact]
    public async Task A_business_whose_plan_is_not_counted_records_nothing()
    {
        var snapshots = new FakeConnectedMachineSnapshotStore();

        var outcome = await UseCase(snapshots: snapshots, plan: SubscriptionPlan.Starter)
            .RecordFromFleetReadAsync(Read(101), CancellationToken.None);

        Assert.Equal(ConnectedMachineSnapshotOutcome.PlanDoesNotCount, outcome);
        Assert.Empty(snapshots.Snapshots);
    }

    /// <summary>
    /// A day already claimed for the business's current account series records nothing, and the plan
    /// is not even read: this is the common case after the first complete read of the day, and it has
    /// to be the cheapest one.
    /// </summary>
    [Fact]
    public async Task A_day_already_claimed_records_nothing_and_reads_no_plan()
    {
        var snapshots = new FakeConnectedMachineSnapshotStore(
            new ConnectedMachineSnapshot(Today, 1, 2, [101, 102], Now.AddHours(-2), true));
        var grants = new FakeManualPlanGrantStore(Grant(SubscriptionPlan.Pro));

        var outcome = await UseCase(snapshots: snapshots, grants: grants)
            .RecordFromFleetReadAsync(Read(101, 102, 103), CancellationToken.None);

        Assert.Equal(ConnectedMachineSnapshotOutcome.AlreadyRecorded, outcome);
        Assert.Equal(0, grants.Reads);
        Assert.Equal(0, snapshots.RecordAttempts);

        // And the stored row is untouched: a second read of the same day is not a correction of the
        // first, because a snapshot is append-only evidence.
        var stored = Assert.Single(snapshots.Snapshots);
        Assert.Equal(2, stored.MachineCount);
    }

    /// <summary>
    /// When two complete reads race, the first to commit wins and the other discards its snapshot -
    /// no error, no retry, and the stored row is the winner's.
    /// </summary>
    [Fact]
    public async Task The_loser_of_a_race_discards_its_snapshot_without_an_error()
    {
        var snapshots = new FakeConnectedMachineSnapshotStore
        {
            ClaimedByAnotherReaderOnNextRecord =
                new ConnectedMachineSnapshot(Today, 1, 2, [101, 102], Now.AddMinutes(-1), true),
        };

        var outcome = await UseCase(snapshots: snapshots)
            .RecordFromFleetReadAsync(Read(101, 102, 103), CancellationToken.None);

        Assert.Equal(ConnectedMachineSnapshotOutcome.SupersededByAnotherRead, outcome);

        var stored = Assert.Single(snapshots.Snapshots);
        Assert.Equal(2, stored.MachineCount);
    }

    /// <summary>
    /// The daily job's path reads the fleet once and records what it read, and reports that it spent
    /// a call - which is what the run-wide cap counts.
    /// </summary>
    [Fact]
    public async Task The_job_reads_the_fleet_once_and_records_it()
    {
        var snapshots = new FakeConnectedMachineSnapshotStore();
        var fleet = FakeConnectedMachineFleetReader.Returning(101, 102);

        var attempt = await UseCase(snapshots: snapshots, fleet: fleet)
            .ReadFleetAndRecordAsync(CancellationToken.None);

        Assert.Equal(ConnectedMachineSnapshotOutcome.Recorded, attempt.Outcome);
        Assert.True(attempt.FleetWasRead);
        Assert.Equal(1, fleet.Reads);
        Assert.Equal(2, Assert.Single(snapshots.Snapshots).MachineCount);
    }

    /// <summary>
    /// And every skip on that path happens before the call: a business that is not counted, cannot be
    /// read, or has already been counted today costs no Nayax call at all.
    /// </summary>
    [Theory]
    [InlineData(SubscriptionPlan.Free, NayaxConnectionStatus.Ready, false)]
    [InlineData(SubscriptionPlan.Pro, NayaxConnectionStatus.NeedsAttention, false)]
    [InlineData(SubscriptionPlan.Pro, NayaxConnectionStatus.Ready, true)]
    public async Task The_job_makes_no_call_for_a_business_it_skips(
        SubscriptionPlan plan,
        NayaxConnectionStatus status,
        bool alreadyCountedToday)
    {
        var snapshots = alreadyCountedToday
            ? new FakeConnectedMachineSnapshotStore(
                new ConnectedMachineSnapshot(Today, 1, 2, [101, 102], Now.AddHours(-1), true))
            : new FakeConnectedMachineSnapshotStore();
        var fleet = FakeConnectedMachineFleetReader.Unreachable();

        var attempt = await UseCase(
                snapshots: snapshots,
                connections: Connection(status),
                plan: plan,
                fleet: fleet)
            .ReadFleetAndRecordAsync(CancellationToken.None);

        Assert.False(attempt.FleetWasRead);
        Assert.NotEqual(ConnectedMachineSnapshotOutcome.Recorded, attempt.Outcome);
        Assert.Equal(0, fleet.Reads);
    }

    /// <summary>
    /// A failed Nayax read propagates rather than becoming a smaller fleet: the job reports the
    /// failure, nothing is stored, and the business's last complete snapshot remains its count.
    /// </summary>
    [Fact]
    public async Task A_failed_fleet_read_stores_nothing_and_propagates()
    {
        var snapshots = new FakeConnectedMachineSnapshotStore();
        var fleet = FakeConnectedMachineFleetReader.Failing(new TimeoutException("Nayax is away."));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            UseCase(snapshots: snapshots, fleet: fleet).ReadFleetAndRecordAsync(CancellationToken.None));

        Assert.Empty(snapshots.Snapshots);
    }

    /// <summary>
    /// A read the adapter could not complete stores nothing on the job's path either, and is reported
    /// as the call it did spend.
    /// </summary>
    [Fact]
    public async Task The_job_stores_nothing_from_an_incomplete_read()
    {
        var snapshots = new FakeConnectedMachineSnapshotStore();
        var fleet = FakeConnectedMachineFleetReader.Incomplete(101, 102);

        var attempt = await UseCase(snapshots: snapshots, fleet: fleet)
            .ReadFleetAndRecordAsync(CancellationToken.None);

        Assert.Equal(ConnectedMachineSnapshotOutcome.IncompleteRead, attempt.Outcome);
        Assert.True(attempt.FleetWasRead);
        Assert.Empty(snapshots.Snapshots);
    }

    private static RecordConnectedMachineSnapshot UseCase(
        FakeConnectedMachineSnapshotStore snapshots,
        FakeNayaxConnectionStore? connections = null,
        FakeManualPlanGrantStore? grants = null,
        SubscriptionPlan plan = SubscriptionPlan.Pro,
        FakeConnectedMachineFleetReader? fleet = null,
        int accountSeries = 1) =>
        new(
            connections ?? Connection(NayaxConnectionStatus.Ready, accountSeries),
            snapshots,
            grants ?? new FakeManualPlanGrantStore(Grant(plan)),
            new FakeBillingSubscriptionStore(),
            fleet ?? FakeConnectedMachineFleetReader.Returning(101),
            new FakeClock(Now));

    private static FakeNayaxConnectionStore Connection(
        NayaxConnectionStatus status,
        int accountSeries = 1) =>
        new("4242", "token", status, accountSeries: accountSeries);

    private static ManualPlanGrantState Grant(SubscriptionPlan plan) =>
        new(
            plan,
            ManualPlanGrantSource.Grandfathered,
            new EntitlementPeriod(Now.AddDays(-30), null));

    private static ConnectedMachineFleetRead Read(params long[] providerMachineIds) =>
        ConnectedMachineFleetRead.Complete(providerMachineIds.Select(id => (long?)id));
}
