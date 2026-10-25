using Inventory.Application.MachineStockSync;
using Inventory.Domain.Nayax;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IMachineRestockSyncCoverageStore"/> (issue #891).
///
/// <para>Both members are local statements only: no Nayax request, no credential decryption, and no
/// write of anything but the two coverage columns. Ownership is contributed entirely by the
/// <see cref="AppDbContext"/> tenant query filters, so nothing here writes a <c>BusinessId</c>
/// predicate of its own and a business that could not be resolved reads and writes nothing.</para>
/// </summary>
public sealed class EfMachineRestockSyncCoverageStore : IMachineRestockSyncCoverageStore
{
    private readonly AppDbContext _db;

    public EfMachineRestockSyncCoverageStore(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// The monotonic conditional write (issue #891): one <c>UPDATE</c> whose <c>WHERE</c> clause
    /// <em>is</em> the comparison, so two concurrent syncs cannot read, judge and overwrite the
    /// stored coverage in a way that loses the newer of them.
    ///
    /// <para>Coverage is ordered by <c>(credential revision, started instant)</c> and only a
    /// strictly greater pair wins. A row with no coverage at all is below every pair, so the first
    /// success always lands; a higher revision always wins, because a credential save supersedes
    /// everything the previous token proved; at the same revision only a later start instant wins,
    /// so the sync that started earlier can never move the timestamp backwards; and a lower revision
    /// never wins, which is what stops a run that began before a credential save from marking the
    /// new credentials fresh.</para>
    ///
    /// <para>It is deliberately the same shape as
    /// <c>EfNayaxConnectionStore.TryApplyStatusResultAsync</c>, including the set-based read-only
    /// guard: an <c>ExecuteUpdate</c> runs in the database and never reaches
    /// <c>BusinessOwnershipEnforcer</c>, so the support session's read-only rule is applied here
    /// explicitly (issue #681). A support session cannot reach this method today - it resolves no
    /// Nayax credentials, so the sync this follows cannot run at all - and the guard is what keeps
    /// that true without depending on it.</para>
    /// </summary>
    public async Task<bool> TryRecordSuccessfulSyncAsync(
        long localMachineId, DateTime startedAtUtc, int credentialRevision, CancellationToken cancellationToken)
    {
        SetBasedWriteEnforcer.RequireWritableScope(_db);

        var updated = await _db.Machines
            .Where(machine => machine.Id == localMachineId
                && (machine.LastSuccessfulRestockSyncCredentialRevision == null
                    || machine.LastSuccessfulRestockSyncCredentialRevision < credentialRevision
                    || (machine.LastSuccessfulRestockSyncCredentialRevision == credentialRevision
                        && (machine.LastSuccessfulRestockSyncStartedAtUtc == null
                            || machine.LastSuccessfulRestockSyncStartedAtUtc < startedAtUtc))))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(machine => machine.LastSuccessfulRestockSyncStartedAtUtc, startedAtUtc)
                    .SetProperty(
                        machine => machine.LastSuccessfulRestockSyncCredentialRevision, credentialRevision),
                cancellationToken);

        return updated > 0;
    }

    /// <summary>
    /// The whole-business aggregate read behind the Dashboard reminder: one statement over
    /// <c>Machines</c> for the provider-named machines and their coverage, and one grouped statement
    /// over the imported restock events for the unresolved counts. Two statements rather than a
    /// join, because the two sides are keyed differently - a machine by this application's own id,
    /// an event by the provider's machine number (issues #740 and #741) - and the one translation
    /// between them is <see cref="NayaxExternalId"/>, applied here over a handful of machines rather
    /// than as a <c>CAST</c> inside a join.
    ///
    /// <para><strong>Pending</strong> is the same definition the Sync Restock working list uses:
    /// still <c>Unprocessed</c> and not explicitly reconciled as already recorded manually. A
    /// needs-review, an insufficient-stock and an unresolved possible-duplicate event therefore all
    /// count - each is work an operator still has to do - while an applied event and one reconciled
    /// as already recorded manually do not. There is no date bound: work that ages out of the Sync
    /// Restock dialog's default window is still work.</para>
    ///
    /// <para>A machine whose stored external id is not text <see cref="NayaxExternalId"/> could have
    /// written resolves to no provider number, so its events cannot be found and it is reported with
    /// no pending count rather than with another machine's. Its own coverage is still reported,
    /// because that is recorded against the local machine.</para>
    /// </summary>
    public async Task<IReadOnlyList<MachineRestockSyncCoverageRecord>> GetProviderMachineCoverageAsync(
        CancellationToken cancellationToken)
    {
        var machines = await _db.Machines
            .AsNoTracking()
            .Where(machine => machine.ExternalMachineId != null)
            .OrderBy(machine => machine.Name)
            .ThenBy(machine => machine.Id)
            .Select(machine => new
            {
                machine.Id,
                machine.Name,
                machine.ExternalMachineId,
                machine.LastSuccessfulRestockSyncStartedAtUtc,
                machine.LastSuccessfulRestockSyncCredentialRevision,
            })
            .ToListAsync(cancellationToken);

        if (machines.Count == 0)
        {
            return [];
        }

        var pending = await _db.NayaxMachineStockEvents
            .AsNoTracking()
            .Where(stockEvent =>
                stockEvent.ProcessingStatus == NayaxStockEventProcessingStatus.Unprocessed
                && stockEvent.DuplicateResolution != NayaxDuplicateResolution.ReconciledManually)
            .GroupBy(stockEvent => stockEvent.MachineId)
            .Select(group => new
            {
                NayaxMachineId = group.Key,
                PendingCount = group.Count(),
                OldestEventDateTimeGmt = group.Min(stockEvent => stockEvent.EventDateTimeGmt),
            })
            .ToListAsync(cancellationToken);

        var pendingByNayaxMachineId = pending.ToDictionary(group => group.NayaxMachineId);

        return machines
            .Select(machine =>
            {
                var counts = NayaxExternalId.TryParse(machine.ExternalMachineId, out var nayaxMachineId)
                    ? pendingByNayaxMachineId.GetValueOrDefault(nayaxMachineId)
                    : null;

                return new MachineRestockSyncCoverageRecord(
                    machine.Id,
                    machine.Name,
                    machine.LastSuccessfulRestockSyncStartedAtUtc,
                    machine.LastSuccessfulRestockSyncCredentialRevision,
                    counts?.PendingCount ?? 0,
                    counts is null ? null : AsUtc(counts.OldestEventDateTimeGmt));
            })
            .ToList();
    }

    /// <summary>
    /// The grouped <c>MIN</c> above is computed by SQLite, so its value does not pass through the
    /// <c>EventDateTimeGmt</c> property's Kind conversion the way a projected column does. The
    /// stored instant is UTC (<c>SyncMachineStockFromNayax.AsUtc</c>, issue #217), so it is marked as
    /// such here: an instant with no <c>Z</c> would be read as browser-local time by the frontend's
    /// date pipe.
    /// </summary>
    private static DateTime AsUtc(DateTime instant) => DateTime.SpecifyKind(instant, DateTimeKind.Utc);
}
