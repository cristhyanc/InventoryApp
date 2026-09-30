using Inventory.Domain.Nayax;

namespace Inventory.Application.MachineStockSync;

/// <summary>
/// The bulk "Already recorded manually" resolution use case (issue #242): lets an operator select
/// many unresolved Sync Restock events and reconcile all of them as already represented by an
/// existing manual restock in one explicit action, instead of resolving flagged possible duplicates
/// one at a time. The app's possible-duplicate detection
/// (<see cref="NayaxMachineStockDuplicatePolicy"/>) is only a suggestion for human resolution, not a
/// precondition (issue #196): any otherwise-eligible unresolved event may be selected here, whether
/// or not the app flagged it, and evidence is linked only when a matching manual refill is actually
/// found. Every event id is resolved independently through the same idempotent
/// <see cref="IMachineStockEventStore.ReconcileAsManualDuplicateAsync"/> primitive
/// <see cref="ResolveMachineStockDuplicate"/> already uses for one flagged duplicate - the same
/// batch shape <see cref="ApplyMachineStockSync"/> already established - so each selected event keeps
/// its own durable audit/reconciliation record and outcome; one invalid or already-settled id is
/// reported back explicitly rather than silently skipped, and it never blocks or changes the outcome
/// of the others in the same request.
/// </summary>
public sealed class ResolveMachineStockEventsAsAlreadyRecorded
{
    private readonly IMachineStockEventStore _store;

    public ResolveMachineStockEventsAsAlreadyRecorded(IMachineStockEventStore store)
    {
        _store = store;
    }

    public async Task<NayaxMachineStockApplyResponseDto> Handle(
        long machineId, IReadOnlyList<int> eventIds, CancellationToken cancellationToken)
    {
        var manualRefills = await _store.GetManualMachineRefillsAsync(machineId, cancellationToken);
        var results = new List<NayaxStockEventApplyResultDto>();

        foreach (var eventId in eventIds.Distinct())
        {
            results.Add(await ResolveOneAsync(machineId, eventId, manualRefills, cancellationToken));
        }

        return new NayaxMachineStockApplyResponseDto(results);
    }

    private async Task<NayaxStockEventApplyResultDto> ResolveOneAsync(
        long machineId,
        int eventId,
        IReadOnlyList<ManualRefillEvidence> manualRefills,
        CancellationToken cancellationToken)
    {
        var state = await _store.FindEventAsync(machineId, eventId, cancellationToken);
        if (state is null)
            return new(eventId, NayaxStockEventApplyOutcome.Error, "Event not found for this machine.", null);

        // Idempotent replay: once reconciled, repeating the request returns the same outcome without
        // touching storage or the reconciliation record again.
        if (state.DuplicateResolution == NayaxDuplicateResolution.ReconciledManually)
        {
            return new(
                eventId,
                NayaxStockEventApplyOutcome.Reconciled,
                "Already reconciled as recorded manually; no Nayax movement was applied.",
                null);
        }

        // An event already applied (an ordinary apply, or a duplicate resolved as a separate
        // restock) already has a real inventory movement; it can never be retroactively marked as
        // already recorded manually instead.
        if (state.ProcessingStatus == NayaxStockEventProcessingStatus.Applied)
        {
            return new(
                eventId,
                NayaxStockEventApplyOutcome.NotApplicable,
                "This event has already been applied as a Nayax inventory movement and cannot be marked as already recorded manually.",
                null);
        }

        var duplicate = state.MatchStatus == NayaxStockEventMatchStatus.Matched
            && state.MatchedProductId is long matchedProductId
            && state.ParsedQuantity is > 0
                ? NayaxMachineStockDuplicatePolicy.FindPossibleDuplicate(
                    matchedProductId, state.ParsedQuantity.Value, state.EventDateTimeGmt, manualRefills)
                : null;

        await _store.ReconcileAsManualDuplicateAsync(
            state.Id, machineId, duplicate?.StockAdjustmentId, cancellationToken);

        return new(
            state.Id,
            NayaxStockEventApplyOutcome.Reconciled,
            "Reconciled as already recorded manually; no Nayax movement was applied.",
            null);
    }
}
