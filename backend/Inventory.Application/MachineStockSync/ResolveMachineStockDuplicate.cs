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
        long machineId, int eventId, NayaxDuplicateResolutionChoice choice, CancellationToken cancellationToken)
    {
        var state = await _store.FindEventAsync(machineId, eventId, cancellationToken);
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

        var manualRefills = await _store.GetManualMachineRefillsAsync(machineId, cancellationToken);
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
                await ReconcileAsync(machineId, state, duplicate.Value, cancellationToken),
            NayaxDuplicateResolutionChoice.ApplyAsSeparateRestock =>
                await ApplyAsSeparateRestockAsync(machineId, state, duplicate.Value, cancellationToken),
            _ => new(eventId, NayaxStockEventApplyOutcome.Error, "Unknown duplicate resolution.", null)
        };
    }

    private async Task<NayaxStockEventApplyResultDto> ReconcileAsync(
        long machineId, MachineStockEventState state, ManualRefillEvidence duplicate, CancellationToken cancellationToken)
    {
        await _store.ReconcileAsManualDuplicateAsync(
            state.Id, machineId, duplicate.StockAdjustmentId, cancellationToken);

        return new(
            state.Id,
            NayaxStockEventApplyOutcome.Reconciled,
            "Reconciled as already recorded manually; no Nayax movement was applied.",
            null);
    }

    private async Task<NayaxStockEventApplyResultDto> ApplyAsSeparateRestockAsync(
        long machineId, MachineStockEventState state, ManualRefillEvidence duplicate, CancellationToken cancellationToken)
    {
        var product = state.MatchedProductId is long productId
            ? await _store.FindStorageProductAsync(productId, cancellationToken)
            : null;

        // The operator has just explicitly overridden the possible-duplicate warning, so the
        // underlying match/quantity/storage guards still apply, but the duplicate gate itself does
        // not: that is exactly what this resolution means.
        var ruling = NayaxMachineStockApplyPolicy.Decide(
            state.ProcessingStatus,
            state.MatchStatus,
            state.NeedsReviewReason,
            state.MatchedProductId,
            state.ParsedQuantity,
            product?.QuantityInStock);

        switch (ruling.Decision)
        {
            case NayaxStockEventApplyDecision.NeedsReview:
                return new(state.Id, NayaxStockEventApplyOutcome.NotMatched, ruling.Message, null);

            case NayaxStockEventApplyDecision.NegativeDiscrepancy:
                return new(state.Id, NayaxStockEventApplyOutcome.NotApplicable, ruling.Message, null);

            case NayaxStockEventApplyDecision.InsufficientStorage:
                return new(state.Id, NayaxStockEventApplyOutcome.InsufficientStock, ruling.Message, null);

            case NayaxStockEventApplyDecision.MatchedProductMissing:
                return new(state.Id, NayaxStockEventApplyOutcome.Error, ruling.Message, null);
        }

        var application = await _store.ApplyRefillAsync(
            state.Id,
            machineId,
            state.NayaxEventLogId,
            state.MatchedProductId!.Value,
            state.ParsedQuantity!.Value,
            NayaxDuplicateResolution.AppliedAsSeparateRestock,
            duplicate.StockAdjustmentId,
            cancellationToken);

        return application.Succeeded
            ? new(
                state.Id,
                NayaxStockEventApplyOutcome.Applied,
                "Applied as a separate restock: the operator explicitly overrode the possible-duplicate warning.",
                application.StockAdjustmentId)
            : new(
                state.Id,
                NayaxStockEventApplyOutcome.Error,
                "Failed to apply this event; no inventory movement was recorded.",
                null);
    }
}
