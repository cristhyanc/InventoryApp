using Inventory.Application.Machines;
using Inventory.Domain.Nayax;

namespace Inventory.Application.MachineStockSync;

/// <summary>
/// The machine-level Sync Restock apply use case (issue #183): applies exactly the imported events
/// the operator explicitly accepted. Each event is decided by the deterministic Domain
/// <see cref="NayaxMachineStockApplyPolicy"/> and, only if it may be applied, recorded as its own
/// <c>MachineRefill</c> movement - so one unresolved or failing event never stops or corrupts the
/// others in the batch.
///
/// <para><strong>One transaction and one cost rebuild per product, not per event</strong> (issue
/// #1127). The request is grouped by the matched product and each product's accepted events are
/// written together inside one write-locked transaction, which ends with a single
/// <c>IRebuildProductCost.RebuildAsync</c> for that product. Replaying a product's whole movement
/// and sale history is what an apply actually costs, so ten ticked events for one product used to
/// mean ten full replays of it; they now mean one. Each event is still its own movement with its own
/// note and its own <c>StockAdjustmentId</c>, so nothing about the audit trail changes.</para>
///
/// <para><strong>The eligibility reads are inside that lock, and that is the point.</strong> Whether
/// an event may be applied is decided from the event's own stored state and the product's storage
/// quantity, and both of those decide a write: a read taken before the lock is a read two
/// simultaneous applies of the same event both pass. So the transaction is <c>BEGIN IMMEDIATE</c> on
/// SQLite - the write lock is held before the events and the product are read - and the loser waits,
/// re-reads the winner's committed state, and reports the established <c>Already applied.</c>
/// outcome having written nothing. Within one batch the same rule applies between the events
/// themselves: every accepted refill reduces the storage available to the next event before its
/// eligibility is decided, so a batch can never take more units out of storage than it holds.</para>
///
/// <para><strong>A failure is scoped to its product.</strong> A movement that cannot be recorded,
/// a fatal costing data-quality issue or a commit that fails leaves that product's transaction uncommitted, so every event
/// whose write it had staged reports the established <c>Error</c> outcome with no movement id, and
/// the other products are applied independently. An event the policy already refused keeps its own
/// outcome - including <c>InsufficientStock</c>, which was decided against the storage left at the
/// time of the check and stays in the response even when the rollback restores that stock; the event
/// is still <c>Unprocessed</c> and can simply be retried.</para>
///
/// <para><strong>The match is re-evaluated inside that lock too</strong> (issue #1248). Which
/// product an event names is decided at import, so an event imported while its MDB position or its
/// product's Nayax id was missing stayed unapplicable for ever. The machine's current mapping is
/// therefore read once per request - outside every lock, because it is a Nayax read and nothing is
/// decided on it - and each batch re-resolves the events still waiting for a match <em>inside</em>
/// its own write lock, writing the refreshed match and the movement it decided in the same
/// transaction. Only events that are still waiting are re-matched: an applied, reconciled or
/// separately-applied event's match is the record of which product's storage moved. A request with
/// nothing waiting makes no Nayax call at all, and a mapping that cannot be read leaves every event
/// decided from what is stored, exactly as before.</para>
///
/// <para><strong>Every failed apply is logged, and one failure explains itself</strong> (issue
/// #1240). Each event of a rolled-back batch leaves one <c>Warning</c> through
/// <see cref="IMachineStockSyncLog"/> with its event id and the failure's short code, so a restock
/// that never lands is no longer invisible to whoever is asked about it. The operator's own result
/// still carries the established generic sentence, with one exception: a fatal cost data-quality
/// refusal names the product and what is wrong with its history, because that is a repair the
/// operator can actually make and a retry cannot.</para>
/// </summary>
public sealed class ApplyMachineStockSync
{
    /// <summary>The established sentence for an event whose write was attempted but not committed.</summary>
    public const string FailedMessage = "Failed to apply this event; no inventory movement was recorded.";

    /// <summary>The log's code for a failure the store did not describe at all.</summary>
    internal const string UnreportedFailure = "Unreported";

    private readonly IMachineStockEventStore _store;
    private readonly RematchMachineStockEvents _rematch;
    private readonly IMachineStockSyncLog _log;

    public ApplyMachineStockSync(
        IMachineStockEventStore store, RematchMachineStockEvents rematch, IMachineStockSyncLog log)
    {
        _store = store;
        _rematch = rematch;
        _log = log;
    }

    /// <summary>
    /// Applies the accepted events of <paramref name="machine"/>: its events are the provider's and
    /// are found by its Nayax number, and its manual refills and the movements written here name its
    /// local id (issue #741). One result per distinct requested event id, in request order.
    /// </summary>
    public async Task<NayaxMachineStockApplyResponseDto> Handle(
        MachineIdentity machine, IReadOnlyList<int> eventIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);

        List<int> requested = [.. eventIds.Distinct()];
        var manualRefills = await _store.GetManualMachineRefillsAsync(machine.LocalMachineId, cancellationToken);
        var results = new Dictionary<int, NayaxStockEventApplyResultDto>();

        // A planning read, deliberately outside every lock, because nothing is decided on it: it
        // only says which product each requested event belongs to. Every batch below re-reads its
        // own events and its product inside its own write lock before anything is written.
        var planned = (await _store.FindEventsAsync(machine.RequiredNayaxMachineId, requested, cancellationToken))
            .ToDictionary(state => state.Id);

        // The machine's current MDB mapping, for the events still waiting for a match (issue
        // #1248). It is read here, outside every lock, because reading it reaches Nayax and nothing
        // is decided on it: it only says which batch such an event might belong to. Each batch
        // re-resolves and writes the match inside its own lock, from the row it read there.
        var mapping = await ReadCurrentMappingAsync(machine, planned.Values, cancellationToken);

        List<long> productOrder = [];
        var batches = new Dictionary<long, List<int>>();

        foreach (var eventId in requested)
        {
            if (!planned.TryGetValue(eventId, out var state))
            {
                results[eventId] = NotFound(eventId);
                continue;
            }

            var refreshed = Refreshed(state, mapping);
            if (refreshed.MatchedProductId is not long productId)
            {
                // Without a matched product every ruling the policy can reach is a non-writing one,
                // whatever storage holds, so this event needs neither a product read nor a lock.
                results[eventId] = Classify(refreshed, Decide(refreshed, null, manualRefills));
                continue;
            }

            if (!batches.TryGetValue(productId, out var batch))
            {
                batches[productId] = batch = [];
                productOrder.Add(productId);
            }

            batch.Add(eventId);
        }

        foreach (var productId in productOrder)
        {
            await ApplyProductBatchAsync(
                machine, productId, batches[productId], manualRefills, mapping, results, cancellationToken);
        }

        return new NayaxMachineStockApplyResponseDto([.. requested.Select(eventId => results[eventId])]);
    }

    /// <summary>
    /// Applies one product's accepted events in request order, in one write-locked transaction that
    /// ends with that product's single cost rebuild. Nothing is written for a batch whose events all
    /// turn out to be non-writing, and in particular no rebuild runs for it.
    /// </summary>
    private async Task ApplyProductBatchAsync(
        MachineIdentity machine,
        long productId,
        IReadOnlyList<int> eventIds,
        IReadOnlyList<ManualRefillEvidence> manualRefills,
        MachineStockMatchRefresh? mapping,
        Dictionary<int, NayaxStockEventApplyResultDto> results,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _store
            .BeginRefillWriteTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Authoritative, inside the write lock: these are the rows every decision below is made on
        // and the quantity the movements below are applied to.
        var stored = await _store.FindEventsAsync(machine.RequiredNayaxMachineId, eventIds, cancellationToken);
        var product = await _store.FindStorageProductAsync(productId, cancellationToken);

        // The refreshed match of whatever this batch read as still waiting for one (issue #1248),
        // decided from the same rows the rulings below are. It is written before the movements and
        // committed with them, so a refreshed match can never outlive a batch that rolled back.
        IReadOnlyList<MachineStockEventMatchUpdate> rematched = mapping is null
            ? []
            : RematchMachineStockEvents.Changes(stored, mapping);

        // Those same rows as the current mapping decides them, which is what every ruling below is
        // made on, so what the batch decides and what it records about the match cannot differ.
        var states = stored.ToDictionary(state => state.Id, state => Refreshed(state, mapping));

        var accepted = AcceptEligible(
            productId, eventIds, states, product?.QuantityInStock, manualRefills, results);

        if (accepted.Count == 0 && rematched.Count == 0)
        {
            return;
        }

        if (rematched.Count > 0)
        {
            await _store
                .UpdateMatchesAsync(machine.RequiredNayaxMachineId, rematched, cancellationToken)
                .ConfigureAwait(false);
        }

        IReadOnlyList<AppliedMachineRefill> applied = [];
        if (accepted.Count > 0)
        {
            var application = await _store
                .ApplyProductRefillsAsync(machine, productId, accepted, cancellationToken)
                .ConfigureAwait(false);

            if (!application.Succeeded)
            {
                // Returned without committing: the transaction's disposal is what discards every
                // write this batch staged - the refreshed matches included - so each accepted event
                // reports the same failure and no movement id.
                ReportFailed(accepted, results, application.Failure);
                return;
            }

            applied = application.Applied;
        }

        try
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A commit that fails is the same failure as a write that fails: nothing this batch
            // staged was committed, the transaction's disposal rolls it back, and the next product
            // is still applied. A cancelled request is not caught here and stops the whole apply.
            // Only the exception's type name is carried: a provider's own message can quote the
            // statement or the data it failed on.
            ReportFailed(accepted, results, new MachineRefillFailure(exception.GetType().Name, null));
            return;
        }

        // Only now are the movement ids real: they are reported once the batch has committed.
        foreach (var refill in applied)
        {
            results[refill.EventId] = results[refill.EventId] with
            {
                StockAdjustmentId = refill.StockAdjustmentId,
            };
        }
    }

    /// <summary>
    /// The refills one batch accepts, in request order, decided from the rows it read inside its own
    /// write lock. Every event the policy refuses is given its non-writing result here instead and
    /// nothing is written for it. <paramref name="storedQuantity"/> is the product's storage quantity
    /// read under that same lock, and each accepted refill reduces the quantity left to the next
    /// event before its eligibility is decided, so a batch can never take more units out of storage
    /// than it holds.
    /// </summary>
    private static List<MachineStockRefillRequest> AcceptEligible(
        long productId,
        IReadOnlyList<int> eventIds,
        IReadOnlyDictionary<int, MachineStockEventState> states,
        int? storedQuantity,
        IReadOnlyList<ManualRefillEvidence> manualRefills,
        Dictionary<int, NayaxStockEventApplyResultDto> results)
    {
        var available = storedQuantity;
        var accepted = new List<MachineStockRefillRequest>();

        foreach (var eventId in eventIds)
        {
            if (!states.TryGetValue(eventId, out var state))
            {
                results[eventId] = NotFound(eventId);
                continue;
            }

            // The matched product is revalidated under the lock as well: a row that no longer names
            // this batch's product is refused rather than written inside its transaction.
            var availableToThisEvent = state.MatchedProductId == productId ? available : null;
            var ruling = Decide(state, availableToThisEvent, manualRefills);
            if (ruling.Decision != NayaxStockEventApplyDecision.Apply)
            {
                results[eventId] = Classify(state, ruling);
                continue;
            }

            accepted.Add(new MachineStockRefillRequest(
                state.Id,
                state.NayaxEventLogId,
                state.ParsedQuantity!.Value,
                NayaxDuplicateResolution.None,
                MatchedManualStockAdjustmentId: null));

            available = availableToThisEvent!.Value - state.ParsedQuantity.Value;

            // The movement id is filled in once the batch's write commits; until then there is none.
            results[eventId] = new(eventId, NayaxStockEventApplyOutcome.Applied, ruling.Message, null);
        }

        return accepted;
    }

    /// <summary>
    /// Every event whose write this batch staged: one log line each, and the per-event result the
    /// operator sees. <paramref name="failure"/> is absent only for a store that reports no reason
    /// at all, which is then logged as <see cref="UnreportedFailure"/> rather than not logged.
    /// </summary>
    private void ReportFailed(
        IEnumerable<MachineStockRefillRequest> accepted,
        Dictionary<int, NayaxStockEventApplyResultDto> results,
        MachineRefillFailure? failure)
    {
        foreach (var eventId in accepted.Select(refill => refill.EventId))
        {
            _log.ApplyFailed(
                eventId, failure?.FailureType ?? UnreportedFailure, failure?.CostDataQualityMessage);
            results[eventId] = Failed(eventId, failure?.CostDataQualityMessage);
        }
    }

    /// <summary>
    /// The established outcome for an event whose write was attempted but not committed, with the
    /// one reason that is published to the operator appended when there is one (issue #1240). The
    /// first sentence is unchanged, so what the operator already knows this result means - nothing
    /// was recorded, the event can be retried - still reads first.
    /// </summary>
    internal static NayaxStockEventApplyResultDto Failed(int eventId, string? reason = null) =>
        new(
            eventId,
            NayaxStockEventApplyOutcome.Error,
            string.IsNullOrWhiteSpace(reason) ? FailedMessage : $"{FailedMessage} {reason}",
            null);

    private static NayaxStockEventApplyResultDto NotFound(int eventId) =>
        new(eventId, NayaxStockEventApplyOutcome.Error, MachineStockResolutionMessages.NotFound, null);

    /// <summary>
    /// The machine's current MDB mapping, or <see langword="null"/> when this request has no event
    /// that a refreshed mapping could decide anything about (issue #1248) - an ordinary apply of
    /// matched events therefore makes no Nayax call at all.
    /// </summary>
    private async Task<MachineStockMatchRefresh?> ReadCurrentMappingAsync(
        MachineIdentity machine,
        IEnumerable<MachineStockEventState> planned,
        CancellationToken cancellationToken)
    {
        if (!planned.Any(RematchMachineStockEvents.IsWaitingForAMatch))
        {
            return null;
        }

        try
        {
            return await _rematch
                .ReadCurrentMappingAsync(machine.RequiredNayaxMachineId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Nayax could not be read, so the machine's current mapping is unknown. Every event is
            // then decided from what is stored, exactly as it was before re-matching existed: an
            // event still waiting for a match keeps waiting rather than being applied on a guess,
            // and the already-matched events of the same request are applied unaffected.
            return null;
        }
    }

    /// <summary>
    /// One event as the current mapping decides it, or unchanged when there is no mapping or the
    /// event is not one that may be re-matched. It is the same resolution
    /// <see cref="RematchMachineStockEvents.Changes"/> stages for the write, so what a batch decides
    /// and what it records about the match cannot differ.
    /// </summary>
    private static MachineStockEventState Refreshed(
        MachineStockEventState state, MachineStockMatchRefresh? mapping) =>
        mapping is not null
            && RematchMachineStockEvents.IsWaitingForAMatch(state)
            && mapping.Resolve(state) is { } resolution
                ? state with
                {
                    MatchStatus = resolution.MatchStatus,
                    MatchedProductId = resolution.MatchedProductId,
                    NeedsReviewReason = resolution.NeedsReviewReason,
                }
                : state;

    private static NayaxStockEventApplyRuling Decide(
        MachineStockEventState state,
        int? availableStorageQuantity,
        IReadOnlyList<ManualRefillEvidence> manualRefills)
    {
        var isPossibleDuplicate = state.MatchStatus == NayaxStockEventMatchStatus.Matched
            && state.MatchedProductId is long matchedProductId
            && state.ParsedQuantity is > 0
            && NayaxMachineStockDuplicatePolicy.FindPossibleDuplicate(
                matchedProductId, state.EventDateTimeGmt, manualRefills) is not null;

        return NayaxMachineStockApplyPolicy.Decide(
            state.ProcessingStatus,
            state.MatchStatus,
            state.NeedsReviewReason,
            state.MatchedProductId,
            state.ParsedQuantity,
            availableStorageQuantity,
            isPossibleDuplicate,
            state.DuplicateResolution);
    }

    /// <summary>
    /// The response for a ruling that writes nothing. Every one of them keeps the policy's own
    /// message, so a refusal reads the same whether it was decided before a lock or inside one.
    /// </summary>
    private static NayaxStockEventApplyResultDto Classify(
        MachineStockEventState state, NayaxStockEventApplyRuling ruling) =>
        ruling.Decision switch
        {
            NayaxStockEventApplyDecision.AlreadyApplied =>
                new(state.Id, NayaxStockEventApplyOutcome.Applied, ruling.Message, state.StockAdjustmentId),

            NayaxStockEventApplyDecision.ReconciledDuplicate =>
                new(state.Id, NayaxStockEventApplyOutcome.Reconciled, ruling.Message, null),

            NayaxStockEventApplyDecision.DuplicateRequiresResolution =>
                new(state.Id, NayaxStockEventApplyOutcome.DuplicateRequiresResolution, ruling.Message, null),

            NayaxStockEventApplyDecision.NeedsReview =>
                new(state.Id, NayaxStockEventApplyOutcome.NotMatched, ruling.Message, null),

            NayaxStockEventApplyDecision.NegativeDiscrepancy =>
                new(state.Id, NayaxStockEventApplyOutcome.NotApplicable, ruling.Message, null),

            NayaxStockEventApplyDecision.InsufficientStorage =>
                new(state.Id, NayaxStockEventApplyOutcome.InsufficientStock, ruling.Message, null),

            NayaxStockEventApplyDecision.MatchedProductMissing =>
                new(state.Id, NayaxStockEventApplyOutcome.Error, ruling.Message, null),

            // Apply is the one ruling that writes, and it is handled where the writes are. Reaching
            // here would mean an accepted event was about to be reported as refused.
            _ => throw new InvalidOperationException(
                $"Stock event {state.Id} decided {ruling.Decision}, which is not a non-writing outcome."),
        };
}
