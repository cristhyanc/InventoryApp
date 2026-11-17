using Inventory.Application.Machines;
using Inventory.Domain.Nayax;

namespace Inventory.Application.MachineStockSync;

/// <summary>
/// The explicit Sync Restock duplicate-resolution use case (issue #196): an operator must choose one
/// of two resolutions for a Nayax event flagged as a possible duplicate of a manual refill before it
/// can leave the "unresolved" state the ordinary <see cref="ApplyMachineStockSync"/> path refuses.
/// <c>AlreadyRecordedManually</c> reconciles the event without ever creating a movement or changing
/// storage; <c>ApplyAsSeparateRestock</c> is an explicit, auditable override that applies it exactly
/// once through the same MachineRefill inventory/costing path as an ordinary apply. Both resolutions
/// are idempotent: repeating either request never creates a second movement or changes storage again.
///
/// <para><strong>Both of them take the write lock, and an event ends in one state</strong> (issue
/// #1248). Each resolution re-reads the event inside a <c>BEGIN IMMEDIATE</c> transaction before it
/// writes anything, because "is this event still pending?" is a check-then-write rule and a read
/// taken before the lock is a read two simultaneous resolutions both pass. Repeating the
/// <em>same</em> resolution is still the established idempotent replay, but one that finds the event
/// settled the <em>other</em> way is refused with
/// <see cref="NayaxStockEventApplyOutcome.ResolutionConflict"/> - the endpoint's <c>409</c> - having
/// written nothing: an event that has been applied cannot also be recorded as never applied.</para>
/// </summary>
public sealed class ResolveMachineStockDuplicate
{
    private readonly IMachineStockEventStore _store;
    private readonly IMachineStockSyncLog _log;

    public ResolveMachineStockDuplicate(IMachineStockEventStore store, IMachineStockSyncLog log)
    {
        _store = store;
        _log = log;
    }

    public async Task<NayaxStockEventApplyResultDto> Handle(
        MachineIdentity machine, int eventId, NayaxDuplicateResolutionChoice choice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);

        // The event is the provider's, found by the Nayax number; the manual refills it may duplicate
        // are this application's movements and name the local machine (issue #741).
        var state = await _store.FindEventAsync(machine.RequiredNayaxMachineId, eventId, cancellationToken);
        if (state is null)
            return NotFound(eventId);

        // Already settled before this request even started: the same resolution is the idempotent
        // replay, the other one is refused. The locked re-read below asks the same question again
        // against what is actually committed, which is what decides a genuine race.
        if (Settled(state, choice) is { } settled)
            return settled;

        var manualRefills = await _store.GetManualMachineRefillsAsync(machine.LocalMachineId, cancellationToken);
        var duplicate = state.MatchStatus == NayaxStockEventMatchStatus.Matched
            && state.MatchedProductId is long matchedProductId
            && state.ParsedQuantity is > 0
                ? NayaxMachineStockDuplicatePolicy.FindPossibleDuplicate(
                    matchedProductId, state.EventDateTimeGmt, manualRefills)
                : null;

        if (duplicate is null)
        {
            return new(
                eventId,
                NayaxStockEventApplyOutcome.NotApplicable,
                "This event is not currently flagged as a possible duplicate and cannot be resolved.",
                null);
        }

        return choice switch
        {
            NayaxDuplicateResolutionChoice.AlreadyRecordedManually =>
                await ReconcileAsync(machine, state, duplicate.Value.Match, cancellationToken),
            NayaxDuplicateResolutionChoice.ApplyAsSeparateRestock =>
                await ApplyAsSeparateRestockAsync(machine, state, duplicate.Value.Match, cancellationToken),
            _ => new(eventId, NayaxStockEventApplyOutcome.Error, "Unknown duplicate resolution.", null)
        };
    }

    /// <summary>
    /// The resolution that writes no movement, and since issue #1248 it takes the same write-locked
    /// transaction the override below takes. It changes no quantity, but it decides the same
    /// question - may this event still be settled? - and that question decides a write: reading the
    /// event before the lock let two operators resolving one event in opposite ways both pass their
    /// own check, leaving it applied and marked as never applied at once. The event is therefore
    /// re-read <em>inside</em> the lock and a resolution that finds the override already committed
    /// is refused with a conflict having written nothing.
    /// </summary>
    private async Task<NayaxStockEventApplyResultDto> ReconcileAsync(
        MachineIdentity machine, MachineStockEventState state, ManualRefillEvidence duplicate, CancellationToken cancellationToken)
    {
        await using var transaction = await _store
            .BeginRefillWriteTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var locked = await _store.FindEventAsync(machine.RequiredNayaxMachineId, state.Id, cancellationToken);
        if (locked is null)
            return NotFound(state.Id);

        if (Settled(locked, NayaxDuplicateResolutionChoice.AlreadyRecordedManually) is { } settled)
            return settled;

        await _store.ReconcileAsManualDuplicateAsync(
            locked.Id, machine.RequiredNayaxMachineId, duplicate.StockAdjustmentId, cancellationToken);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new(
            locked.Id,
            NayaxStockEventApplyOutcome.Reconciled,
            MachineStockResolutionMessages.Reconciled,
            null);
    }

    /// <summary>
    /// How an already-settled event answers <paramref name="choice"/>, or <see langword="null"/>
    /// when it is still pending (issue #1248). A resolution asking for the state the event is
    /// already in is the established idempotent replay; one asking for the other state is a
    /// conflict, because the two resolutions are mutually exclusive answers to the same question
    /// and the first one committed is the event's final state.
    /// </summary>
    internal static NayaxStockEventApplyResultDto? Settled(
        MachineStockEventState state, NayaxDuplicateResolutionChoice choice)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.DuplicateResolution == NayaxDuplicateResolution.ReconciledManually)
        {
            return choice == NayaxDuplicateResolutionChoice.AlreadyRecordedManually
                ? new(state.Id, NayaxStockEventApplyOutcome.Reconciled, MachineStockResolutionMessages.AlreadyReconciled, null)
                : new(state.Id, NayaxStockEventApplyOutcome.ResolutionConflict, MachineStockResolutionMessages.ReconciledCannotBeApplied, null);
        }

        if (state.ProcessingStatus == NayaxStockEventProcessingStatus.Applied)
        {
            return choice == NayaxDuplicateResolutionChoice.ApplyAsSeparateRestock
                ? new(state.Id, NayaxStockEventApplyOutcome.Applied, MachineStockResolutionMessages.AlreadyApplied, state.StockAdjustmentId)
                : new(state.Id, NayaxStockEventApplyOutcome.ResolutionConflict, MachineStockResolutionMessages.AppliedCannotBeReconciled, null);
        }

        return null;
    }

    private static NayaxStockEventApplyResultDto NotFound(int eventId) =>
        new(eventId, NayaxStockEventApplyOutcome.Error, MachineStockResolutionMessages.NotFound, null);

    /// <summary>
    /// The one resolution that writes, and it takes the same write-locked transaction one product's
    /// apply batch takes (issue #1127): the event and its matched product's storage quantity are
    /// re-read and revalidated <em>inside</em> the lock, because both of them decide the write. That
    /// is what keeps this action from recording a second movement when it races an ordinary apply of
    /// the same event - the loser waits, re-reads the winner's committed <c>Applied</c> state, and
    /// reports it having written nothing.
    /// </summary>
    private async Task<NayaxStockEventApplyResultDto> ApplyAsSeparateRestockAsync(
        MachineIdentity machine, MachineStockEventState state, ManualRefillEvidence duplicate, CancellationToken cancellationToken)
    {
        await using var transaction = await _store
            .BeginRefillWriteTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var locked = await _store.FindEventAsync(machine.RequiredNayaxMachineId, state.Id, cancellationToken);
        if (locked is null)
            return NotFound(state.Id);

        // The same settled states the use case answers before any lock, re-checked against what is
        // actually committed, so a resolution that arrives while another writer is committing is
        // answered from that writer's result instead of applying the event twice. A second override
        // is still the idempotent replay; a reconciliation that got there first is a conflict.
        if (Settled(locked, NayaxDuplicateResolutionChoice.ApplyAsSeparateRestock) is { } settled)
            return settled;

        var product = locked.MatchedProductId is long productId
            ? await _store.FindStorageProductAsync(productId, cancellationToken)
            : null;

        // The operator has just explicitly overridden the possible-duplicate warning, so the
        // underlying match/quantity/storage guards still apply, but the duplicate gate itself does
        // not: that is exactly what this resolution means.
        var ruling = NayaxMachineStockApplyPolicy.Decide(
            locked.ProcessingStatus,
            locked.MatchStatus,
            locked.NeedsReviewReason,
            locked.MatchedProductId,
            locked.ParsedQuantity,
            product?.QuantityInStock,
            eventCode: locked.EventCode);

        switch (ruling.Decision)
        {
            case NayaxStockEventApplyDecision.NeedsReview:
                return new(locked.Id, NayaxStockEventApplyOutcome.NotMatched, ruling.Message, null);

            case NayaxStockEventApplyDecision.NegativeDiscrepancy:
            case NayaxStockEventApplyDecision.ProductReplacementNotApplicable:
                return new(locked.Id, NayaxStockEventApplyOutcome.NotApplicable, ruling.Message, null);

            case NayaxStockEventApplyDecision.InsufficientStorage:
                return new(locked.Id, NayaxStockEventApplyOutcome.InsufficientStock, ruling.Message, null);

            case NayaxStockEventApplyDecision.MatchedProductMissing:
                return new(locked.Id, NayaxStockEventApplyOutcome.Error, ruling.Message, null);
        }

        var application = await _store.ApplyProductRefillsAsync(
            machine,
            locked.MatchedProductId!.Value,
            [
                new MachineStockRefillRequest(
                    locked.Id,
                    locked.NayaxEventLogId,
                    locked.ParsedQuantity!.Value,
                    NayaxDuplicateResolution.AppliedAsSeparateRestock,
                    duplicate.StockAdjustmentId),
            ],
            cancellationToken);

        if (!application.Succeeded)
        {
            // Returned without committing: the transaction's disposal discards the movement and the
            // resolution state together, exactly as they would have been written together. The
            // override writes the same refill as an ordinary apply, so it is logged and explains a
            // cost data-quality refusal in exactly the same way (issue #1240).
            _log.ApplyFailed(
                locked.Id,
                application.Failure?.FailureType ?? ApplyMachineStockSync.UnreportedFailure,
                application.Failure?.CostDataQualityMessage);
            return ApplyMachineStockSync.Failed(locked.Id, application.Failure?.CostDataQualityMessage);
        }

        try
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A failed commit wrote nothing either: the disposal rolls the movement and the
            // resolution back together and the event stays unresolved, so it reports the same
            // failure. A cancelled request is not caught here. Only the exception's type name is
            // carried: a provider's own message can quote the statement or the data it failed on.
            _log.ApplyFailed(locked.Id, exception.GetType().Name, null);
            return ApplyMachineStockSync.Failed(locked.Id);
        }

        return new(
            locked.Id,
            NayaxStockEventApplyOutcome.Applied,
            "Applied as a separate restock: the operator explicitly overrode the possible-duplicate warning.",
            application.Applied.Single().StockAdjustmentId);
    }
}
