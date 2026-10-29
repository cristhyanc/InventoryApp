using Inventory.Domain.ConnectedMachines;

namespace Inventory.Application.ConnectedMachines;

/// <summary>
/// What recording a connected-machine snapshot did (issue #978). Every answer is a distinct fact
/// about the current business, and none of them is an error: a day that is already claimed, a plan
/// that is not counted and a connection that cannot be used are all normal.
/// </summary>
public enum ConnectedMachineSnapshotOutcome
{
    /// <summary>This call wrote the day's snapshot for the current account series.</summary>
    Recorded = 10,

    /// <summary>
    /// The day and series were already claimed before this call tried, so nothing was written -
    /// which is what "one read per business per day" looks like from the second reader of the day.
    /// </summary>
    AlreadyRecorded = 20,

    /// <summary>
    /// Two complete reads raced and the other one committed first. This call discarded its snapshot:
    /// no error, no retry, and the stored row is the winner's.
    /// </summary>
    SupersededByAnotherRead = 30,

    /// <summary>
    /// The read was not complete - it failed part way, a page was refused, or the caller could not
    /// tell - so nothing was stored and the last complete snapshot remains the count.
    /// </summary>
    IncompleteRead = 40,

    /// <summary>
    /// The business's plan does not have connected machines counted
    /// (<see cref="ConnectedMachineSnapshotPolicy.RecordsSnapshots"/>). Nothing is written and
    /// nothing is withheld from it.
    /// </summary>
    PlanDoesNotCount = 50,

    /// <summary>
    /// The business has no usable Nayax connection - nothing stored, or credentials known not to
    /// work - so there was no account to count and, on the job's path, no call was made.
    /// </summary>
    ConnectionUnavailable = 60,
}

/// <summary>
/// One attempt by the daily job to count one business (issue #978).
/// </summary>
/// <param name="Outcome">What the attempt did.</param>
/// <param name="FleetWasRead">
/// Whether this attempt actually called Nayax. It is what the job's run-wide cap counts, because the
/// cap exists to bound remote reads rather than loop iterations: a business skipped for its plan, its
/// connection or a day already counted costs no call at all.
/// </param>
public sealed record ConnectedMachineSnapshotAttempt(
    ConnectedMachineSnapshotOutcome Outcome,
    bool FleetWasRead);

/// <summary>
/// The single write path for a connected-machine snapshot (issue #978), with the two ways in that
/// exist.
///
/// <para><strong>Both ways end in the same insert-if-absent claim</strong>
/// (<see cref="IConnectedMachineSnapshotStore.TryRecordAsync"/>), which is what makes concurrent page
/// loads and the daily job unable to produce two snapshots for one business, day and account series.
/// Neither way takes a business, a date, a series or a count from a caller: the business is the one
/// the scope resolved, the day and the instant come from the application clock, the series comes from
/// the business's own stored connection, and the count comes from the fleet read.</para>
/// </summary>
public interface IConnectedMachineSnapshotRecorder
{
    /// <summary>
    /// Records today's snapshot from a fleet read the application has <strong>already</strong>
    /// performed - the refresh path of #978: whenever a feature reads the whole fleet for a counted
    /// business, that read becomes the day's evidence and no extra Nayax call is made for it.
    /// </summary>
    Task<ConnectedMachineSnapshotOutcome> RecordFromFleetReadAsync(
        ConnectedMachineFleetRead read,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the fleet and records today's snapshot - the daily job's path, which is the only one
    /// that spends a Nayax call of its own.
    ///
    /// <para>Everything that can skip the business is decided <strong>before</strong> the call: a
    /// plan whose machines are not counted, a connection that cannot be used, and a day already
    /// claimed for the current series each answer without reading anything remotely. There is no
    /// retry here - the Nayax client's own bounded resilience is the only retry in the path - and an
    /// upstream failure propagates, so the job can report it and move on without writing anything.</para>
    /// </summary>
    Task<ConnectedMachineSnapshotAttempt> ReadFleetAndRecordAsync(CancellationToken cancellationToken);
}
