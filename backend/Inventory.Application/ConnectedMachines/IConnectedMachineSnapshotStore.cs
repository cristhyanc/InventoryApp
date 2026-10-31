using Inventory.Domain.ConnectedMachines;

namespace Inventory.Application.ConnectedMachines;

/// <summary>
/// The narrow persistence port over a business's connected-machine snapshots (issue #978), owned by
/// the Application layer.
///
/// <para><strong>Append-only, and there is no method that is not.</strong> There is no update, no
/// delete and no correction: a snapshot is billing evidence about a UTC day, and a later day is a
/// later row. The persistence boundary refuses a modification of a stored row as well, so
/// "append-only" is a property of the store and of the database rather than of whoever remembers
/// it.</para>
///
/// <para><strong>The business is never a parameter.</strong> Every member acts on the business the
/// caller's scope already resolved, through the ordinary tenant query filters and the central
/// ownership stamp, so no route, query, body, header or command line can choose whose count is read
/// or written (AGENTS.md § Tenant ownership and data isolation). No method takes a predicate, a
/// column list, a page or a sort.</para>
/// </summary>
public interface IConnectedMachineSnapshotStore
{
    /// <summary>
    /// The snapshots of the current business's latest recorded UTC day, or an empty list when it has
    /// none.
    ///
    /// <para>The day's rows rather than one row, because a day the business's Nayax account changed
    /// on can hold one snapshot per account series, and which of them is the day's count is a Domain
    /// decision (<see cref="ConnectedMachineSnapshotPolicy.Latest"/>) rather than an
    /// <c>ORDER BY</c> this store would otherwise be the second place to express.</para>
    /// </summary>
    Task<IReadOnlyList<ConnectedMachineSnapshot>> FindLatestDayAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Whether the current business already holds a snapshot for <paramref name="dayUtc"/> in
    /// <paramref name="accountSeries"/>.
    ///
    /// <para>It is a cheap skip, never the claim: two callers can both read <see langword="false"/>
    /// here, which is exactly why <see cref="TryRecordAsync"/> is insert-if-absent and the uniqueness
    /// rule is the database's. The daily job uses it to leave a business alone before spending a
    /// Nayax call on it, and the refresh path uses it to stop before reading anything else.</para>
    /// </summary>
    Task<bool> ExistsForAsync(DateTime dayUtc, int accountSeries, CancellationToken cancellationToken);

    /// <summary>
    /// Records <paramref name="snapshot"/> for the current business unless one already exists for its
    /// UTC day and account series.
    ///
    /// <para><strong>One insert-if-absent operation, and the database decides the race.</strong> The
    /// unique constraint on (business, UTC date, account series) is what makes "at most one snapshot
    /// per business per day per series" true when a page load and the daily job read the same fleet at
    /// the same moment: the first to commit wins and the other discards its snapshot - answering
    /// <see langword="false"/>, with no error, no retry and nothing written.</para>
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when this call wrote the snapshot; <see langword="false"/> when the day
    /// and series were already claimed.
    /// </returns>
    Task<bool> TryRecordAsync(ConnectedMachineSnapshot snapshot, CancellationToken cancellationToken);
}
