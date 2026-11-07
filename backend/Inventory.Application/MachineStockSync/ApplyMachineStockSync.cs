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
/// <para><strong>A failure is scoped to its product.</strong> A movement that cannot be recorded or
/// a fatal costing data-quality issue leaves that product's transaction uncommitted, so every event
/// whose write it had staged reports the established <c>Error</c> outcome with no movement id, and
/// the other products are applied independently. An event the policy already refused keeps its own
/// outcome - including <c>InsufficientStock</c>, which was decided against the storage left at the
/// time of the check and stays in the response even when the rollback restores that stock; the event
/// is still <c>Unprocessed</c> and can simply be retried.</para>
/// </summary>
public sealed class ApplyMachineStockSync
{
    private readonly IMachineStockEventStore _store;

    public ApplyMachineStockSync(IMachineStockEventStore store)
    {
        _store = store;
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

        List<long> productOrder = [];
        var batches = new Dictionary<long, List<int>>();

        foreach (var eventId in requested)
        {
            if (!planned.TryGetValue(eventId, out var state))
            {
                results[eventId] = NotFound(eventId);
                continue;
            }

            if (state.MatchedProductId is not long productId)
            {
                // Without a matched product every ruling the policy can reach is a non-writing one,
                // whatever storage holds, so this event needs neither a product read nor a lock.
                results[eventId] = Classify(state, Decide(state, null, manualRefills));
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
                machine, productId, batches[productId], manualRefills, results, cancellationToken);
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
        Dictionary<int, NayaxStockEventApplyResultDto> results,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _store
            .BeginRefillWriteTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Authoritative, inside the write lock: these are the rows every decision below is made on
        // and the quantity the movements below are applied to.
        var states = (await _store.FindEventsAsync(machine.RequiredNayaxMachineId, eventIds, cancellationToken))
            .ToDictionary(state => state.Id);
        var product = await _store.FindStorageProductAsync(productId, cancellationToken);

        // The stored quantity read under the lock, less every refill this batch has already
        // accepted. An event that does not fit the running balance is refused and stays Unprocessed.
        var available = product?.QuantityInStock;
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

            available = availableToThisEvent!.Value - state.ParsedQuantity!.Value;

            // The movement id is filled in from the write below; until it commits there is none.
            results[eventId] = new(eventId, NayaxStockEventApplyOutcome.Applied, ruling.Message, null);
        }

        if (accepted.Count == 0)
        {
            return;
        }

        var application = await _store
            .ApplyProductRefillsAsync(machine, productId, accepted, cancellationToken)
            .ConfigureAwait(false);

        if (!application.Succeeded)
        {
            // Returned without committing: the transaction's disposal is what discards every write
            // this batch staged, so each of them reports the same failure and no movement id.
            foreach (var refill in accepted)
            {
                results[refill.EventId] = new(
                    refill.EventId,
                    NayaxStockEventApplyOutcome.Error,
                    "Failed to apply this event; no inventory movement was recorded.",
                    null);
            }

            return;
        }

        foreach (var applied in application.Applied)
        {
            results[applied.EventId] = results[applied.EventId] with
            {
                StockAdjustmentId = applied.StockAdjustmentId,
            };
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static NayaxStockEventApplyResultDto NotFound(int eventId) =>
        new(eventId, NayaxStockEventApplyOutcome.Error, "Event not found for this machine.", null);

    private static NayaxStockEventApplyRuling Decide(
        MachineStockEventState state,
        int? availableStorageQuantity,
        IReadOnlyList<ManualRefillEvidence> manualRefills)
    {
        var isPossibleDuplicate = state.MatchStatus == NayaxStockEventMatchStatus.Matched
            && state.MatchedProductId is long matchedProductId
            && state.ParsedQuantity is > 0
            && NayaxMachineStockDuplicatePolicy.FindPossibleDuplicate(
                matchedProductId, state.ParsedQuantity.Value, state.EventDateTimeGmt, manualRefills) is not null;

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
