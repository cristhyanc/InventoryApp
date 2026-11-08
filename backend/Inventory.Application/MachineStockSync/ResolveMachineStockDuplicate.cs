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
/// </summary>
public sealed class ResolveMachineStockDuplicate
{
    private readonly IMachineStockEventStore _store;

    public ResolveMachineStockDuplicate(IMachineStockEventStore store)
    {
        _store = store;
    }

    public async Task<NayaxStockEventApplyResultDto> Handle(
        MachineIdentity machine, int eventId, NayaxDuplicateResolutionChoice choice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);

        // The event is the provider's, found by the Nayax number; the manual refills it may duplicate
        // are this application's movements and name the local machine (issue #741).
        var state = await _store.FindEventAsync(machine.RequiredNayaxMachineId, eventId, cancellationToken);
        if (state is null)
            return new(eventId, NayaxStockEventApplyOutcome.Error, "Event not found for this machine.", null);

        // Idempotent replay: once settled, repeating either resolution returns the same outcome
        // without creating another movement or touching storage again.
        if (state.DuplicateResolution == NayaxDuplicateResolution.ReconciledManually)
        {
            return new(
                eventId,
                NayaxStockEventApplyOutcome.Reconciled,
                "Already reconciled as recorded manually; no Nayax movement was applied.",
                null);
        }

        if (state.ProcessingStatus == NayaxStockEventProcessingStatus.Applied)
            return new(eventId, NayaxStockEventApplyOutcome.Applied, "Already applied.", state.StockAdjustmentId);

        var manualRefills = await _store.GetManualMachineRefillsAsync(machine.LocalMachineId, cancellationToken);
        var duplicate = state.MatchStatus == NayaxStockEventMatchStatus.Matched
            && state.MatchedProductId is long matchedProductId
            && state.ParsedQuantity is > 0
                ? NayaxMachineStockDuplicatePolicy.FindPossibleDuplicate(
                    matchedProductId, state.ParsedQuantity.Value, state.EventDateTimeGmt, manualRefills)
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
                await ReconcileAsync(machine, state, duplicate.Value, cancellationToken),
            NayaxDuplicateResolutionChoice.ApplyAsSeparateRestock =>
                await ApplyAsSeparateRestockAsync(machine, state, duplicate.Value, cancellationToken),
            _ => new(eventId, NayaxStockEventApplyOutcome.Error, "Unknown duplicate resolution.", null)
        };
    }

    private async Task<NayaxStockEventApplyResultDto> ReconcileAsync(
        MachineIdentity machine, MachineStockEventState state, ManualRefillEvidence duplicate, CancellationToken cancellationToken)
    {
        await _store.ReconcileAsManualDuplicateAsync(
            state.Id, machine.RequiredNayaxMachineId, duplicate.StockAdjustmentId, cancellationToken);

        return new(
            state.Id,
            NayaxStockEventApplyOutcome.Reconciled,
            "Reconciled as already recorded manually; no Nayax movement was applied.",
            null);
    }

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
            return new(state.Id, NayaxStockEventApplyOutcome.Error, "Event not found for this machine.", null);

        // The same two settled states the use case answers before any lock, re-checked against what
        // is actually committed, so a resolution that arrives while another writer is committing is
        // answered with that writer's result instead of applying the event twice.
        if (locked.DuplicateResolution == NayaxDuplicateResolution.ReconciledManually)
        {
            return new(
                locked.Id,
                NayaxStockEventApplyOutcome.Reconciled,
                "Already reconciled as recorded manually; no Nayax movement was applied.",
                null);
        }

        if (locked.ProcessingStatus == NayaxStockEventProcessingStatus.Applied)
            return new(locked.Id, NayaxStockEventApplyOutcome.Applied, "Already applied.", locked.StockAdjustmentId);

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
            product?.QuantityInStock);

        switch (ruling.Decision)
        {
            case NayaxStockEventApplyDecision.NeedsReview:
                return new(locked.Id, NayaxStockEventApplyOutcome.NotMatched, ruling.Message, null);

            case NayaxStockEventApplyDecision.NegativeDiscrepancy:
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
            // resolution state together, exactly as they would have been written together.
            return ApplyMachineStockSync.Failed(locked.Id);
        }

        try
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A failed commit wrote nothing either: the disposal rolls the movement and the
            // resolution back together and the event stays unresolved, so it reports the same
            // failure. A cancelled request is not caught here.
            return ApplyMachineStockSync.Failed(locked.Id);
        }

        return new(
            locked.Id,
            NayaxStockEventApplyOutcome.Applied,
            "Applied as a separate restock: the operator explicitly overrode the possible-duplicate warning.",
            application.Applied.Single().StockAdjustmentId);
    }
}
