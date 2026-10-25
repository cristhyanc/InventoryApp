namespace Inventory.Application.MachineStockSync;

/// <summary>
/// One provider-linked machine's locally persisted restock-sync state (issue #891), as the reminder
/// use case reads it. Everything on it is this application's own database: no figure here comes
/// from a Nayax call.
/// </summary>
/// <param name="LocalMachineId">
/// <c>Machines.Id</c>, this application's own key - what the machine details route takes.
/// </param>
/// <param name="MachineName">What the operator calls the machine.</param>
/// <param name="LastSuccessfulSyncStartedAtUtc">
/// The instant the last fully successful <em>Sync Restock</em> operation <em>started</em>, or
/// <see langword="null"/> when none is recorded. The start instant and not the completion instant,
/// because a fetch can only have accounted for alerts that existed when it began.
/// </param>
/// <param name="CredentialRevision">
/// The Nayax credential revision that fetch authenticated under, or <see langword="null"/> when no
/// fetch is recorded. Compared against the connection's current revision by
/// <c>MachineRestockSyncReminderPolicy.IsCoverageCurrent</c>: an older revision's success is not
/// evidence about the token that replaced it.
/// </param>
/// <param name="KnownPendingEventCount">
/// How many imported restock events of this machine are still unresolved - neither applied nor
/// explicitly reconciled as already recorded manually. It counts <em>persisted</em> facts only:
/// zero means nothing imported is outstanding, never that Nayax holds no un-fetched alert.
/// </param>
/// <param name="OldestKnownPendingEventAtUtc">
/// The canonical <c>EventDateTimeGMT</c> of the oldest of those events, or <see langword="null"/>
/// when there is none.
/// </param>
public sealed record MachineRestockSyncCoverageRecord(
    long LocalMachineId,
    string MachineName,
    DateTime? LastSuccessfulSyncStartedAtUtc,
    int? CredentialRevision,
    int KnownPendingEventCount,
    DateTime? OldestKnownPendingEventAtUtc);

/// <summary>
/// Narrow persistence port for the Sync Restock workflow's own successful-fetch coverage and the
/// unresolved-event counts the Dashboard reminder is built from (issue #891), owned by the
/// Application layer.
///
/// <para>It takes no business id, like every other store here: ownership is applied centrally by the
/// <c>AppDbContext</c> tenant query filters and <c>BusinessOwnershipEnforcer</c>, and a business id
/// a method accepted would be one a later caller could supply.</para>
///
/// <para><strong>It is separate from <see cref="IMachineStockEventStore"/> on purpose.</strong> That
/// port is the machine-level import and apply workflow, reached one machine at a time with the
/// provider's machine number. This one is a whole-business aggregate read keyed by the local
/// machine, plus one conditional write; keeping them apart is what lets the reminder read be an
/// aggregate query rather than a fan-out over machines, and keeps the apply path's surface
/// unchanged.</para>
/// </summary>
public interface IMachineRestockSyncCoverageStore
{
    /// <summary>
    /// Records that a <em>Sync Restock</em> operation for local machine
    /// <paramref name="localMachineId"/> fully succeeded, but only while that is really newer
    /// coverage than what is stored.
    ///
    /// <para><strong>The comparison and the write are one statement.</strong> Coverage is ordered by
    /// <c>(credential revision, started instant)</c> and the stored row is advanced only when the
    /// offered pair is strictly greater:</para>
    /// <list type="bullet">
    ///   <item>a higher <paramref name="credentialRevision"/> than the stored one always wins - the
    ///   credentials were replaced and this is the first success under the new ones;</item>
    ///   <item>at the same revision, a later <paramref name="startedAtUtc"/> wins, so two
    ///   concurrent syncs cannot let the one that started earlier move the timestamp backwards;</item>
    ///   <item>a <em>lower</em> revision never wins, which is what stops an operation that began
    ///   before a credential save from marking the new credentials fresh.</item>
    /// </list>
    ///
    /// <para>It records nothing else: no event, no inventory quantity, no cost and no connection
    /// status. It is also the only thing in this application that writes this coverage - nothing
    /// backfills it, and no migration dates it from history that was never measured.</para>
    /// </summary>
    /// <param name="localMachineId">The local machine the successful fetch was for.</param>
    /// <param name="startedAtUtc">
    /// The instant the operation started, taken before its first Nayax call.
    /// </param>
    /// <param name="credentialRevision">
    /// The Nayax credential revision the operation authenticated under.
    /// </param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when the stored coverage was advanced, and <see langword="false"/>
    /// when it was left alone - because the machine is not the caller's business's, or because what
    /// is stored is already the same or newer.
    /// </returns>
    Task<bool> TryRecordSuccessfulSyncAsync(
        long localMachineId, DateTime startedAtUtc, int credentialRevision, CancellationToken cancellationToken);

    /// <summary>
    /// The restock-sync state of every machine of the caller's business that its sales provider
    /// names, ordered by machine name.
    ///
    /// <para>Machines the business created itself carry no provider id, so there is no provider
    /// restock sync to be overdue for and they are absent rather than reported as never synced. A
    /// business with no provider-named machine at all reads an empty list.</para>
    ///
    /// <para>It is one aggregate read of local tables and makes no Nayax request, decrypts no
    /// credential and writes nothing - which is what makes it safe to run on every Dashboard
    /// load.</para>
    /// </summary>
    Task<IReadOnlyList<MachineRestockSyncCoverageRecord>> GetProviderMachineCoverageAsync(
        CancellationToken cancellationToken);
}
