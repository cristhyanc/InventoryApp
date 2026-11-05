using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IPriceCollectorRunStore"/> (issue #924, parent #817):
/// the one write the collector work list makes.
///
/// <para><strong>Two platform tables and nothing else.</strong> <c>PriceCollectorRuns</c> and
/// <c>PriceCollectorWorkItems</c> carry no business key, so there is no filter to ignore and no stamp
/// to apply: a collector request's <em>denied</em> <c>BusinessScope</c> leaves
/// <c>BusinessOwnershipEnforcer</c> refusing every tenant-owned write exactly as it does for the
/// platform-admin surfaces, and nothing here tries one. There is no <c>IgnoreQueryFilters</c> in this
/// adapter, no raw SQL and no unscoped context.</para>
///
/// <para><strong>Expiring the open run and issuing the new one are one transaction.</strong> Without
/// it there would be an instant with two open runs, or - worse - one where the expiry committed and
/// the new run did not, leaving no open run at all while a collector was being told it had one. "At
/// most one open run" is the invariant the results endpoint of #937 reads, so it is the database's
/// rather than a sequence of saves'.</para>
///
/// <para><strong>Expiring removes nothing.</strong> An expired run keeps its counts, its work items
/// and their outcomes: what changes is its status and its end instant, which is the instant it was
/// expired rather than the instant it was due to expire - the two are different facts, and recording
/// the second as the first would say the run ended at a time nothing happened.</para>
///
/// <para><strong>But a <c>schedule=due</c> issue never expires a run a collector is still working
/// on</strong> (issue #1108). Work pending in a run that is still open and unexpired is a search in
/// progress; expiring that run would finalise it as <c>NotSearched</c>, strand the collector holding
/// it and let the same pair be issued and paid for again. So a <c>due</c> call that finds work in
/// flight defers - it writes nothing at all - and the work it would have issued is owed again on the
/// next call. The manual <c>schedule=all</c> run keeps today's behaviour and expires whatever is
/// open, which is the same deliberate bypass that lets it re-issue a covered pair.</para>
/// </summary>
public sealed class EfPriceCollectorRunStore : IPriceCollectorRunStore
{
    private readonly AppDbContext _db;

    public EfPriceCollectorRunStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<IssuedPriceCollectorRun?> IssueAsync(
        NewPriceCollectorRun run,
        PriceCollectorDueWork? onlyDue,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        foreach (var item in run.WorkItems)
        {
            // Refused before anything is written, and refused by the Domain rather than here: a work
            // item naming two products or none is work no result could ever be attributed to.
            PriceCollectorWorkItemPolicy.RequireConsistentWorkItem(
                item.MasterProductId,
                item.WaitingKey,
                outcome: null);
        }

        PriceCollectorRunPolicy.RequireConsistentRun(
            PriceCollectorRunStatus.Running,
            run.IssuedAtUtc,
            completedAtUtc: null,
            listingsCheckedCount: 0,
            observationsRecordedCount: 0,
            failureCount: 0);

        // BEGIN IMMEDIATE when the provider is SQLite (issue #1108), for the reason the pick-list
        // refill and the platform plan grant take it: the due check below reads the pairs' latest
        // answer and their in-flight state and then writes work items from that answer, so the write
        // lock has to be held before the read rather than acquired on the first INSERT - otherwise
        // two concurrent `due` calls can both read "nothing pending" and both issue the same pair.
        // With no filter the transaction is the one issue #924 shipped and does the same work.
        await using var writeLock = await SqliteWriteLockTransaction
            .BeginAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        var workItems = run.WorkItems;
        var workItemLimitReached = false;

        if (onlyDue is not null)
        {
            var due = await DueWorkItemsAsync(run, onlyDue, cancellationToken).ConfigureAwait(false);

            // The budget is filled here, from the pairs that are actually owed a search, rather than
            // by the caller from the first products the rotation reached (issue #1108): a budget's
            // worth of covered or in-flight candidates would otherwise leave this run with nothing to
            // issue while the due work just past them was never looked at.
            var selection = onlyDue.Fill(due.WorkItems);

            workItems = selection.WorkItems;
            workItemLimitReached = selection.WorkItemLimitReached;

            if (workItems.Count == 0)
            {
                // Nothing is due, so nothing at all is written - not even the expiry of the run that
                // was open. An empty run would be a run left to expire into a page of NotSearched
                // rows that claimed a search had been declined, and #1110 relies on a `due` call with
                // nothing to do leaving nothing to finalise.
                return null;
            }

            if (due.WorkIsInFlight)
            {
                // A collector is still working on an unexpired run. Issuing would expire it below and
                // finalise its pending work as NotSearched, which would strand that collector and
                // make the very pairs it is searching issuable - and payable - a second time. So this
                // call defers instead and writes nothing: the single-open-run contract holds, the
                // live run keeps its work, and the due pairs this run would have carried are owed
                // again on the next call, once that run has reported or expired.
                return null;
            }
        }

        var stillOpen = await _db.PriceCollectorRuns
            .Where(existing => existing.Status == PriceCollectorRunStatus.Running)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var open in stillOpen)
        {
            // The same closure a completion performs (issue #937), with the status fixed to
            // Expired: every search nobody answered becomes NotSearched, the per-outcome counts are
            // written, and what the run did manage is recorded. Expiring and completing differ only
            // in what the status says about who reported. On a `due` issue nothing reaching here
            // holds work in flight - the check above deferred if anything did - so this closes runs
            // that are overdue or that already reported every search, and takes no search away from
            // a collector; a manual `all` run deliberately expires whatever is open.
            await PriceCollectorRunClosure
                .CloseAsync(
                    _db,
                    open,
                    run.IssuedAtUtc,
                    PriceCollectorRunStatus.Expired,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var issued = new Models.PriceCollectorRun
        {
            StartedAtUtc = run.IssuedAtUtc,
            ExpiresAtUtc = run.ExpiresAtUtc,
            Status = PriceCollectorRunStatus.Running,
        };

        _db.PriceCollectorRuns.Add(issued);

        // Saved before the work items so the run has the key they reference. Both saves are inside
        // the one transaction, so a failure between them stores neither.
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var stored = workItems
            .Select(item => new Models.PriceCollectorWorkItem
            {
                PriceCollectorRunId = issued.Id,
                SupplierPresetId = item.SupplierPresetId,
                MasterProductId = item.MasterProductId,
                WaitingKey = item.WaitingKey,
                IssuedAtUtc = run.IssuedAtUtc,
                Outcome = null,
                OutcomeAtUtc = null,
            })
            .ToList();

        _db.PriceCollectorWorkItems.AddRange(stored);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await writeLock.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new IssuedPriceCollectorRun(
            issued.Id,
            issued.StartedAtUtc,
            run.ExpiresAtUtc,
            stored.ConvertAll(item => new IssuedPriceCollectorWorkItem(
                item.Id,
                item.SupplierPresetId,
                item.MasterProductId,
                item.WaitingKey)),
            workItemLimitReached);
    }

    /// <summary>
    /// The candidate work items that are actually due, decided inside the write transaction and
    /// before the open run is expired (issue #1108).
    ///
    /// <para><strong>Two bounded reads, and the rule is the Domain's.</strong> The first is the pairs
    /// pending in a run that is still open and unexpired - at most one such run exists, and a run
    /// carries at most <c>PriceCollectorWorkListPolicy.MaxWorkItemsPerRun</c> items, so the read is
    /// bounded by a run rather than by history. The second is the answers recorded at or after the
    /// <em>earliest</em> of the candidate suppliers' boundaries, which is what bounds it: an answer
    /// older than every boundary cannot cover anything, so there is no reason to read it. Each pair is
    /// then judged against <em>its own</em> supplier's boundary in memory, because the boundaries
    /// differ per supplier and a single SQL comparison could only use one of them.</para>
    ///
    /// <para>A pair finalised as <c>NotSearched</c> appears in neither read, which is exactly its
    /// meaning: nothing looked, so it neither covers the pair nor blocks it, and the pair may be
    /// retried at once even while that run is still open.</para>
    ///
    /// <para><strong>The pending read answers a second question too</strong> (issue #1108): whether
    /// <em>any</em> work at all is in flight, for any supplier. A run still open with pending work is
    /// a collector still working, and the caller must not expire it to make room - so the pending
    /// read is not narrowed to the candidate suppliers. It is bounded all the same, by a run rather
    /// than by history.</para>
    /// </summary>
    private async Task<DueWork> DueWorkItemsAsync(
        NewPriceCollectorRun run,
        PriceCollectorDueWork onlyDue,
        CancellationToken cancellationToken)
    {
        var boundaries = onlyDue.CoverageBoundaryBySupplier;

        var candidates = run.WorkItems
            .Where(item => boundaries.ContainsKey(item.SupplierPresetId))
            .ToList();

        // Pending in an unexpired open run, read before anything is expired. A run with no stored
        // expiry - one of the runs #925 shipped the schema for - is one nothing expires
        // automatically, so its pending work still blocks.
        var pending = await _db.PriceCollectorWorkItems
            .AsNoTracking()
            .Where(item => item.Outcome == null
                && item.PriceCollectorRun!.Status == PriceCollectorRunStatus.Running
                && (item.PriceCollectorRun.ExpiresAtUtc == null
                    || item.PriceCollectorRun.ExpiresAtUtc > run.IssuedAtUtc))
            .Select(item => new
            {
                item.SupplierPresetId,
                item.MasterProductId,
                item.WaitingKey,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return new DueWork([], pending.Count > 0);
        }

        var candidateKeys = candidates
            .Select(PairKey.Of)
            .ToHashSet();

        var supplierIds = candidates
            .Select(item => item.SupplierPresetId)
            .Distinct()
            .ToList();

        var earliestBoundary = boundaries.Values.Min();

        var inFlight = pending
            .Select(item => new PairKey(
                item.SupplierPresetId,
                item.MasterProductId,
                item.WaitingKey))
            .Where(candidateKeys.Contains)
            .ToHashSet();

        // The latest answer each pair has, among the answers that could possibly still count.
        // "Answered" is any recorded outcome except NotSearched, which is the one outcome that says
        // nobody looked - the same distinction SupplierSearchDuePolicy.IsAnswered states.
        var answers = await _db.PriceCollectorWorkItems
            .AsNoTracking()
            .Where(item => supplierIds.Contains(item.SupplierPresetId)
                && item.Outcome != null
                && item.Outcome != PriceCollectorWorkItemOutcome.NotSearched
                && item.OutcomeAtUtc != null
                && item.OutcomeAtUtc >= earliestBoundary)
            .GroupBy(item => new
            {
                item.SupplierPresetId,
                item.MasterProductId,
                item.WaitingKey,
            })
            .Select(group => new
            {
                group.Key.SupplierPresetId,
                group.Key.MasterProductId,
                group.Key.WaitingKey,
                LatestAnsweredAtUtc = group.Max(item => item.OutcomeAtUtc),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var latestAnswered = answers
            .Select(entry => (
                Pair: new PairKey(entry.SupplierPresetId, entry.MasterProductId, entry.WaitingKey),
                entry.LatestAnsweredAtUtc))
            .Where(entry => candidateKeys.Contains(entry.Pair))
            .ToDictionary(entry => entry.Pair, entry => entry.LatestAnsweredAtUtc);

        return new DueWork(
            [.. candidates.Where(item =>
            {
                var pair = PairKey.Of(item);

                return SupplierSearchDuePolicy.IsPairDueAtBoundary(
                    boundaries[item.SupplierPresetId],
                    latestAnswered.TryGetValue(pair, out var answered) ? answered : null,
                    inFlight.Contains(pair));
            })],
            pending.Count > 0);
    }

    /// <summary>
    /// What the due read decided (issue #1108): the candidate pairs that are owed a search, in the
    /// order the work list published them, and whether any work at all is still in flight.
    /// </summary>
    /// <param name="WorkItems">The due pairs, before the budget is filled from them.</param>
    /// <param name="WorkIsInFlight">
    /// Whether a run that is still open and unexpired holds a pending work item - for any supplier,
    /// not only a candidate one. Issuing would expire that run and finalise its pending work as
    /// <c>NotSearched</c>, so a <c>due</c> call defers rather than take a live run away from the
    /// collector working on it.
    /// </param>
    private sealed record DueWork(
        IReadOnlyList<NewPriceCollectorWorkItem> WorkItems,
        bool WorkIsInFlight);

    /// <summary>
    /// One product-and-supplier pair, which is what a work item is about and what the due rule is
    /// decided per. The waiting key is compared with the record type's own default string equality,
    /// which is ordinal - the same comparison the work list's rotation orders it by.
    /// </summary>
    private sealed record PairKey(int SupplierPresetId, long? MasterProductId, string? WaitingKey)
    {
        internal static PairKey Of(NewPriceCollectorWorkItem item) =>
            new(item.SupplierPresetId, item.MasterProductId, item.WaitingKey);
    }

    /// <inheritdoc />
    public async Task<PriceCollectorWorkCursor?> LastIssuedWorkAsync(
        CancellationToken cancellationToken)
    {
        // The newest run's last work item: work is written in the order the work list publishes it,
        // so the highest key in the highest run is the product that run was issued up to. One row,
        // and no count - the next run needs where to start, not how much there was.
        var last = await _db.PriceCollectorWorkItems
            .AsNoTracking()
            .OrderByDescending(item => item.PriceCollectorRunId)
            .ThenByDescending(item => item.Id)
            .Select(item => new PriceCollectorWorkCursor(item.MasterProductId, item.WaitingKey))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return last;
    }
}
