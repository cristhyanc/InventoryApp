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
///
/// <para><strong>And which account a snapshot is attributed to, on both paths.</strong> A snapshot is
/// stamped with the series the fleet read itself carried - the account whose credentials
/// authenticated that request - never with a series this use case read for itself, because an Owner
/// can save a different Nayax account while a request is in flight. The account-change tests below
/// are the regressions for that: they switch the stored account mid-read, from either side, and
/// assert that no fleet lands under the wrong series and that the other account's day stays free for
/// its own read.</para>
/// </summary>
public class RecordConnectedMachineSnapshotTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 6, 30, 0, DateTimeKind.Utc);

    private static readonly DateTime Today = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A complete fleet read the application performed anyway becomes the day's snapshot - the
    /// refresh rule of #978 - and what is stored is derived entirely from the read and the clock. The
    /// series is the read's own, which is the account its credentials authenticated it as.
    /// </summary>
    [Fact]
    public async Task Records_the_days_snapshot_from_a_complete_fleet_read()
    {
        var snapshots = new FakeConnectedMachineSnapshotStore();

        var outcome = await UseCase(snapshots: snapshots)
            .RecordFromFleetReadAsync(ReadFor(4, 103, 101, 102), CancellationToken.None);

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
                ConnectedMachineFleetRead.Incomplete(accountSeries: 1, [101, 102]),
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
    /// <strong>The refresh path's account-change regression.</strong> A fleet request authenticated
    /// as account A is in flight; the Owner saves account B; only then does A's answer arrive and get
    /// offered to the recorder. The evidence must land under A - the account it actually came from -
    /// and B's day must be left unclaimed, which is exactly what the earlier defect got wrong in both
    /// directions: it stamped A's machines with B's series, and that row then blocked B's own read for
    /// the rest of the day.
    /// </summary>
    [Fact]
    public async Task A_read_that_finishes_after_an_account_change_is_recorded_under_its_own_series()
    {
        var snapshots = new FakeConnectedMachineSnapshotStore();
        var connections = Connection(NayaxConnectionStatus.Ready);

        // The fleet request resolves account A's credentials and leaves the API - series 1.
        var inFlight = ReadFor(1, 101, 102, 103);

        // While it is in flight the Owner connects a different Nayax account, which is series 2.
        await connections.SaveCredentialAsync("9999", "token-b", CancellationToken.None);
        Assert.Equal(2, connections.Connection!.AccountSeries);

        // Only now does A's answer arrive.
        var outcome = await UseCase(snapshots: snapshots, connections: connections)
            .RecordFromFleetReadAsync(inFlight, CancellationToken.None);

        Assert.Equal(ConnectedMachineSnapshotOutcome.Recorded, outcome);

        var stored = Assert.Single(snapshots.Snapshots);
        Assert.Equal(1, stored.AccountSeries);
        Assert.Equal(3, stored.MachineCount);
    }

    /// <summary>
    /// And the other half of that guarantee: because the superseded answer claimed only its own
    /// series, a valid read of the <em>new</em> account records its own snapshot the same day. The day
    /// then holds one row per series, which is #978's account-change rule, and
    /// <c>ConnectedMachineSnapshotPolicy.Latest</c> decides which of them is the day's count.
    /// </summary>
    [Fact]
    public async Task The_new_account_can_still_record_its_own_snapshot_the_same_day()
    {
        var snapshots = new FakeConnectedMachineSnapshotStore();
        var connections = Connection(NayaxConnectionStatus.Ready);
        var useCase = UseCase(snapshots: snapshots, connections: connections);

        await connections.SaveCredentialAsync("9999", "token-b", CancellationToken.None);

        // The superseded account's answer, then the new account's own read.
        Assert.Equal(
            ConnectedMachineSnapshotOutcome.Recorded,
            await useCase.RecordFromFleetReadAsync(ReadFor(1, 101, 102, 103), CancellationToken.None));
        Assert.Equal(
            ConnectedMachineSnapshotOutcome.Recorded,
            await useCase.RecordFromFleetReadAsync(ReadFor(2, 201), CancellationToken.None));

        Assert.Equal(
            [(1, 3), (2, 1)],
            snapshots.Snapshots
                .Select(snapshot => (snapshot.AccountSeries, snapshot.MachineCount))
                .Order()
                .ToArray());
        Assert.All(snapshots.Snapshots, snapshot => Assert.Equal(Today, snapshot.DateUtc));
    }

    /// <summary>
    /// Rotating the token of the <em>same</em> Nayax account is not an account change: the series is
    /// unchanged, so the day is already claimed and the second read records nothing. A fleet is not
    /// re-counted, and its evidence is not split, because an operator pasted a fresh token.
    /// </summary>
    [Fact]
    public async Task A_token_rotation_on_the_same_account_does_not_start_a_new_series()
    {
        var snapshots = new FakeConnectedMachineSnapshotStore();
        var connections = Connection(NayaxConnectionStatus.Ready);
        var useCase = UseCase(snapshots: snapshots, connections: connections);

        Assert.Equal(
            ConnectedMachineSnapshotOutcome.Recorded,
            await useCase.RecordFromFleetReadAsync(ReadFor(1, 101, 102), CancellationToken.None));

        // The same operator id with a new token: a new credential revision, the same account.
        await connections.SaveCredentialAsync("4242", "rotated-token", CancellationToken.None);
        Assert.Equal(1, connections.Connection!.AccountSeries);
        Assert.Equal(2, connections.Connection.CredentialRevision);

        Assert.Equal(
            ConnectedMachineSnapshotOutcome.AlreadyRecorded,
            await useCase.RecordFromFleetReadAsync(ReadFor(1, 101, 102, 103), CancellationToken.None));

        var stored = Assert.Single(snapshots.Snapshots);
        Assert.Equal(1, stored.AccountSeries);
        Assert.Equal(2, stored.MachineCount);
    }

    /// <summary>
    /// A read that cannot say which Nayax account authenticated it records nothing at all, and asks
    /// the stores nothing either: an unattributable count is not evidence, and guessing the account
    /// stored at this moment is the mistake this answer exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_read_with_no_account_provenance_records_nothing()
    {
        var snapshots = new FakeConnectedMachineSnapshotStore();
        var connections = Connection(NayaxConnectionStatus.Ready);

        var outcome = await UseCase(snapshots: snapshots, connections: connections)
            .RecordFromFleetReadAsync(ReadFor(null, 101, 102), CancellationToken.None);

        Assert.Equal(ConnectedMachineSnapshotOutcome.AccountUnknown, outcome);
        Assert.Empty(snapshots.Snapshots);
        Assert.Equal(0, snapshots.ExistenceChecks);
        Assert.Equal(0, connections.ConnectionReads);
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
    /// <strong>The job path's account-change regression - the mirror image of the refresh path's.</strong>
    /// The job reads eligibility (and the business's current series) <em>before</em> the call, so a
    /// save that lands while the call is in flight leaves the earlier series describing the wrong
    /// account. The fleet that comes back was authenticated as the new account, so that is the series
    /// it is recorded under, and the superseded series' day is left alone.
    /// </summary>
    [Fact]
    public async Task The_jobs_read_is_recorded_under_the_account_it_was_authenticated_as()
    {
        var snapshots = new FakeConnectedMachineSnapshotStore();
        var connections = Connection(NayaxConnectionStatus.Ready);

        // The answer comes back from the new account - series 2 - and the switch happens while the
        // call is in flight, after the job has already read eligibility for series 1.
        var fleet = FakeConnectedMachineFleetReader.ReturningFor(2, 201, 202);
        fleet.DuringRead = () =>
            connections.SaveCredentialAsync("9999", "token-b", CancellationToken.None);

        var attempt = await UseCase(snapshots: snapshots, connections: connections, fleet: fleet)
            .ReadFleetAndRecordAsync(CancellationToken.None);

        Assert.Equal(ConnectedMachineSnapshotOutcome.Recorded, attempt.Outcome);
        Assert.True(attempt.FleetWasRead);

        var stored = Assert.Single(snapshots.Snapshots);
        Assert.Equal(2, stored.AccountSeries);
        Assert.Equal(2, stored.MachineCount);
    }

    /// <summary>
    /// And nothing on the job's path is recorded for a read with no account provenance either - the
    /// call was spent, so it is reported, but a count that cannot name its account is not stored.
    /// </summary>
    [Fact]
    public async Task The_job_stores_nothing_from_a_read_with_no_account_provenance()
    {
        var snapshots = new FakeConnectedMachineSnapshotStore();
        var fleet = FakeConnectedMachineFleetReader.ReturningFor(accountSeries: null, 201);

        var attempt = await UseCase(snapshots: snapshots, fleet: fleet)
            .ReadFleetAndRecordAsync(CancellationToken.None);

        Assert.Equal(ConnectedMachineSnapshotOutcome.AccountUnknown, attempt.Outcome);
        Assert.True(attempt.FleetWasRead);
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

    /// <summary>A complete read of series 1 - the ordinary case, one account throughout.</summary>
    private static ConnectedMachineFleetRead Read(params long[] providerMachineIds) =>
        ReadFor(1, providerMachineIds);

    /// <summary>
    /// A complete read authenticated as <paramref name="accountSeries"/>: the account the fleet
    /// answer really came from, which is what a snapshot is stamped with.
    /// </summary>
    private static ConnectedMachineFleetRead ReadFor(
        int? accountSeries, params long[] providerMachineIds) =>
        ConnectedMachineFleetRead.Complete(
            accountSeries, providerMachineIds.Select(id => (long?)id));
}
