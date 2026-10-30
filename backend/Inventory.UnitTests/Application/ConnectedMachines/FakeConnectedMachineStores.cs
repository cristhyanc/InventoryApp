using Inventory.Application.ConnectedMachines;
using Inventory.Domain.ConnectedMachines;

namespace InventoryApi.Tests.Application.ConnectedMachines;

/// <summary>
/// In-memory fake of the connected-machine snapshot port (issue #978), holding one business's rows,
/// so the Application tests exercise the claim, the skipping and the race without a database.
///
/// <para>It records the reads as well as the writes, because several of the properties under test are
/// about work that must <em>not</em> happen: a business whose plan is not counted must not be read
/// for snapshots, and an incomplete read must claim nothing at all.</para>
///
/// <para>It reproduces the real adapter's insert-if-absent behaviour - a day and series that are
/// already claimed are left exactly as they are and the caller is told it did not write - because
/// that is what the use case's outcomes depend on. The relational proof that the unique index does
/// the same lives in the integration tests.</para>
/// </summary>
internal sealed class FakeConnectedMachineSnapshotStore : IConnectedMachineSnapshotStore
{
    private readonly List<ConnectedMachineSnapshot> _snapshots;

    public FakeConnectedMachineSnapshotStore(params ConnectedMachineSnapshot[] snapshots) =>
        _snapshots = [.. snapshots];

    /// <summary>Every snapshot the store holds, in the order it was told about them.</summary>
    public IReadOnlyList<ConnectedMachineSnapshot> Snapshots => _snapshots;

    /// <summary>How many times the latest day was read.</summary>
    public int LatestDayReads { get; private set; }

    /// <summary>How many times an existing claim was checked.</summary>
    public int ExistenceChecks { get; private set; }

    /// <summary>How many times a snapshot was offered, whether or not it was written.</summary>
    public int RecordAttempts { get; private set; }

    /// <summary>
    /// A snapshot another reader commits <em>between</em> this store's existence check and its
    /// record - the race of issue #978, which the loser must discard without an error.
    /// </summary>
    public ConnectedMachineSnapshot? ClaimedByAnotherReaderOnNextRecord { get; set; }

    public Task<IReadOnlyList<ConnectedMachineSnapshot>> FindLatestDayAsync(
        CancellationToken cancellationToken)
    {
        LatestDayReads++;

        if (_snapshots.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<ConnectedMachineSnapshot>>([]);
        }

        var latestDay = _snapshots.Max(snapshot => snapshot.DateUtc);

        return Task.FromResult<IReadOnlyList<ConnectedMachineSnapshot>>(
            [.. _snapshots.Where(snapshot => snapshot.DateUtc == latestDay).OrderBy(snapshot => snapshot.AccountSeries)]);
    }

    public Task<bool> ExistsForAsync(
        DateTime dayUtc,
        int accountSeries,
        CancellationToken cancellationToken)
    {
        ExistenceChecks++;

        return Task.FromResult(Exists(dayUtc, accountSeries));
    }

    public Task<bool> TryRecordAsync(
        ConnectedMachineSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        RecordAttempts++;

        if (ClaimedByAnotherReaderOnNextRecord is { } other)
        {
            ClaimedByAnotherReaderOnNextRecord = null;
            _snapshots.Add(other);
        }

        if (Exists(snapshot.DateUtc, snapshot.AccountSeries))
        {
            return Task.FromResult(false);
        }

        _snapshots.Add(snapshot);

        return Task.FromResult(true);
    }

    private bool Exists(DateTime dayUtc, int accountSeries) =>
        _snapshots.Any(stored =>
            stored.DateUtc == dayUtc && stored.AccountSeries == accountSeries);
}

/// <summary>
/// In-memory fake of the connected-machine fleet reader (issue #978): the one Nayax call the daily
/// job makes, as a test decides it - a fleet, an incomplete read, or a failure.
///
/// <para>Every answer carries the account series the read was authenticated as, because that is what
/// the recorder stamps a snapshot with. <see cref="DuringRead"/> is what makes the job path's race
/// testable without a web host: it runs while the call is "in flight", so a test can switch the
/// business's Nayax account between the job's eligibility read and the fleet answer and then assert
/// which account the evidence landed under.</para>
/// </summary>
internal sealed class FakeConnectedMachineFleetReader : IConnectedMachineFleetReader
{
    private readonly ConnectedMachineFleetRead? _read;
    private readonly Exception? _failure;

    private FakeConnectedMachineFleetReader(ConnectedMachineFleetRead? read, Exception? failure)
    {
        _read = read;
        _failure = failure;
    }

    /// <summary>A reader that answers a complete read of these provider machine ids.</summary>
    public static FakeConnectedMachineFleetReader Returning(params long[] providerMachineIds) =>
        ReturningFor(accountSeries: 1, providerMachineIds);

    /// <summary>
    /// A reader whose read was authenticated as <paramref name="accountSeries"/> - the account the
    /// fleet answer really came from.
    /// </summary>
    public static FakeConnectedMachineFleetReader ReturningFor(
        int? accountSeries, params long[] providerMachineIds) =>
        new(
            ConnectedMachineFleetRead.Complete(
                accountSeries, providerMachineIds.Select(id => (long?)id)),
            failure: null);

    /// <summary>A reader whose read did not account for the whole fleet.</summary>
    public static FakeConnectedMachineFleetReader Incomplete(params long[] providerMachineIds) =>
        new(
            ConnectedMachineFleetRead.Incomplete(
                accountSeries: 1, providerMachineIds.Select(id => (long?)id)),
            failure: null);

    /// <summary>A reader whose call to Nayax failed, as an upstream failure does.</summary>
    public static FakeConnectedMachineFleetReader Failing(Exception failure) => new(read: null, failure);

    /// <summary>A reader that must never be called, for a business the job has to skip.</summary>
    public static FakeConnectedMachineFleetReader Unreachable() =>
        new(
            read: null,
            new InvalidOperationException(
                "The fleet must not be read for a business the snapshot rules skip."));

    /// <summary>How many times Nayax was called.</summary>
    public int Reads { get; private set; }

    /// <summary>
    /// What happens while this read is in flight - an Owner saving a different Nayax account, for
    /// example. It runs after the caller decided to spend the call and before the answer is handed
    /// back, which is exactly the window the job path's attribution has to survive.
    /// </summary>
    public Func<Task>? DuringRead { get; set; }

    public async Task<ConnectedMachineFleetRead> ReadAsync(CancellationToken cancellationToken)
    {
        Reads++;

        if (DuringRead is { } duringRead)
        {
            await duringRead().ConfigureAwait(false);
        }

        return _failure is not null
            ? throw _failure
            : _read!;
    }
}
