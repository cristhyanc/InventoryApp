using Inventory.Application.PriceCatalogue;
using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

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
        List<(SupplierListing Listing, SupplierPriceObservation Observation, DateTime ObservedAtUtc,
            decimal? PreviousPrice)> accepted = [];

        foreach (var submission in batch.Rows)
        {
            decided.Add(Decide(run, submission, context, asOfUtc, accepted));
        }

        // One save for every listing, pending reading, observation, suggestion and recorded row the
        // batch produced, so the keys the second save needs are assigned together.
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var (listing, observation, observedAtUtc, previousPrice) in accepted)
        {
            listing.LatestObservationId = observation.Id;
            listing.LastCheckedAtUtc = observedAtUtc;

            // A re-read that finds the same price moves the check instant and leaves the change
            // instant alone - the two answer different questions (SupplierListingPriceStatePolicy).
            if (previousPrice != observation.PackPrice)
            {
                listing.LastPriceChangeAtUtc = observedAtUtc;
            }

            SupplierListingPriceStatePolicy.RequireConsistentState(
                listing.LatestObservationId,
                listing.LastCheckedAtUtc,
                listing.LastPriceChangeAtUtc);
        }

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
    /// <para><strong>Touching an overdue run expires it, and then refuses the request.</strong> That
    /// is the issue's own wording - "expired the next time it is touched" - and the order matters:
    /// the expiry is committed so the run's unanswered searches are recorded as
    /// <c>NotSearched</c> rather than left open until something else notices, and only then is the
    /// caller told the run is closed. A run that was already finished is refused without being
    /// touched.</para>
    /// </summary>
    private async Task<PriceCollectorRun?> FindOpenRunAsync(
        long runId,
        DateTime asOfUtc,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        var run = await _db.PriceCollectorRuns
            .FirstOrDefaultAsync(candidate => candidate.Id == runId, cancellationToken)
            .ConfigureAwait(false);

        if (run is null)
        {
            return null;
        }

        if (PriceCollectorRunPolicy.IsFinished(run.Status))
        {
            throw new DomainConflictException(PriceCollectorRunPolicy.AlreadyFinishedMessage);
        }

        if (run.ExpiresAtUtc is { } expiresAtUtc
            && PriceCollectorRunPolicy.HasExpired(expiresAtUtc, asOfUtc))
        {
            await PriceCollectorRunClosure
                .CloseAsync(_db, run, asOfUtc, PriceCollectorRunStatus.Expired, cancellationToken)
                .ConfigureAwait(false);

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            throw new DomainConflictException(PriceCollectorRunPolicy.AlreadyFinishedMessage);
        }

        return run;
    }

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
    /// Everything one batch needs, read once: the run's cited work items (and any work item of
    /// another run it cited, so that can be said rather than guessed), the outcomes already
    /// recorded for those keys, the listings those supplier product ids name, the last accepted
    /// price of each, the pending readings they hold, the supplier master records involved and the
    /// master product each waiting key has been matched to.
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

        var latestObservationIds = listings
            .Where(listing => listing.LatestObservationId is not null)
            .Select(listing => listing.LatestObservationId!.Value)
            .Distinct()
            .ToList();

        var lastAcceptedPrices = await _db.SupplierPriceObservations
            .AsNoTracking()
            .Where(observation => latestObservationIds.Contains(observation.Id))
            .Select(observation => new { observation.Id, observation.PackPrice })
            .ToDictionaryAsync(entry => entry.Id, entry => entry.PackPrice, cancellationToken)
            .ConfigureAwait(false);

        var listingIds = listings.ConvertAll(listing => listing.Id);

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
            lastAcceptedPrices,
            pending.ToDictionary(reading => reading.SupplierListingId),
            suppliers.ToDictionary(supplier => supplier.SupplierPresetId),
            resolvedKeys,
            [.. existingSuggestionPairs.Select(pair => (pair.WaitingKey, pair.MasterProductId!.Value))]);
    }

    /// <summary>
    /// Decides one submitted row and writes what the decision implies, in the fixed order the class
    /// summary states. It adds entities to the context and never saves: the caller owns the
    /// transaction and the two saves.
    /// </summary>
    private DecidedRow Decide(
        PriceCollectorRun run,
        PriceCollectorResultRowSubmission submission,
        SubmissionContext context,
        DateTime asOfUtc,
        List<(SupplierListing, SupplierPriceObservation, DateTime, decimal?)> accepted)
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

        if (!context.WorkItems.TryGetValue(submission.WorkItemId, out var workItem))
        {
            return Refused(
                submission.WorkItemId,
                supplierProductId,
                PriceCollectorResultPolicy.UnknownWorkItemReason);
        }

        if (workItem.PriceCollectorRunId != run.Id)
        {
            return Refused(
                submission.WorkItemId,
                supplierProductId,
                PriceCollectorResultPolicy.WorkItemFromAnotherRunReason);
        }

        var key = (workItem.Id, supplierProductId);

        if (context.AlreadyRecorded.TryGetValue(key, out var prior))
        {
            // The earlier outcome, verbatim, and nothing written. The reason is the stored one
            // rather than a re-derived one, so a repeat cannot answer differently because the
            // catalogue moved underneath it.
            return new DecidedRow(
                workItem.Id,
                supplierProductId,
                prior.Outcome,
                prior.Reason,
                Listing: null,
                Observation: null,
                prior.SupplierListingId,
                prior.SupplierPriceObservationId);
        }

        if (workItem.Outcome is not null)
        {
            return Refused(
                workItem.Id,
                supplierProductId,
                PriceCollectorResultPolicy.SearchAlreadyAnsweredReason);
        }

        if (!context.ClaimedInThisBatch.Add(key))
        {
            return Refused(
                workItem.Id,
                supplierProductId,
                PriceCollectorResultPolicy.DuplicateRowInBatchReason);
        }

        if (submission.SupplierPresetId != workItem.SupplierPresetId)
        {
            return Record(
                workItem,
                supplierProductId,
                PriceCollectorRowOutcome.Rejected,
                PriceCollectorResultPolicy.SupplierDiffersFromWorkItemReason,
                listing: null,
                observation: null,
                asOfUtc);
        }

        ObservedRow observed;

        try
        {
            observed = ObservedRow.From(submission);
        }
        catch (DomainValidationException refusal)
        {
            return Record(
                workItem,
                supplierProductId,
                PriceCollectorRowOutcome.Rejected,
                refusal.Message,
                listing: null,
                observation: null,
                asOfUtc);
        }

        var supplier = context.Suppliers[workItem.SupplierPresetId];

        if (observed.PriceBasis != supplier.PriceBasis)
        {
            return Record(
                workItem,
                supplierProductId,
                PriceCollectorRowOutcome.Rejected,
                PriceCollectorResultPolicy.PriceBasisDiffersFromSupplierReason,
                listing: null,
                observation: null,
                asOfUtc);
        }

        if (!PriceCollectorResultPolicy.IsObservedWithinRun(
            observed.ObservedAtUtc,
            run.StartedAtUtc,
            asOfUtc))
        {
            return Record(
                workItem,
                supplierProductId,
                PriceCollectorRowOutcome.Rejected,
                observed.ObservedAtUtc < run.StartedAtUtc
                    ? PriceCollectorResultPolicy.ObservedBeforeRunStartedReason
                    : PriceCollectorResultPolicy.ObservedInFutureReason,
                listing: null,
                observation: null,
                asOfUtc);
        }

        if (!PriceCollectorResultPolicy.SourceLinkMatchesSupplier(supplier.SiteUrl, observed.SourceUrl))
        {
            return Record(
                workItem,
                supplierProductId,
                PriceCollectorRowOutcome.Rejected,
                PriceCollectorResultPolicy.SourceLinkHostMismatchReason,
                listing: null,
                observation: null,
                asOfUtc);
        }

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

        var previousPrice = listing?.LatestObservationId is { } latest
            && context.LastAcceptedPrices.TryGetValue(latest, out var price)
                ? price
                : (decimal?)null;

        var decision = listing is null
            ? PriceCollectorResultPolicy.DecideForNewListing()
            : PriceCollectorResultPolicy.DecideForKnownListing(
                listing.MatchStatus,
                IdentityOf(listing),
                observed.Identity,
                observed.PackPrice,
                previousPrice);

        if (listing is null)
        {
            listing = NewListing(workItem, resolvedMasterProductId, supplierProductId, observed, asOfUtc);
            _db.SupplierListings.Add(listing);
            context.Listings[(workItem.SupplierPresetId, supplierProductId)] = listing;
        }
        else
        {
            UpdateListing(listing, observed, asOfUtc);
        }

        if (decision.NewMatchStatus is { } newStatus)
        {
            listing.MatchStatus = newStatus;
        }

        SupplierPriceObservation? observation = null;

        if (decision.RecordsAcceptedObservation)
        {
            observation = new SupplierPriceObservation
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
            accepted.Add((listing, observation, observed.ObservedAtUtc, previousPrice));

            // An accepted price settles whatever disagreement was waiting on this listing.
            if (context.Pending.Remove(listing.Id, out var resolved))
            {
                _db.SupplierListingPendingObservations.Remove(resolved);
            }
        }

        if (decision.StoresPendingObservation)
        {
            StorePending(context, listing, workItem, run, observed, decision.Reason!, asOfUtc);
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
    /// <para><strong>Identity is rewritten only while the listing is still a proposal.</strong> A
    /// confirmed, flagged or refused listing's variant, size, barcode and pack count are what
    /// somebody decided about, and overwriting them would quietly resolve the disagreement that
    /// sent the listing to review in the first place; the reading is kept beside it as a pending
    /// observation instead, where #928 can compare the two.</para>
    /// </summary>
    private static void UpdateListing(SupplierListing listing, ObservedRow observed, DateTime asOfUtc)
    {
        listing.Title = observed.Title ?? listing.Title;
        listing.Url = observed.SourceUrl ?? listing.Url;
        listing.UpdatedAtUtc = asOfUtc;

        if (listing.MatchStatus != SupplierListingMatchStatus.Proposed)
        {
            return;
        }

        listing.Variant = observed.Identity.Variant;
        listing.SizeValue = observed.Identity.SizeValue;
        listing.SizeUnit = observed.Identity.SizeUnit;
        listing.UnitsPerPack = observed.Identity.UnitsPerPack;
        listing.Barcode = observed.Identity.Barcode;
        listing.BarcodeScope = observed.Identity.BarcodeScope;
    }

    /// <summary>
    /// Keeps the reading as this listing's pending observation, replacing whatever was pending for
    /// it. One row per listing, which the unique index holds.
    /// </summary>
    private void StorePending(
        SubmissionContext context,
        SupplierListing listing,
        PriceCollectorWorkItem workItem,
        PriceCollectorRun run,
        ObservedRow observed,
        string reason,
        DateTime asOfUtc)
    {
        SupplierListingPendingObservation? reading = null;

        // A listing this batch has just created has no key yet, and no pending reading either -
        // there is nothing to replace, so it always gets a new row.
        if (listing.Id != 0)
        {
            context.Pending.TryGetValue(listing.Id, out reading);
        }

        if (reading is null)
        {
            reading = new SupplierListingPendingObservation { SupplierListing = listing };

            _db.SupplierListingPendingObservations.Add(reading);

            if (listing.Id != 0)
            {
                context.Pending[listing.Id] = reading;
            }
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

    /// <summary>Everything one batch is decided against, read once before any of it is decided.</summary>
    private sealed record SubmissionContext(
        Dictionary<long, PriceCollectorWorkItem> WorkItems,
        Dictionary<(long WorkItemId, string SupplierProductId), PriceCollectorResultRow> AlreadyRecorded,
        Dictionary<(int SupplierPresetId, string SupplierProductId), SupplierListing> Listings,
        Dictionary<long, decimal> LastAcceptedPrices,
        Dictionary<long, SupplierListingPendingObservation> Pending,
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
    }
}
