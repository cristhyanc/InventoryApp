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
/// fleet read's own provenance, and the count from that same complete read - so a snapshot can never
/// be a figure somebody submitted.</para>
///
/// <para><strong>A snapshot is stamped with the account that authenticated the read it came
/// from.</strong> <see cref="ConnectedMachineFleetRead.AccountSeries"/> is the series of the very
/// connection snapshot whose credentials the fleet request carried, and it is the <em>only</em> thing
/// this use case will stamp a row with. It never reads a series of its own, because that is a
/// different fact: an Owner can save a different Nayax account while a fleet request is in flight, so
/// a series read after the answer arrives (the refresh path) or before the request leaves (the job's
/// path) can name the account the fleet was not read from. Carrying it has three consequences worth
/// stating, because together they are the whole guarantee:
/// <list type="bullet">
/// <item>a superseded in-flight answer is recorded as <em>its own</em> account's evidence, which is
/// what it truthfully is, and the old series' rows stay immutable;</item>
/// <item>it can never claim the replacement account's day, because the claim it makes names its own
/// series - there is no superseded-response discard to make atomic, since the claim and the
/// provenance are one value;</item>
/// <item>a later valid read of the new account therefore still records its own snapshot that same
/// day, and <see cref="ConnectedMachineSnapshotPolicy.Latest"/> decides which of the day's rows is
/// the count.</item>
/// </list>
/// A read whose provenance is unknown records nothing at all
/// (<see cref="ConnectedMachineSnapshotOutcome.AccountUnknown"/>): evidence that cannot name its
/// account is not evidence.</para>
///
/// <para><strong>Every skip is decided before anything expensive happens.</strong> The connection is
/// read first - to answer "may this business's fleet be read at all", and nothing else; the day's
/// claim is checked next, because after the first snapshot of the day that is the common case and it
/// costs one indexed read; the plan records are read last, because they are two reads that only
/// matter for a business that is otherwise countable. On the job's path the whole sequence runs
/// <em>before</em> the Nayax call, so a business that is skipped costs no call - and the series that
/// preflight used for the day's claim check is a cheap skip only, replaced by the read's own series
/// the moment the fleet answers.</para>
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

        if (read.AccountSeries is not { } series)
        {
            // Second, and still before anything is read: a fleet read that cannot say which Nayax
            // account authenticated it cannot be attributed, and an unattributable count is worse
            // than none. Nothing guesses the current series here - that is the whole defect this
            // guard exists for.
            return ConnectedMachineSnapshotOutcome.AccountUnknown;
        }

        if (!await MayCountThisBusinessAsync(cancellationToken).ConfigureAwait(false))
        {
            return ConnectedMachineSnapshotOutcome.ConnectionUnavailable;
        }

        // The read's own series from here on: for the day's existing-claim check and for the claim
        // itself, so both describe the account the fleet actually came from.
        var refusal = await RefusalAsync(series, cancellationToken).ConfigureAwait(false);

        return refusal ?? await ClaimAsync(read, series, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ConnectedMachineSnapshotAttempt> ReadFleetAndRecordAsync(
        CancellationToken cancellationToken)
    {
        // The preflight's series is the business's current one, and it is used for one thing only:
        // deciding whether this business is worth a Nayax call at all. It is deliberately not
        // carried into the claim - the account can change while the call is in flight, and the read
        // that comes back names the account it was really made with.
        var currentSeries = await CurrentSeriesAsync(cancellationToken).ConfigureAwait(false);

        if (currentSeries is not { } series)
        {
            return new ConnectedMachineSnapshotAttempt(
                ConnectedMachineSnapshotOutcome.ConnectionUnavailable,
                FleetWasRead: false);
        }

        if (await RefusalAsync(series, cancellationToken).ConfigureAwait(false) is { } refusal)
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

        if (read.AccountSeries is not { } readSeries)
        {
            return new ConnectedMachineSnapshotAttempt(
                ConnectedMachineSnapshotOutcome.AccountUnknown,
                FleetWasRead: true);
        }

        // The read's series, never the preflight's: if the account changed in between, this snapshot
        // belongs to the account the fleet was read from, and the other account's day stays free for
        // its own read.
        var outcome = await ClaimAsync(read, readSeries, cancellationToken).ConfigureAwait(false);

        return new ConnectedMachineSnapshotAttempt(outcome, FleetWasRead: true);
    }

    /// <summary>
    /// The business's current account series, or <see langword="null"/> when it has no connection
    /// this application may read a fleet with.
    ///
    /// <para><strong>The series it returns is a cheap skip, never provenance.</strong> The job path
    /// uses it to decide whether this business is worth a Nayax call - no row at all is
    /// <c>NotConfigured</c>, a connection the gate refuses is one an operator has to fix, and a day
    /// already claimed for the current account needs no call either. What a snapshot is
    /// <em>stamped</em> with is always the series the fleet read itself carried.</para>
    /// </summary>
    private async Task<int?> CurrentSeriesAsync(CancellationToken cancellationToken)
    {
        var connection = await _connections.FindAsync(cancellationToken).ConfigureAwait(false);

        return connection is not null
            && ConnectedMachineSnapshotPolicy.MayReadFleet(connection.Status)
            ? connection.AccountSeries
            : null;
    }

    /// <summary>
    /// Whether this business has a Nayax connection whose fleet this application may count at all -
    /// the same gate the job applies before spending a call, applied on the refresh path after a
    /// read some other feature already performed.
    ///
    /// <para>It deliberately answers a <see langword="bool"/>: the series this read happens to hold
    /// is <strong>not</strong> consulted, because the row it is read from may by now belong to a
    /// different Nayax account than the one that authenticated the fleet request. The gate is about
    /// the business, the series is about the request, and keeping them apart is the point.</para>
    /// </summary>
    private async Task<bool> MayCountThisBusinessAsync(CancellationToken cancellationToken) =>
        await CurrentSeriesAsync(cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// Everything other than the account itself that can stop a snapshot being recorded for
    /// <paramref name="accountSeries"/>, in the order explained on the type: the day's existing
    /// claim, then the plan.
    /// </summary>
    private async Task<ConnectedMachineSnapshotOutcome?> RefusalAsync(
        int accountSeries,
        CancellationToken cancellationToken)
    {
        var day = ConnectedMachineSnapshotDay.For(_clock.UtcNow);

        if (await _snapshots.ExistsForAsync(day, accountSeries, cancellationToken).ConfigureAwait(false))
        {
            return ConnectedMachineSnapshotOutcome.AlreadyRecorded;
        }

        var plan = ConnectedMachineSnapshotPolicy.PlanOnRecord(
            await _grants.ListForCurrentBusinessAsync(cancellationToken).ConfigureAwait(false),
            await _subscriptions.FindForCurrentBusinessAsync(cancellationToken).ConfigureAwait(false),
            _clock.UtcNow);

        return ConnectedMachineSnapshotPolicy.RecordsSnapshots(plan)
            ? null
            : ConnectedMachineSnapshotOutcome.PlanDoesNotCount;
    }

    /// <summary>
    /// The claim itself: one insert-if-absent for this business, this UTC day and
    /// <paramref name="accountSeries"/> - which is always the series of the connection snapshot that
    /// authenticated <paramref name="read"/>. Losing it is not an error: the other reader's snapshot
    /// is just as complete as this one, so this one is discarded.
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
}
