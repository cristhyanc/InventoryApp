using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// What closing a collector run does to its work, wherever it is closed from (issue #937, parent
/// #817).
///
/// <para><strong>Completing a run and expiring one have the same effect on its searches, and that
/// is why this is one place rather than two.</strong> The issue states expiry as "with the same
/// effect": every search nobody answered becomes <see cref="PriceCollectorWorkItemOutcome.NotSearched"/>,
/// the per-outcome counts are written, and the run records how much it accounted for. The one thing
/// that differs is the status - <see cref="PriceCollectorRunStatus.Expired"/> says nobody reported,
/// while a completed run's status is derived from what its searches did
/// (<see cref="PriceCollectorRunPolicy.StatusFor"/>) - so the caller passes that in and nothing
/// else about the two paths diverges. A run expired by the work list of #924 and a run expired by
/// being touched here therefore leave exactly the same evidence.</para>
///
/// <para><strong>Closing removes nothing.</strong> No listing loses its latest accepted
/// observation, its last successful check or its match status, and no work item's own recorded
/// outcome is overwritten - the owner's constraint of 27 October 2026. What is written is the
/// absence of an answer, as an answer.</para>
///
/// <para>It does not save or commit: its caller owns the transaction, because closing a run is
/// always part of a larger operation - issuing the next run, or answering the request that touched
/// this one.</para>
/// </summary>
internal static class PriceCollectorRunClosure
{
    /// <summary>
    /// Closes <paramref name="run"/> at <paramref name="closedAtUtc"/> with
    /// <paramref name="status"/>, turning every unanswered search into
    /// <see cref="PriceCollectorWorkItemOutcome.NotSearched"/> and recording the counts, and answers
    /// what it recorded.
    ///
    /// <para><paramref name="status"/> is <see langword="null"/> for an ordinary completion, which
    /// derives it from the counts; an expiry passes
    /// <see cref="PriceCollectorRunStatus.Expired"/>.</para>
    /// </summary>
    public static async Task<PriceCollectorRunClosureResult> CloseAsync(
        AppDbContext db,
        PriceCollectorRun run,
        DateTime closedAtUtc,
        PriceCollectorRunStatus? status,
        CancellationToken cancellationToken)
    {
        var workItems = await db.PriceCollectorWorkItems
            .Where(item => item.PriceCollectorRunId == run.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var unanswered in workItems.Where(item => item.Outcome is null))
        {
            unanswered.Outcome = PriceCollectorWorkItemOutcome.NotSearched;
            unanswered.OutcomeAtUtc = closedAtUtc;
        }

        var counts = new PriceCollectorRunOutcomeCounts(
            workItems.Count(item => item.Outcome == PriceCollectorWorkItemOutcome.Complete),
            workItems.Count(item => item.Outcome == PriceCollectorWorkItemOutcome.Truncated),
            workItems.Count(item => item.Outcome == PriceCollectorWorkItemOutcome.NoMatches),
            workItems.Count(item => item.Outcome == PriceCollectorWorkItemOutcome.Failed),
            workItems.Count(item => item.Outcome == PriceCollectorWorkItemOutcome.NotSearched));

        var rows = await db.PriceCollectorResultRows
            .AsNoTracking()
            .Where(row => row.PriceCollectorRunId == run.Id)
            .Select(row => new { row.SupplierListingId, row.Outcome })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        run.CompleteSearchCount = counts.Complete;
        run.TruncatedSearchCount = counts.Truncated;
        run.NoMatchesSearchCount = counts.NoMatches;
        run.FailedSearchCount = counts.Failed;
        run.NotSearchedCount = counts.NotSearched;

        // Distinct listings, because two packs of one product are two rows about two listings and a
        // re-read of one listing is one listing checked however many rows mentioned it.
        run.ListingsCheckedCount = rows
            .Where(row => row.SupplierListingId is not null)
            .Select(row => row.SupplierListingId!.Value)
            .Distinct()
            .Count();

        run.ObservationsRecordedCount = rows.Count(
            row => row.Outcome == PriceCollectorRowOutcome.Accepted);

        // A refused row and a search that failed are both failures this run could not account for,
        // and they are added rather than merged into one of the two counts above for the reason
        // PriceCollectorRun states: a listing that was checked and refused and one that was never
        // reached are different, and subtraction would hide the difference.
        run.FailureCount =
            rows.Count(row => row.Outcome == PriceCollectorRowOutcome.Rejected) + counts.Failed;

        run.Status = status ?? PriceCollectorRunPolicy.StatusFor(counts);
        run.CompletedAtUtc = closedAtUtc;

        PriceCollectorRunPolicy.RequireConsistentRun(
            run.Status,
            run.StartedAtUtc,
            run.CompletedAtUtc,
            run.ListingsCheckedCount,
            run.ObservationsRecordedCount,
            run.FailureCount);

        return new PriceCollectorRunClosureResult(run.Status, counts);
    }
}

/// <summary>What closing one run recorded (issue #937): the status it ended in and its counts.</summary>
internal sealed record PriceCollectorRunClosureResult(
    PriceCollectorRunStatus Status,
    PriceCollectorRunOutcomeCounts Counts);
