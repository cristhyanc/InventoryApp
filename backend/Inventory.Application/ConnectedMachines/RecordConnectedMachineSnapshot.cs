using Inventory.Application.Nayax;
using Inventory.Application.Subscriptions;
using Inventory.Application.Time;
using Inventory.Domain.ConnectedMachines;

namespace Inventory.Application.ConnectedMachines;

/// <summary>
/// Records one UTC day's connected-machine count for the business this scope resolved (issue #978).
///
/// <para><strong>It is the only writer of a snapshot</strong>, and it takes no business, date, series
/// or count from a caller. The day comes from the application clock, the account series from the
/// business's own stored connection, and the count from a complete fleet read - so a snapshot can
/// never be a figure somebody submitted.</para>
///
/// <para><strong>Every skip is decided before anything expensive happens.</strong> The connection is
/// read first because it answers two questions at once (may the fleet be read at all, and which
/// account series a snapshot would belong to); the day's claim is checked next, because after the
/// first snapshot of the day that is the common case and it costs one indexed read; the plan records
/// are read last, because they are two reads that only matter for a business that is otherwise
/// countable. On the job's path the whole sequence runs <em>before</em> the Nayax call, so a business
/// that is skipped costs no call.</para>
///
/// <para><strong>The rules themselves are Domain.</strong> Which plans are counted, which connection
/// statuses may be read, what a complete read is and which snapshot is a day's count all belong to
/// <see cref="ConnectedMachineSnapshotPolicy"/> and <see cref="ConnectedMachineFleetRead"/>; this use
/// case only supplies them with facts and performs the claim.</para>
/// </summary>
public sealed class RecordConnectedMachineSnapshot : IConnectedMachineSnapshotRecorder
{
    private readonly INayaxConnectionStore _connections;
    private readonly IConnectedMachineSnapshotStore _snapshots;
    private readonly IManualPlanGrantStore _grants;
    private readonly IBillingSubscriptionStore _subscriptions;
    private readonly IConnectedMachineFleetReader _fleet;
    private readonly IClock _clock;

    public RecordConnectedMachineSnapshot(
        INayaxConnectionStore connections,
        IConnectedMachineSnapshotStore snapshots,
        IManualPlanGrantStore grants,
        IBillingSubscriptionStore subscriptions,
        IConnectedMachineFleetReader fleet,
        IClock clock)
    {
        _connections = connections;
        _snapshots = snapshots;
        _grants = grants;
        _subscriptions = subscriptions;
        _fleet = fleet;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<ConnectedMachineSnapshotOutcome> RecordFromFleetReadAsync(
        ConnectedMachineFleetRead read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);

        if (!read.IsComplete)
        {
            // First, and before anything is read: a fleet nobody finished reading is not a fleet
            // with fewer machines in it, so it is not evidence of anything.
            return ConnectedMachineSnapshotOutcome.IncompleteRead;
        }

        var eligibility = await EligibilityAsync(cancellationToken).ConfigureAwait(false);

        return eligibility.Refusal
            ?? await ClaimAsync(read, eligibility.AccountSeries, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ConnectedMachineSnapshotAttempt> ReadFleetAndRecordAsync(
        CancellationToken cancellationToken)
    {
        var eligibility = await EligibilityAsync(cancellationToken).ConfigureAwait(false);

        if (eligibility.Refusal is { } refusal)
        {
            return new ConnectedMachineSnapshotAttempt(refusal, FleetWasRead: false);
        }

        // The one Nayax call this feature makes of its own accord, once per business per day. A
        // failure propagates: the job reports it and moves on, and nothing is stored.
        var read = await _fleet.ReadAsync(cancellationToken).ConfigureAwait(false);

        if (!read.IsComplete)
        {
            return new ConnectedMachineSnapshotAttempt(
                ConnectedMachineSnapshotOutcome.IncompleteRead,
                FleetWasRead: true);
        }

        var outcome = await ClaimAsync(read, eligibility.AccountSeries, cancellationToken)
            .ConfigureAwait(false);

        return new ConnectedMachineSnapshotAttempt(outcome, FleetWasRead: true);
    }

    /// <summary>
    /// Everything that can stop a snapshot being recorded, in the order explained on the type: the
    /// connection, the day's existing claim, then the plan.
    /// </summary>
    private async Task<Eligibility> EligibilityAsync(CancellationToken cancellationToken)
    {
        var connection = await _connections.FindAsync(cancellationToken).ConfigureAwait(false);

        if (connection is null || !ConnectedMachineSnapshotPolicy.MayReadFleet(connection.Status))
        {
            // No row at all is NotConfigured, and a connection the gate refuses is one an operator
            // has to fix. Either way there is no account this application may count.
            return Eligibility.Refused(ConnectedMachineSnapshotOutcome.ConnectionUnavailable);
        }

        var day = ConnectedMachineSnapshotDay.For(_clock.UtcNow);
        var series = connection.AccountSeries;

        if (await _snapshots.ExistsForAsync(day, series, cancellationToken).ConfigureAwait(false))
        {
            return Eligibility.Refused(ConnectedMachineSnapshotOutcome.AlreadyRecorded);
        }

        var plan = ConnectedMachineSnapshotPolicy.PlanOnRecord(
            await _grants.ListForCurrentBusinessAsync(cancellationToken).ConfigureAwait(false),
            await _subscriptions.FindForCurrentBusinessAsync(cancellationToken).ConfigureAwait(false),
            _clock.UtcNow);

        return ConnectedMachineSnapshotPolicy.RecordsSnapshots(plan)
            ? Eligibility.Allowed(series)
            : Eligibility.Refused(ConnectedMachineSnapshotOutcome.PlanDoesNotCount);
    }

    /// <summary>
    /// The claim itself: one insert-if-absent for this business, this UTC day and this account
    /// series. Losing it is not an error - the other reader's snapshot is just as complete as this
    /// one, so this one is discarded.
    /// </summary>
    private async Task<ConnectedMachineSnapshotOutcome> ClaimAsync(
        ConnectedMachineFleetRead read,
        int accountSeries,
        CancellationToken cancellationToken)
    {
        var takenAt = _clock.UtcNow;

        var snapshot = new ConnectedMachineSnapshot(
            ConnectedMachineSnapshotDay.For(takenAt),
            accountSeries,
            read.Count,
            read.ProviderMachineIds,
            takenAt,
            ReadWasComplete: true);

        return await _snapshots.TryRecordAsync(snapshot, cancellationToken).ConfigureAwait(false)
            ? ConnectedMachineSnapshotOutcome.Recorded
            : ConnectedMachineSnapshotOutcome.SupersededByAnotherRead;
    }

    private sealed record Eligibility(ConnectedMachineSnapshotOutcome? Refusal, int AccountSeries)
    {
        public static Eligibility Allowed(int accountSeries) => new(Refusal: null, accountSeries);

        public static Eligibility Refused(ConnectedMachineSnapshotOutcome refusal) =>
            new(refusal, AccountSeries: 0);
    }
}
