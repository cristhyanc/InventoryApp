using Inventory.Application.PriceCatalogue;
using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

// How one supplier listing is named while a batch is being decided: the supplier master record and
// that supplier's own product id, which is the unique pair the catalogue keys a listing by. It is
// deliberately not the listing's database id - a listing a batch has just created has none until
// the batch is saved, and two rows of one batch can reach it.
using ListingKey = (int SupplierPresetId, string SupplierProductId);

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IPriceCollectorResultStore"/> (issue #937, parent
/// #817): the writes the collector's own results make.
///
/// <para><strong>Platform tables and nothing else.</strong> <c>SupplierListings</c>,
/// <c>SupplierPriceObservations</c>, <c>SupplierListingPendingObservations</c>,
/// <c>PriceCollectorResultRows</c>, <c>PriceCollectorRuns</c>, <c>PriceCollectorWorkItems</c> and
/// the matching evidence on <c>ProductMatchSuggestions</c> - every one of them about no business.
/// There is no <c>IgnoreQueryFilters</c> in this adapter, no raw SQL and no unscoped context: a
/// collector request's <em>denied</em> <c>BusinessScope</c> leaves <c>BusinessOwnershipEnforcer</c>
/// refusing every tenant-owned write exactly as it does elsewhere, and nothing here attempts one.
/// </para>
///
/// <para><strong>Each public method is one transaction, read and write together.</strong> Deciding
/// a row means reading the run, its work item, the listing and that listing's last accepted price,
/// and then writing a listing, an observation and the row's own recorded outcome. Splitting the
/// read from the write would let two submissions of one idempotency key both decide that the key
/// was free; here they cannot, and the unique index on
/// <c>(run, work item, supplier product id)</c> is the backstop underneath that if they somehow
/// did.</para>
///
/// <para><strong>Delivery order decides nothing.</strong> The collector decides how it splits its
/// searches into batches and in which order it sends them, and two work items of one run - one
/// issued for a master product, one for a waiting key since matched to it - can report the same
/// supplier listing. So the order rows arrive in says nothing about the order they were read in,
/// and three rules keep it from mattering:</para>
/// <list type="bullet">
/// <item><description><strong>Every row of a run is judged against the listing as the run found
/// it</strong> - the match status it held and the accepted price it held when the run started
/// (<see cref="PriceCollectorResultPolicy.StatusAtRunStart"/>). A reading of this run is never the
/// band for another reading of this run, and a row of this run that flags the listing does not turn
/// a later-delivered row of it into a held reading, so each row's outcome is the same whichever
/// order the run's rows arrive in and however they are split into batches.</description></item>
/// <item><description><strong>What a listing currently costs is a replay of the run's accepted
/// readings in observed order</strong>
/// (<see cref="SupplierListingPriceStatePolicy.StateAfterRun"/>): its latest accepted observation,
/// its last successful check and its last price change are recomputed from the run's accepted
/// readings after every batch, never moved by whichever row came last.</description></item>
/// <item><description><strong>The one reading a listing holds pending is the latest one
/// observed</strong> (<see cref="SupplierListingPriceStatePolicy.ReplacesCurrentReading"/>), and a
/// proposal's identity follows that reading.</description></item>
/// </list>
///
/// <para><strong>Every rule is the Domain's, and this class is the sequence.</strong>
/// <see cref="PriceCollectorResultPolicy"/> decides what becomes of a row,
/// <see cref="SupplierListingPolicy"/> and <see cref="SupplierPriceObservationPolicy"/> decide what
/// may be stored, <see cref="PriceCollectorWorkItemPolicy"/> decides what a search may be answered
/// with and what its absence proves, and <see cref="PriceCollectorRunPolicy"/> decides how a run
/// ended. What is here is the reading, the writing and the order they happen in.</para>
///
/// <para><strong>A row is refused, never half-stored.</strong> The checks run in a fixed order -
/// is this work item real, is it this run's, has this key already been answered, is it this
/// supplier's, is the payload storable, is the basis the supplier's, was it read inside the run
/// window and from the supplier's own site, and does the listing already belong to another product
/// - and the first that fails is the row's outcome. Nothing about the listing changes for a refused
/// row.</para>
/// </summary>
public sealed class EfPriceCollectorResultStore : IPriceCollectorResultStore
{
    private readonly AppDbContext _db;

    public EfPriceCollectorResultStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<PriceCollectorResultReceipt?> SubmitAsync(
        PriceCollectorResultBatch batch,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var run = await FindOpenRunAsync(batch.RunId, asOfUtc, transaction, cancellationToken)
            .ConfigureAwait(false);

        if (run is null)
        {
            return null;
        }

        var context = await LoadAsync(run, batch, cancellationToken).ConfigureAwait(false);

        List<DecidedRow> decided = [];

        foreach (var submission in batch.Rows)
        {
            decided.Add(Decide(run, submission, context, asOfUtc));
        }

        // One save for every listing, pending reading, observation, suggestion and recorded row the
        // batch produced, so the keys the second save needs are assigned together.
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // What each listing whose readings this batch accepted now currently costs, replayed from
        // every reading this run has accepted for it - the earlier batches' and this one's, which only
        // have their keys now - in the order they were observed.
        await ApplyCurrentPriceStateAsync(run, context, cancellationToken).ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Read after both saves, because a newly created listing and a newly recorded observation
        // only have their ids then - and a receipt that reported a zero would be a key nothing
        // could be looked up by.
        var results = decided.ConvertAll(row => new PriceCollectorRowResult(
            row.WorkItemId,
            row.SupplierProductId,
            row.Outcome,
            row.Reason,
            row.ListingId,
            row.ObservationId));

        return new PriceCollectorResultReceipt(
            run.Id,
            [.. results.Where(row => row.Outcome == PriceCollectorRowOutcome.Accepted)],
            [.. results.Where(row => row.Outcome == PriceCollectorRowOutcome.SentToReview)],
            [.. results.Where(row => row.Outcome == PriceCollectorRowOutcome.Pending)],
            [.. results.Where(row => row.Outcome == PriceCollectorRowOutcome.Rejected)]);
    }

    /// <summary>
    /// Writes the price-check state of every listing this batch accepted a reading for: a replay of
    /// all of this run's accepted readings of it, in observed order, from the price it held before
    /// the run (<see cref="SupplierListingPriceStatePolicy.StateAfterRun"/>). Reading the run's
    /// observations back rather than carrying state between batches is what makes the answer the
    /// same however the collector split and ordered them.
    /// </summary>
    private async Task ApplyCurrentPriceStateAsync(
        PriceCollectorRun run,
        SubmissionContext context,
        CancellationToken cancellationToken)
    {
        if (context.AcceptedListings.Count == 0)
        {
            return;
        }

        var listingIds = context.AcceptedListings
            .Select(key => context.Listings[key].Id)
            .ToList();

        var readings = await _db.SupplierPriceObservations
            .AsNoTracking()
            .Where(observation => observation.PriceCollectorRunId == run.Id
                && listingIds.Contains(observation.SupplierListingId))
            .Select(observation => new
            {
                observation.SupplierListingId,
                observation.Id,
                observation.ObservedAtUtc,
                observation.PackPrice,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byListing = readings.ToLookup(reading => reading.SupplierListingId);

        foreach (var key in context.AcceptedListings)
        {
            var listing = context.Listings[key];

            var state = SupplierListingPriceStatePolicy.StateAfterRun(
                context.RunStart[key].PackPrice,
                listing.LastPriceChangeAtUtc,
                [.. byListing[listing.Id].Select(reading => new AcceptedSupplierReading(
                    reading.Id,
                    reading.ObservedAtUtc,
                    reading.PackPrice))]);

            listing.LatestObservationId = state.LatestObservationId;
            listing.LastCheckedAtUtc = state.LastCheckedAtUtc;
            listing.LastPriceChangeAtUtc = state.LastPriceChangeAtUtc;

            SupplierListingPriceStatePolicy.RequireConsistentState(
                listing.LatestObservationId,
                listing.LastCheckedAtUtc,
                listing.LastPriceChangeAtUtc);
        }
    }

    /// <inheritdoc />
    public async Task<PriceCollectorSearchAnswerReceipt?> AnswerSearchAsync(
        PriceCollectorSearchAnswer answer,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(answer);

        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var run = await FindOpenRunAsync(answer.RunId, asOfUtc, transaction, cancellationToken)
            .ConfigureAwait(false);

        if (run is null)
        {
            return null;
        }

        var workItem = await _db.PriceCollectorWorkItems
            .FirstOrDefaultAsync(
                item => item.Id == answer.WorkItemId && item.PriceCollectorRunId == run.Id,
                cancellationToken)
            .ConfigureAwait(false);

        if (workItem is null)
        {
            return null;
        }

        if (workItem.Outcome is { } already)
        {
            // A retry whose response was lost is not a conflict: an identical answer changes
            // nothing and is reported as the answer that was recorded.
            var identical = already == answer.Outcome
                && workItem.FailureReason == answer.FailureReason
                && workItem.SubmittedRowCount == answer.SubmittedRowCount;

            if (!identical)
            {
                throw new DomainConflictException(
                    PriceCollectorWorkItemPolicy.AlreadyAnsweredDifferentlyMessage);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new PriceCollectorSearchAnswerReceipt(
                workItem.Id,
                already,
                workItem.FailureReason,
                workItem.SubmittedRowCount ?? 0,
                workItem.OutcomeAtUtc!.Value,
                ListingsRecordedNotFound: 0);
        }

        var reported = await _db.PriceCollectorResultRows
            .AsNoTracking()
            .Where(row => row.PriceCollectorRunId == run.Id
                && row.PriceCollectorWorkItemId == workItem.Id)
            .Select(row => row.SupplierProductId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // The collector's own total against what actually arrived. A lost batch leaves the search
        // unanswered rather than finishing it short, which is what stops an incomplete delivery
        // recording a listing as missing.
        if (reported.Count != answer.SubmittedRowCount)
        {
            throw new DomainConflictException(
                PriceCollectorWorkItemPolicy.SubmittedTotalMismatchMessage);
        }

        workItem.Outcome = answer.Outcome;
        workItem.OutcomeAtUtc = asOfUtc;
        workItem.FailureReason = answer.FailureReason;
        workItem.SubmittedRowCount = answer.SubmittedRowCount;

        PriceCollectorWorkItemPolicy.RequireConsistentWorkItem(
            workItem.MasterProductId,
            workItem.WaitingKey,
            workItem.Outcome);

        var notFound = PriceCollectorWorkItemPolicy.RecordsListingsNotFound(answer.Outcome)
            ? await RecordListingsNotFoundAsync(workItem, reported, asOfUtc, cancellationToken)
                .ConfigureAwait(false)
            : 0;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new PriceCollectorSearchAnswerReceipt(
            workItem.Id,
            answer.Outcome,
            answer.FailureReason,
            answer.SubmittedRowCount,
            asOfUtc,
            notFound);
    }

    /// <inheritdoc />
    public async Task<PriceCollectorRunCompletion?> CompleteRunAsync(
        long runId,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var run = await FindOpenRunAsync(runId, asOfUtc, transaction, cancellationToken)
            .ConfigureAwait(false);

        if (run is null)
        {
            return null;
        }

        var closure = await PriceCollectorRunClosure
            .CloseAsync(_db, run, asOfUtc, status: null, cancellationToken)
            .ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new PriceCollectorRunCompletion(
            run.Id,
            closure.Status,
            asOfUtc,
            closure.Counts,
            run.ListingsCheckedCount,
            run.ObservationsRecordedCount,
            run.FailureCount);
    }

    /// <summary>
    /// The run this request is about while it is still open, <see langword="null"/> when no run has
    /// that id - and an expiry, committed, when the run was overdue.
    ///
    /// <para>The rule itself is <see cref="PriceCollectorOpenRun"/>'s, shared with the matches
    /// endpoint of issue #941: every collector endpoint that writes asks the same question before it
    /// writes anything, and two copies of "closed, overdue or open" would be two answers to it. The
    /// transaction is this method's own, because the expiry it may commit is this request's.</para>
    /// </summary>
    private Task<PriceCollectorRun?> FindOpenRunAsync(
        long runId,
        DateTime asOfUtc,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken) =>
        PriceCollectorOpenRun.FindAsync(
            _db,
            runId,
            asOfUtc,
            transaction.CommitAsync,
            cancellationToken);

    /// <summary>
    /// Records that a complete search did not find the known listings it was looking for: every
    /// listing of this work item's product at this work item's supplier that no row of this run
    /// mentioned.
    ///
    /// <para>It is reached only for an outcome <see cref="PriceCollectorWorkItemPolicy.RecordsListingsNotFound"/>
    /// accepts, and only after the declared total matched the rows received - so "absent from the
    /// results" means absent from a search that was both complete and completely delivered. It
    /// writes one column and changes nothing else: the listing keeps its price, its check instant
    /// and its match status.</para>
    /// </summary>
    private async Task<int> RecordListingsNotFoundAsync(
        PriceCollectorWorkItem workItem,
        IReadOnlyCollection<string> reportedSupplierProductIds,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        var resolvedMasterProductId = workItem.MasterProductId
            ?? await ResolvedMasterProductIdAsync(workItem.WaitingKey, cancellationToken)
                .ConfigureAwait(false);

        var candidates = await _db.SupplierListings
            .Where(listing => listing.SupplierPresetId == workItem.SupplierPresetId)
            .Where(listing =>
                (resolvedMasterProductId != null && listing.MasterProductId == resolvedMasterProductId)
                || (workItem.WaitingKey != null && listing.WaitingKey == workItem.WaitingKey))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var missing = candidates
            .Where(listing => !reportedSupplierProductIds.Contains(
                listing.SupplierProductId,
                StringComparer.Ordinal))
            .ToList();

        foreach (var listing in missing)
        {
            listing.LastNotFoundAtUtc = asOfUtc;
            listing.UpdatedAtUtc = asOfUtc;
        }

        return missing.Count;
    }

    /// <summary>
    /// The master product a waiting key has been matched to since the work list was issued, or
    /// <see langword="null"/> while nothing has matched it.
    ///
    /// <para><strong>The match is read from the suggestion that applied it</strong>, which is the
    /// platform's own record of "this key is that product" and the thing #941's automatic matching
    /// and #928's Super Admin review both write. Reading it here is what makes a work item issued
    /// for a waiting key resolve to the master product it was matched to <em>earlier in the same
    /// run</em>: the work list is issued once and the matching happens while the collector is
    /// searching, so a row that arrives afterwards must be attributed to the product the key became
    /// rather than to the key it was.</para>
    ///
    /// <para>The newest applied or accepted suggestion wins, because an undone one is recorded as
    /// <c>Undone</c> rather than removed and must not go on resolving the key.</para>
    /// </summary>
    private async Task<long?> ResolvedMasterProductIdAsync(
        string? waitingKey,
        CancellationToken cancellationToken)
    {
        if (waitingKey is null)
        {
            return null;
        }

        return await _db.ProductMatchSuggestions
            .AsNoTracking()
            .Where(suggestion => suggestion.WaitingKey == waitingKey
                && suggestion.MasterProductId != null
                && (suggestion.Status == ProductMatchSuggestionStatus.AutoApplied
                    || suggestion.Status == ProductMatchSuggestionStatus.Accepted))
            .OrderByDescending(suggestion => suggestion.UpdatedAtUtc)
            .ThenByDescending(suggestion => suggestion.Id)
            .Select(suggestion => suggestion.MasterProductId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The pack price of a listing's latest accepted observation from <em>before</em> this run - the
    /// band every reading of this run is judged against.
    ///
    /// <para>Usually that is simply the listing's latest observation. Only when an earlier batch of
    /// this run has already moved it is the run's own reading skipped for the latest one an earlier
    /// run recorded: latest observed, and of two bearing the same instant the one recorded first,
    /// which is the order <see cref="SupplierListingPriceStatePolicy.StateAfterRun"/> keeps.</para>
    /// </summary>
    private async Task<decimal?> PriceBeforeRunAsync(
        PriceCollectorRun run,
        SupplierListing listing,
        CancellationToken cancellationToken)
    {
        if (listing.LatestObservationId is not { } latestId)
        {
            return null;
        }

        var latest = await _db.SupplierPriceObservations
            .AsNoTracking()
            .Where(observation => observation.Id == latestId)
            .Select(observation => new { observation.PriceCollectorRunId, observation.PackPrice })
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);

        if (latest.PriceCollectorRunId != run.Id)
        {
            return latest.PackPrice;
        }

        return await _db.SupplierPriceObservations
            .AsNoTracking()
            .Where(observation => observation.SupplierListingId == listing.Id
                && observation.PriceCollectorRunId != run.Id)
            .OrderByDescending(observation => observation.ObservedAtUtc)
            .ThenBy(observation => observation.Id)
            .Select(observation => (decimal?)observation.PackPrice)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Everything one batch needs, read once: the run's cited work items (and any work item of
    /// another run it cited, so that can be said rather than guessed), the outcomes already
    /// recorded for those keys, the listings those supplier product ids name, the status and the
    /// accepted price each held when the run started, the pending readings they hold,
    /// the supplier master records involved and the master product each waiting key has been
    /// matched to.
    /// </summary>
    private async Task<SubmissionContext> LoadAsync(
        PriceCollectorRun run,
        PriceCollectorResultBatch batch,
        CancellationToken cancellationToken)
    {
        var citedWorkItemIds = batch.Rows.Select(row => row.WorkItemId).Distinct().ToList();
        var citedSupplierIds = batch.Rows.Select(row => row.SupplierPresetId).Distinct().ToList();

        var citedSupplierProductIds = batch.Rows
            .Select(row => (row.SupplierProductId ?? string.Empty).Trim())
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var workItems = await _db.PriceCollectorWorkItems
            .Where(item => citedWorkItemIds.Contains(item.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var alreadyRecorded = await _db.PriceCollectorResultRows
            .AsNoTracking()
            .Where(row => row.PriceCollectorRunId == run.Id
                && citedWorkItemIds.Contains(row.PriceCollectorWorkItemId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var listings = await _db.SupplierListings
            .Where(listing => citedSupplierIds.Contains(listing.SupplierPresetId)
                && citedSupplierProductIds.Contains(listing.SupplierProductId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var listingIds = listings.ConvertAll(listing => listing.Id);

        // The listings this run has already sent to review, which is how the status each one held
        // when the run started is recovered (PriceCollectorResultPolicy.StatusAtRunStart).
        var sentToReviewThisRun = (await _db.PriceCollectorResultRows
            .AsNoTracking()
            .Where(row => row.PriceCollectorRunId == run.Id
                && row.Outcome == PriceCollectorRowOutcome.SentToReview
                && row.SupplierListingId != null
                && listingIds.Contains(row.SupplierListingId.Value))
            .Select(row => row.SupplierListingId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToHashSet();

        // Keyed by the listing's own key rather than its database id, because a listing this batch
        // is about to create needs an entry too, and has no id until the batch is saved.
        Dictionary<ListingKey, RunStartState> runStart = [];
        Dictionary<long, ListingKey> listingKeys = [];

        foreach (var listing in listings)
        {
            var key = (listing.SupplierPresetId, listing.SupplierProductId);
            listingKeys[listing.Id] = key;

            runStart[key] = new RunStartState(
                PriceCollectorResultPolicy.StatusAtRunStart(
                    listing.MatchStatus,
                    sentToReviewThisRun.Contains(listing.Id)),
                await PriceBeforeRunAsync(run, listing, cancellationToken).ConfigureAwait(false));
        }

        var pending = await _db.SupplierListingPendingObservations
            .Where(reading => listingIds.Contains(reading.SupplierListingId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var suppliers = await _db.SupplierPresets
            .AsNoTracking()
            .Where(preset => citedSupplierIds.Contains(preset.Id))
            .Select(preset => new SupplierFacts(preset.Id, preset.Url, preset.PricesIncludeGst))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<string, long?> resolvedKeys = new(StringComparer.Ordinal);

        foreach (var waitingKey in workItems
            .Where(item => item.WaitingKey is not null)
            .Select(item => item.WaitingKey!)
            .Distinct(StringComparer.Ordinal))
        {
            resolvedKeys[waitingKey] = await ResolvedMasterProductIdAsync(waitingKey, cancellationToken)
                .ConfigureAwait(false);
        }

        var waitingKeys = resolvedKeys.Keys.ToList();

        var existingSuggestionPairs = await _db.ProductMatchSuggestions
            .AsNoTracking()
            .Where(suggestion => suggestion.MasterProductId != null
                && waitingKeys.Contains(suggestion.WaitingKey))
            .Select(suggestion => new { suggestion.WaitingKey, suggestion.MasterProductId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new SubmissionContext(
            workItems.ToDictionary(item => item.Id),
            alreadyRecorded.ToDictionary(
                row => (row.PriceCollectorWorkItemId, row.SupplierProductId),
                row => row),
            listings.ToDictionary(listing => (listing.SupplierPresetId, listing.SupplierProductId)),
            runStart,
            pending.ToDictionary(reading => listingKeys[reading.SupplierListingId]),
            suppliers.ToDictionary(supplier => supplier.SupplierPresetId),
            resolvedKeys,
            [.. existingSuggestionPairs.Select(pair => (pair.WaitingKey, pair.MasterProductId!.Value))]);
    }

    /// <summary>
    /// Decides one submitted row and writes what the decision implies, in the fixed order the class
    /// summary states. It adds entities to the context and never saves: the caller owns the
    /// transaction and the two saves.
    ///
    /// <para>Each stage of the fixed order - resolving the work item, validating the row's content
    /// against it, resolving the listing it names, and applying what the Domain decided - is its own
    /// method below, in that order, so the sequence reads the way the class summary states it.</para>
    /// </summary>
    private DecidedRow Decide(
        PriceCollectorRun run,
        PriceCollectorResultRowSubmission submission,
        SubmissionContext context,
        DateTime asOfUtc)
    {
        string supplierProductId;

        try
        {
            supplierProductId = SupplierListingPolicy.NormaliseSupplierProductId(
                submission.SupplierProductId);
        }
        catch (DomainValidationException refusal)
        {
            // Unrecordable: the supplier product id is a third of the idempotency key, so a row
            // without one has no key to be remembered by. It is refused the same way every time it
            // is sent, which is what idempotency would have given it anyway.
            return Refused(submission.WorkItemId, string.Empty, refusal.Message);
        }

        var resolved = ResolveWorkItem(run, submission, context, supplierProductId);

        if (resolved.Refusal is not null)
        {
            return resolved.Refusal;
        }

        var workItem = resolved.WorkItem!;
        var validated = ValidateContent(run, workItem, submission, context, supplierProductId, asOfUtc);

        if (validated.Refusal is not null)
        {
            return validated.Refusal;
        }

        return DecideForValidatedRow(
            run,
            workItem,
            supplierProductId,
            validated.Observed!,
            context,
            asOfUtc);
    }

    /// <summary>
    /// The first two checks of the fixed order - is this work item real and is it this run's - and
    /// the idempotency and in-batch checks that follow them: has this key already been answered, has
    /// its search already been answered some other way, and has this batch already claimed it.
    /// </summary>
    private static WorkItemResolution ResolveWorkItem(
        PriceCollectorRun run,
        PriceCollectorResultRowSubmission submission,
        SubmissionContext context,
        string supplierProductId)
    {
        if (!context.WorkItems.TryGetValue(submission.WorkItemId, out var workItem))
        {
            return new WorkItemResolution(null, Refused(
                submission.WorkItemId,
                supplierProductId,
                PriceCollectorResultPolicy.UnknownWorkItemReason));
        }

        if (workItem.PriceCollectorRunId != run.Id)
        {
            return new WorkItemResolution(null, Refused(
                submission.WorkItemId,
                supplierProductId,
                PriceCollectorResultPolicy.WorkItemFromAnotherRunReason));
        }

        var key = (workItem.Id, supplierProductId);

        if (context.AlreadyRecorded.TryGetValue(key, out var prior))
        {
            // The earlier outcome, verbatim, and nothing written. The reason is the stored one
            // rather than a re-derived one, so a repeat cannot answer differently because the
            // catalogue moved underneath it.
            return new WorkItemResolution(null, new DecidedRow(
                workItem.Id,
                supplierProductId,
                prior.Outcome,
                prior.Reason,
                Listing: null,
                Observation: null,
                prior.SupplierListingId,
                prior.SupplierPriceObservationId));
        }

        if (workItem.Outcome is not null)
        {
            return new WorkItemResolution(null, Refused(
                workItem.Id,
                supplierProductId,
                PriceCollectorResultPolicy.SearchAlreadyAnsweredReason));
        }

        if (!context.ClaimedInThisBatch.Add(key))
        {
            return new WorkItemResolution(null, Refused(
                workItem.Id,
                supplierProductId,
                PriceCollectorResultPolicy.DuplicateRowInBatchReason));
        }

        return new WorkItemResolution(workItem, null);
    }

    /// <summary>
    /// The content checks of the fixed order: is it this supplier's, is the payload storable, is the
    /// basis the supplier's, and was it read inside the run window and from the supplier's own site.
    /// </summary>
    private ContentValidation ValidateContent(
        PriceCollectorRun run,
        PriceCollectorWorkItem workItem,
        PriceCollectorResultRowSubmission submission,
        SubmissionContext context,
        string supplierProductId,
        DateTime asOfUtc)
    {
        if (submission.SupplierPresetId != workItem.SupplierPresetId)
        {
            return new ContentValidation(null, Record(
                workItem,
                supplierProductId,
                PriceCollectorRowOutcome.Rejected,
                PriceCollectorResultPolicy.SupplierDiffersFromWorkItemReason,
                listing: null,
                observation: null,
                asOfUtc));
        }

        ObservedRow observed;

        try
        {
            observed = ObservedRow.From(submission);
        }
        catch (DomainValidationException refusal)
        {
            return new ContentValidation(null, Record(
                workItem,
                supplierProductId,
                PriceCollectorRowOutcome.Rejected,
                refusal.Message,
                listing: null,
                observation: null,
                asOfUtc));
        }

        var supplier = context.Suppliers[workItem.SupplierPresetId];

        if (observed.PriceBasis != supplier.PriceBasis)
        {
            return new ContentValidation(null, Record(
                workItem,
                supplierProductId,
                PriceCollectorRowOutcome.Rejected,
                PriceCollectorResultPolicy.PriceBasisDiffersFromSupplierReason,
                listing: null,
                observation: null,
                asOfUtc));
        }

        if (!PriceCollectorResultPolicy.IsObservedWithinRun(
            observed.ObservedAtUtc,
            run.StartedAtUtc,
            asOfUtc))
        {
            return new ContentValidation(null, Record(
                workItem,
                supplierProductId,
                PriceCollectorRowOutcome.Rejected,
                observed.ObservedAtUtc < run.StartedAtUtc
                    ? PriceCollectorResultPolicy.ObservedBeforeRunStartedReason
                    : PriceCollectorResultPolicy.ObservedInFutureReason,
                listing: null,
                observation: null,
                asOfUtc));
        }

        if (!PriceCollectorResultPolicy.SourceLinkMatchesSupplier(supplier.SiteUrl, observed.SourceUrl))
        {
            return new ContentValidation(null, Record(
                workItem,
                supplierProductId,
                PriceCollectorRowOutcome.Rejected,
                PriceCollectorResultPolicy.SourceLinkHostMismatchReason,
                listing: null,
                observation: null,
                asOfUtc));
        }

        return new ContentValidation(observed, null);
    }

    /// <summary>
    /// The last check of the fixed order - does the listing already belong to another product - and,
    /// once a row has passed every check, what the Domain decides it implies.
    /// </summary>
    private DecidedRow DecideForValidatedRow(
        PriceCollectorRun run,
        PriceCollectorWorkItem workItem,
        string supplierProductId,
        ObservedRow observed,
        SubmissionContext context,
        DateTime asOfUtc)
    {
        var resolvedMasterProductId = workItem.MasterProductId
            ?? (workItem.WaitingKey is { } ownKey ? context.ResolvedWaitingKeys[ownKey] : null);

        context.Listings.TryGetValue(
            (workItem.SupplierPresetId, supplierProductId),
            out var listing);

        if (listing is not null
            && !BelongsToThisProduct(listing, resolvedMasterProductId, workItem.WaitingKey))
        {
            // The one refusal that also writes something else. A known master product's listing
            // turning up under a waiting key's search is evidence about that key, and #928's
            // reviewer is the only one who may act on it - the link itself is never changed here.
            if (workItem.WaitingKey is { } evidenceKey && listing.MasterProductId is { } otherMaster)
            {
                RecordMatchingEvidence(context, evidenceKey, otherMaster, listing, asOfUtc);
            }

            return Record(
                workItem,
                supplierProductId,
                PriceCollectorRowOutcome.Rejected,
                PriceCollectorResultPolicy.ListingBelongsToAnotherProductReason,
                listing,
                observation: null,
                asOfUtc);
        }

        return ApplyDecision(
            run,
            workItem,
            listing,
            resolvedMasterProductId,
            supplierProductId,
            observed,
            context,
            asOfUtc);
    }

    /// <summary>
    /// What the Domain's decision for one validated row implies: the listing it creates or updates,
    /// the observation it accepts or the pending reading it holds instead, and the row's own outcome.
    ///
    /// <para><strong>The row is decided against the listing as this run found it</strong> - the
    /// status it held and the accepted price it held when the run started - and never against what
    /// another row of this run did to it. Two searches of one listing in one run, delivered in either
    /// order and in one batch or several, therefore get the same outcomes: neither reading is the
    /// other's band, and one being sent to review does not turn the other into a held reading. A
    /// listing this batch creates starts as the proposal it becomes.</para>
    /// </summary>
    private DecidedRow ApplyDecision(
        PriceCollectorRun run,
        PriceCollectorWorkItem workItem,
        SupplierListing? listing,
        long? resolvedMasterProductId,
        string supplierProductId,
        ObservedRow observed,
        SubmissionContext context,
        DateTime asOfUtc)
    {
        var listingKey = (workItem.SupplierPresetId, supplierProductId);
        var start = context.RunStart.GetValueOrDefault(listingKey);

        var decision = listing is null
            ? PriceCollectorResultPolicy.DecideForNewListing()
            : PriceCollectorResultPolicy.DecideForKnownListing(
                start!.Status,
                IdentityOf(listing),
                observed.Identity,
                observed.PackPrice,
                start.PackPrice);

        if (listing is null)
        {
            listing = NewListing(workItem, resolvedMasterProductId, supplierProductId, observed, asOfUtc);
            _db.SupplierListings.Add(listing);
            context.Listings[listingKey] = listing;
            context.RunStart[listingKey] = new RunStartState(SupplierListingMatchStatus.Proposed, PackPrice: null);
        }
        else
        {
            UpdateListing(listing, observed, asOfUtc);
        }

        if (decision.NewMatchStatus is { } newStatus)
        {
            listing.MatchStatus = newStatus;
        }

        var observation = decision.RecordsAcceptedObservation
            ? AcceptObservation(context, listingKey, listing, run, observed, asOfUtc)
            : null;

        // A proposal's identity is the identity of the reading it holds, which is the latest one
        // observed - not the last one delivered. A confirmed, flagged or refused listing's identity
        // is never rewritten by a reading (see UpdateListing).
        if (decision.StoresPendingObservation
            && StorePending(context, listingKey, listing, workItem, run, observed, decision.Reason!, asOfUtc)
            && context.RunStart[listingKey].Status == SupplierListingMatchStatus.Proposed)
        {
            ApplyIdentity(listing, observed);
        }

        return Record(
            workItem,
            supplierProductId,
            decision.Outcome,
            decision.Reason,
            listing,
            observation,
            asOfUtc);
    }

    /// <summary>
    /// Stores one accepted reading as the append-only price it is.
    ///
    /// <para><strong>Whether it is what the listing currently costs is decided after the
    /// save</strong>, by replaying every reading this run has accepted for the listing in observed
    /// order (<see cref="ApplyCurrentPriceStateAsync"/>): an earlier reading delivered later is kept
    /// as history and never moves the current price, the last check or the last change
    /// backwards.</para>
    /// </summary>
    private SupplierPriceObservation AcceptObservation(
        SubmissionContext context,
        ListingKey listingKey,
        SupplierListing listing,
        PriceCollectorRun run,
        ObservedRow observed,
        DateTime asOfUtc)
    {
        var observation = new SupplierPriceObservation
        {
            SupplierListing = listing,
            PriceCollectorRunId = run.Id,
            PackPrice = observed.PackPrice,
            RegularPackPrice = observed.RegularPackPrice,
            Currency = observed.Currency,
            PriceBasis = observed.PriceBasis,
            SupplierGstRate = observed.SupplierGstRate,
            IsPromotion = observed.IsPromotion,
            PromotionEndsAtUtc = observed.PromotionEndsAtUtc,
            StockStatus = observed.StockStatus,
            ObservedAtUtc = observed.ObservedAtUtc,
            SourceReference = observed.SourceReference,
            SourceUrl = observed.SourceUrl,
            CreatedAtUtc = asOfUtc,
        };

        _db.SupplierPriceObservations.Add(observation);
        context.AcceptedListings.Add(listingKey);

        // An accepted price settles whatever disagreement an earlier run left waiting on this
        // listing. A reading this run held is left alone: it was held because of this run's
        // evidence, and whether it is still held must not depend on which of the run's rows arrived
        // first.
        if (context.Pending.TryGetValue(listingKey, out var resolved)
            && resolved.PriceCollectorRunId != run.Id)
        {
            context.Pending.Remove(listingKey);
            _db.SupplierListingPendingObservations.Remove(resolved);
        }

        return observation;
    }

    /// <summary>The outcome of <see cref="ResolveWorkItem"/>: a work item to continue with, or a decided refusal.</summary>
    private sealed record WorkItemResolution(PriceCollectorWorkItem? WorkItem, DecidedRow? Refusal);

    /// <summary>The outcome of <see cref="ValidateContent"/>: a storable row to continue with, or a decided refusal.</summary>
    private sealed record ContentValidation(ObservedRow? Observed, DecidedRow? Refusal);

    /// <summary>
    /// Whether <paramref name="listing"/> is a listing of the product this work item is about: the
    /// master product the work item names or the key resolved to, or the very key the work item was
    /// issued for.
    ///
    /// <para>The second half is what the issue's same-run resolution criterion needs. A listing
    /// still held under the waiting key is this product's listing even once the key has been
    /// matched - moving it to the master product is #941's and #928's, not a result's - and a
    /// listing already under that master product is this product's listing too, which is exactly
    /// the case that must not be refused as belonging to another product.</para>
    /// </summary>
    private static bool BelongsToThisProduct(
        SupplierListing listing,
        long? resolvedMasterProductId,
        string? waitingKey) =>
        (resolvedMasterProductId is { } master && listing.MasterProductId == master)
        || (waitingKey is not null
            && string.Equals(listing.WaitingKey, waitingKey, StringComparison.Ordinal));

    private static SupplierListingIdentity IdentityOf(SupplierListing listing) =>
        new(
            listing.Variant,
            listing.SizeValue,
            listing.SizeUnit,
            listing.Barcode,
            listing.BarcodeScope,
            listing.UnitsPerPack);

    private static SupplierListing NewListing(
        PriceCollectorWorkItem workItem,
        long? resolvedMasterProductId,
        string supplierProductId,
        ObservedRow observed,
        DateTime asOfUtc)
    {
        // Attached to the product its work item is about and to nothing else: the master product
        // the work item names, the master product its waiting key has been matched to since, or -
        // while nothing has matched it - the waiting key itself. Nothing about the row decides
        // this.
        var listing = new SupplierListing
        {
            SupplierPresetId = workItem.SupplierPresetId,
            MasterProductId = resolvedMasterProductId,
            WaitingKey = resolvedMasterProductId is null ? workItem.WaitingKey : null,
            OriginWaitingKey = workItem.WaitingKey,
            SupplierProductId = supplierProductId,
            Url = observed.SourceUrl,
            Title = observed.Title,
            Variant = observed.Identity.Variant,
            SizeValue = observed.Identity.SizeValue,
            SizeUnit = observed.Identity.SizeUnit,
            UnitsPerPack = observed.Identity.UnitsPerPack,
            Barcode = observed.Identity.Barcode,
            BarcodeScope = observed.Identity.BarcodeScope,
            MatchStatus = SupplierListingMatchStatus.Proposed,
            CreatedAtUtc = asOfUtc,
            UpdatedAtUtc = asOfUtc,
        };

        SupplierListingPolicy.RequireExactlyOneOwner(listing.MasterProductId, listing.WaitingKey);

        return listing;
    }

    /// <summary>
    /// Updates what a rediscovery may change about a listing: its last-seen instant and the
    /// evidence - the supplier's own wording and the page it is on.
    ///
    /// <para><strong>Identity is not touched here.</strong> It is rewritten only for a listing that
    /// was still a proposal when the run started, and only to the identity of the reading it now
    /// holds (<see cref="ApplyIdentity"/>). A confirmed, flagged or refused listing's variant, size,
    /// barcode and pack count are what somebody decided about, and overwriting them would quietly
    /// resolve the disagreement that sent the listing to review in the first place; the reading is
    /// kept beside it as a pending observation instead, where #928 can compare the two.</para>
    /// </summary>
    private static void UpdateListing(SupplierListing listing, ObservedRow observed, DateTime asOfUtc)
    {
        listing.Title = observed.Title ?? listing.Title;
        listing.Url = observed.SourceUrl ?? listing.Url;
        listing.UpdatedAtUtc = asOfUtc;
    }

    /// <summary>A proposal's identity, set to that of the reading it holds.</summary>
    private static void ApplyIdentity(SupplierListing listing, ObservedRow observed)
    {
        listing.Variant = observed.Identity.Variant;
        listing.SizeValue = observed.Identity.SizeValue;
        listing.SizeUnit = observed.Identity.SizeUnit;
        listing.UnitsPerPack = observed.Identity.UnitsPerPack;
        listing.Barcode = observed.Identity.Barcode;
        listing.BarcodeScope = observed.Identity.BarcodeScope;
    }

    /// <summary>
    /// Keeps the reading as this listing's pending observation, replacing whatever was pending for
    /// it, and says whether it did. One row per listing, which the unique index holds.
    ///
    /// <para><strong>The reading is found by the listing's own key - its supplier and that
    /// supplier's product id - and never by its database id</strong>, which a listing this batch
    /// has just created does not have until the batch is saved. Two searches of one run can reach
    /// one listing: a work item issued for a master product and one issued for a waiting key since
    /// matched to that product can report the same supplier product id in a single batch. Keyed by
    /// the database id, the second row would have added a second reading against the same unsaved
    /// listing, the unique index would have refused the save, and the whole batch - every unrelated
    /// row in it included - would have been rolled back.</para>
    ///
    /// <para><strong>And only a later reading replaces the one being held</strong>
    /// (<see cref="SupplierListingPriceStatePolicy.ReplacesCurrentReading"/>), for the same reason
    /// an accepted price is chronological: the reading #928's reviewer is shown must be the latest
    /// one read, not the last one delivered. The row is still answered and still recorded either
    /// way, and the listing has already been flagged by the caller - what an earlier reading does
    /// not do is overwrite a later one.</para>
    /// </summary>
    private bool StorePending(
        SubmissionContext context,
        ListingKey listingKey,
        SupplierListing listing,
        PriceCollectorWorkItem workItem,
        PriceCollectorRun run,
        ObservedRow observed,
        string reason,
        DateTime asOfUtc)
    {
        context.Pending.TryGetValue(listingKey, out var reading);

        if (reading is null)
        {
            reading = new SupplierListingPendingObservation { SupplierListing = listing };

            _db.SupplierListingPendingObservations.Add(reading);
            context.Pending[listingKey] = reading;
        }
        else if (!SupplierListingPriceStatePolicy.ReplacesCurrentReading(
            reading.ObservedAtUtc,
            observed.ObservedAtUtc))
        {
            return false;
        }

        reading.PriceCollectorRunId = run.Id;
        reading.PriceCollectorWorkItemId = workItem.Id;
        reading.Reason = reason;
        reading.PackPrice = observed.PackPrice;
        reading.RegularPackPrice = observed.RegularPackPrice;
        reading.Currency = observed.Currency;
        reading.PriceBasis = observed.PriceBasis;
        reading.SupplierGstRate = observed.SupplierGstRate;
        reading.IsPromotion = observed.IsPromotion;
        reading.PromotionEndsAtUtc = observed.PromotionEndsAtUtc;
        reading.StockStatus = observed.StockStatus;
        reading.ObservedAtUtc = observed.ObservedAtUtc;
        reading.SourceReference = observed.SourceReference;
        reading.SourceUrl = observed.SourceUrl;
        reading.Variant = observed.Identity.Variant;
        reading.SizeValue = observed.Identity.SizeValue;
        reading.SizeUnit = observed.Identity.SizeUnit;
        reading.Barcode = observed.Identity.Barcode;
        reading.BarcodeScope = observed.Identity.BarcodeScope;
        reading.UnitsPerPack = observed.Identity.UnitsPerPack;
        reading.Title = observed.Title;
        reading.RecordedAtUtc = asOfUtc;

        return true;
    }

    /// <summary>
    /// Records that a search for one waiting key found a listing of another master product, as a
    /// tentative match suggestion for #928's reviewer.
    ///
    /// <para>Only when that pair has no suggestion at all. A pair somebody rejected or undid is
    /// kept forever precisely so it is never proposed again
    /// (<see cref="ProductMatchSuggestionStatus.Rejected"/>), and re-recording the same evidence
    /// every run would bury the reviewer in rows about one refusal.</para>
    /// </summary>
    private void RecordMatchingEvidence(
        SubmissionContext context,
        string waitingKey,
        long masterProductId,
        SupplierListing listing,
        DateTime asOfUtc)
    {
        if (!context.SuggestionPairs.Add((waitingKey, masterProductId)))
        {
            return;
        }

        _db.ProductMatchSuggestions.Add(new ProductMatchSuggestion
        {
            WaitingKey = waitingKey,
            MasterProductId = masterProductId,
            Confidence = 0m,
            Reason = EvidenceReason(listing),
            CollectorRun = EvidenceCollectorRun,
            Status = ProductMatchSuggestionStatus.Tentative,
            CreatedAtUtc = asOfUtc,
            UpdatedAtUtc = asOfUtc,
        });
    }

    /// <summary>
    /// What the evidence row says. It names the listing and the supplier's own product id - the two
    /// things a reviewer needs in order to look at the same page the search did - and no business,
    /// product name or price, because a match suggestion is about a folded description and must
    /// stay that way.
    /// </summary>
    private static string EvidenceReason(SupplierListing listing) =>
        "A search for this key found supplier listing "
            + listing.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " (supplier product id "
            + listing.SupplierProductId
            + "), which is already linked to this master product. The link was not changed.";

    /// <summary>
    /// The collector-run label this application writes on evidence it records itself. Opaque text
    /// that nothing parses; it says the row came from the results endpoint rather than from a
    /// matcher's own proposal.
    /// </summary>
    private const string EvidenceCollectorRun = "price-collector-results";

    /// <summary>A refused row that is not recorded, because it has no idempotency key to record it under.</summary>
    private static DecidedRow Refused(long workItemId, string supplierProductId, string reason) =>
        new(
            workItemId,
            supplierProductId,
            PriceCollectorRowOutcome.Rejected,
            reason,
            Listing: null,
            Observation: null,
            RecordedListingId: null,
            RecordedObservationId: null);

    /// <summary>
    /// Records the row's outcome under its idempotency key and answers it, so a repeat of the same
    /// key is answered with this decision rather than decided again.
    /// </summary>
    private DecidedRow Record(
        PriceCollectorWorkItem workItem,
        string supplierProductId,
        PriceCollectorRowOutcome outcome,
        string? reason,
        SupplierListing? listing,
        SupplierPriceObservation? observation,
        DateTime asOfUtc)
    {
        _db.PriceCollectorResultRows.Add(new PriceCollectorResultRow
        {
            PriceCollectorRunId = workItem.PriceCollectorRunId,
            PriceCollectorWorkItemId = workItem.Id,
            SupplierProductId = supplierProductId,
            Outcome = outcome,
            Reason = reason,
            SupplierListing = listing,
            SupplierPriceObservation = observation,
            SubmittedAtUtc = asOfUtc,
        });

        return new DecidedRow(
            workItem.Id,
            supplierProductId,
            outcome,
            reason,
            listing,
            observation,
            RecordedListingId: null,
            RecordedObservationId: null);
    }

    /// <summary>
    /// One decided row before the save that gives its listing and its observation their keys.
    ///
    /// <para>It holds the entities rather than their ids precisely because a newly created listing
    /// has no id until the batch is saved, and a receipt reporting a zero would be a key nothing
    /// could be looked up by. A row answered from an <em>earlier</em> submission has no entities
    /// and carries the stored ids instead.</para>
    /// </summary>
    private sealed record DecidedRow(
        long WorkItemId,
        string SupplierProductId,
        PriceCollectorRowOutcome Outcome,
        string? Reason,
        SupplierListing? Listing,
        SupplierPriceObservation? Observation,
        long? RecordedListingId,
        long? RecordedObservationId)
    {
        public long? ListingId => Listing?.Id ?? RecordedListingId;

        public long? ObservationId => Observation?.Id ?? RecordedObservationId;
    }

    /// <summary>
    /// One supplier master record as a submitted row is checked against it: where its pages live,
    /// and what its published prices mean.
    /// </summary>
    private sealed record SupplierFacts(int SupplierPresetId, string? SiteUrl, bool PricesIncludeGst)
    {
        /// <summary>
        /// The basis every observation of this supplier is stored on - the record's own, copied onto
        /// the reading as it was observed (issue #925), never read back through the supplier later.
        /// </summary>
        public SupplierPriceBasis PriceBasis => PricesIncludeGst
            ? SupplierPriceBasis.InclusiveOfGst
            : SupplierPriceBasis.ExclusiveOfGst;
    }

    /// <summary>
    /// One submitted row once the Domain has said what of it may be stored: the identity it
    /// describes and the price it reports, each normalised by the policy that owns it.
    /// </summary>
    private sealed record ObservedRow(
        SupplierListingIdentity Identity,
        string? Title,
        decimal PackPrice,
        decimal? RegularPackPrice,
        string Currency,
        SupplierPriceBasis PriceBasis,
        decimal? SupplierGstRate,
        bool IsPromotion,
        DateTime? PromotionEndsAtUtc,
        SupplierStockStatus StockStatus,
        DateTime ObservedAtUtc,
        string? SourceReference,
        string? SourceUrl)
    {
        /// <exception cref="DomainValidationException">
        /// Something in the row is not a value this catalogue stores. Nothing is written.
        /// </exception>
        public static ObservedRow From(PriceCollectorResultRowSubmission submission)
        {
            SupplierPriceObservationPolicy.RequireDeclaredVocabulary(
                submission.PriceBasis,
                submission.StockStatus);
            SupplierPriceObservationPolicy.RequireConsistentPromotion(
                submission.IsPromotion,
                submission.PromotionEndsAtUtc);

            var (sizeValue, sizeUnit) = SupplierListingPolicy.NormaliseIndividualSize(
                submission.SizeValue,
                submission.SizeUnit);
            var (barcode, barcodeScope) = SupplierListingPolicy.NormaliseBarcode(
                submission.Barcode,
                submission.BarcodeScope);

            return new ObservedRow(
                new SupplierListingIdentity(
                    SupplierListingPolicy.NormaliseVariant(submission.Variant),
                    sizeValue,
                    sizeUnit,
                    barcode,
                    barcodeScope,
                    SupplierListingPolicy.NormaliseUnitsPerPack(submission.PackQuantity)),
                SupplierListingPolicy.NormaliseTitle(submission.Title),
                SupplierPriceObservationPolicy.RequirePackPrice(submission.PackPrice),
                SupplierPriceObservationPolicy.NormaliseRegularPrice(submission.RegularPackPrice),
                SupplierPriceObservationPolicy.NormaliseCurrency(submission.Currency),
                submission.PriceBasis,
                SupplierPriceObservationPolicy.NormaliseGstRate(submission.SupplierGstRate),
                submission.IsPromotion,
                submission.PromotionEndsAtUtc,
                submission.StockStatus,
                submission.ObservedAtUtc,
                SupplierPriceObservationPolicy.NormaliseSourceReference(submission.SourceReference),
                SupplierPriceObservationPolicy.NormaliseSourceUrl(submission.SourceUrl));
        }
    }

    /// <summary>
    /// What one listing was when the current run started: the match status it held, and the pack
    /// price of the latest accepted observation it held from before the run - the band every row of
    /// the run is judged against, or <see langword="null"/> while it had no accepted price.
    /// </summary>
    private sealed record RunStartState(SupplierListingMatchStatus Status, decimal? PackPrice);

    /// <summary>Everything one batch is decided against, read once before any of it is decided.</summary>
    private sealed record SubmissionContext(
        Dictionary<long, PriceCollectorWorkItem> WorkItems,
        Dictionary<(long WorkItemId, string SupplierProductId), PriceCollectorResultRow> AlreadyRecorded,
        Dictionary<ListingKey, SupplierListing> Listings,
        Dictionary<ListingKey, RunStartState> RunStart,
        Dictionary<ListingKey, SupplierListingPendingObservation> Pending,
        Dictionary<int, SupplierFacts> Suppliers,
        Dictionary<string, long?> ResolvedWaitingKeys,
        HashSet<(string WaitingKey, long MasterProductId)> SuggestionPairs)
    {
        /// <summary>
        /// The keys this batch has already claimed. A repeat across batches is answered with the
        /// first outcome; a repeat inside one batch has no first outcome to answer with, so it is
        /// refused rather than letting ordering decide which of the two was stored.
        /// </summary>
        public HashSet<(long WorkItemId, string SupplierProductId)> ClaimedInThisBatch { get; } = [];

        /// <summary>
        /// The listings this batch accepted a reading for, whose current price is replayed after
        /// the save.
        /// </summary>
        public HashSet<ListingKey> AcceptedListings { get; } = [];
    }
}
