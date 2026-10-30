using System.Linq.Expressions;
using Inventory.Application.MachineStockSync;
using Inventory.Domain.Nayax;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
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
    /// The monotonic, credential-fenced conditional write (issue #891): one <c>UPDATE</c> whose
    /// <c>WHERE</c> clause <em>is</em> both comparisons, so two concurrent syncs cannot read, judge
    /// and overwrite the stored coverage in a way that loses the newer of them, and no run can
    /// record coverage against credentials it cannot have used.
    ///
    /// <para><strong>Nothing left to sync</strong> (issue #1018) is a further condition of the same
    /// statement: the machine's provider number <paramref name="nayaxMachineId"/> has no event that
    /// <see cref="IsPendingSince"/> counts from <paramref name="pendingFromUtc"/>. Being part of the
    /// <c>WHERE</c> clause rather than a read before it, an event imported by a concurrent sync
    /// cannot land between the check and the write.</para>
    ///
    /// <para><strong>The credential fence.</strong> The statement's first condition is that the
    /// business's stored connection <em>still</em> holds exactly
    /// <paramref name="credentialRevision"/> - the revision the caller read before its first Nayax
    /// call. <c>BusinessNayaxConnection.CredentialRevision</c> only ever increases
    /// (<c>EfNayaxConnectionStore.SaveCredentialAsync</c> increments it in the database, nothing
    /// lowers it and there is no path that deletes the row), so a stored revision equal to the
    /// offered one proves that no credential save landed between that read and this statement.
    /// That, and not the caller's read on its own, is what makes the claim's provenance verified:
    /// every Nayax request the operation made in between therefore resolved its credentials from
    /// that same revision. A save that landed anywhere in that window - including after the caller
    /// read the revision but before the request captured the credentials it authenticated with -
    /// moves the stored revision past the offered one, matches no row, and leaves the coverage
    /// unrecorded rather than misattributed. It is a fence rather than a separate check precisely
    /// because the comparison and the write are one statement: a save committed while this
    /// statement runs cannot slip between them.</para>
    ///
    /// <para>Coverage is then ordered by <c>(credential revision, started instant)</c> and only a
    /// strictly greater pair wins. A row with no coverage at all is below every pair, so the first
    /// success always lands; a higher revision always wins, because a credential save supersedes
    /// everything the previous token proved; at the same revision only a later start instant wins,
    /// so the sync that started earlier can never move the timestamp backwards; and a lower revision
    /// never wins, which refuses an old-revision completion even in the one case the fence cannot
    /// speak about - a run whose own revision is still the stored one while stored coverage is
    /// newer.</para>
    ///
    /// <para>It is deliberately the same shape as
    /// <c>EfNayaxConnectionStore.TryApplyStatusResultAsync</c>, including the set-based read-only
    /// guard: an <c>ExecuteUpdate</c> runs in the database and never reaches
    /// <c>BusinessOwnershipEnforcer</c>, so the support session's read-only rule is applied here
    /// explicitly (issue #681). A support session cannot reach this method today - it resolves no
    /// Nayax credentials, so the sync this follows cannot run at all - and the guard is what keeps
    /// that true without depending on it.</para>
    ///
    /// <para>Both halves are scoped by the central tenant query filters, the connection subquery
    /// included: the fence reads the <em>caller's own</em> business's connection, and a caller whose
    /// business could not be resolved matches no connection and no machine.</para>
    /// </summary>
    public async Task<bool> TryRecordSuccessfulSyncAsync(
        long localMachineId,
        DateTime startedAtUtc,
        int credentialRevision,
        long nayaxMachineId,
        DateTime pendingFromUtc,
        CancellationToken cancellationToken)
    {
        SetBasedWriteEnforcer.RequireWritableScope(_db);

        // Issue #1018: "synced" means nothing is left to sync, so the machine's pending events - by
        // the very predicate the reminder counts with - are a third condition of the same statement.
        var pendingEvents = _db.NayaxMachineStockEvents.Where(IsPendingSince(pendingFromUtc));

        var updated = await _db.Machines
            .Where(machine => machine.Id == localMachineId
                && !pendingEvents.Any(stockEvent => stockEvent.MachineId == nayaxMachineId)
                && _db.BusinessNayaxConnections.Any(
                    connection => connection.CredentialRevision == credentialRevision)
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
    /// as already recorded manually do not. Two further bounds come from issue #948: only events at
    /// or after <paramref name="pendingFromUtc"/> count, and an event whose parsed quantity is zero
    /// or negative never counts - that is a machine discrepancy, which
    /// <c>NayaxMachineStockApplyPolicy</c> never applies, not a restock. An unparsed event has no
    /// quantity and still counts, because it needs review. Nothing outside those bounds is changed:
    /// it is simply not on the Dashboard, and Sync Restock still lists it.</para>
    ///
    /// <para>A machine whose stored external id is not text <see cref="NayaxExternalId"/> could have
    /// written resolves to no provider number, so its events cannot be found and it is reported with
    /// no pending count rather than with another machine's. Its own coverage is still reported,
    /// because that is recorded against the local machine.</para>
    /// </summary>
    public async Task<IReadOnlyList<MachineRestockSyncCoverageRecord>> GetProviderMachineCoverageAsync(
        DateTime pendingFromUtc, CancellationToken cancellationToken)
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
            .Where(IsPendingSince(pendingFromUtc))
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
    /// What counts as pending restock work (issues #891 and #948), written once: still
    /// <c>Unprocessed</c>, not reconciled as already recorded manually, dated at or after
    /// <paramref name="pendingFromUtc"/>, and positive or unparsed. The reminder's counts and the
    /// "nothing left to sync" condition of the coverage write (issue #1018) both use it, so the two
    /// can never disagree about whether a machine has work left.
    /// </summary>
    private static Expression<Func<NayaxMachineStockEvent, bool>> IsPendingSince(DateTime pendingFromUtc) =>
        stockEvent =>
            stockEvent.ProcessingStatus == NayaxStockEventProcessingStatus.Unprocessed
            && stockEvent.DuplicateResolution != NayaxDuplicateResolution.ReconciledManually
            && stockEvent.EventDateTimeGmt >= pendingFromUtc
            && (stockEvent.ParsedQuantity == null || stockEvent.ParsedQuantity > 0);

    /// <summary>
    /// The grouped <c>MIN</c> above is computed by SQLite, so its value does not pass through the
    /// <c>EventDateTimeGmt</c> property's Kind conversion the way a projected column does. The
    /// stored instant is UTC (<c>SyncMachineStockFromNayax.AsUtc</c>, issue #217), so it is marked as
    /// such here: an instant with no <c>Z</c> would be read as browser-local time by the frontend's
    /// date pipe.
    /// </summary>
    private static DateTime AsUtc(DateTime instant) => DateTime.SpecifyKind(instant, DateTimeKind.Utc);
}
