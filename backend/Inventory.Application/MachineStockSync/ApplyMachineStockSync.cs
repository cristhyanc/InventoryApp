using Inventory.Domain.Nayax;

namespace Inventory.Application.MachineStockSync;

/// <summary>
/// The machine-level Sync Restock apply use case (issue #183): applies exactly the imported events
/// the operator explicitly accepted, one at a time. Each event is decided by the deterministic
/// Domain <see cref="NayaxMachineStockApplyPolicy"/> and then, only if it may be applied, recorded
/// through <see cref="IMachineStockEventStore.ApplyRefillAsync"/> in its own transaction - so one
/// unresolved or failing event never stops or corrupts the others in the batch.
/// </summary>
public sealed class ApplyMachineStockSync
{
    private readonly IMachineStockEventStore _store;

    public ApplyMachineStockSync(IMachineStockEventStore store)
    {
        _store = store;
    }

    public async Task<NayaxMachineStockApplyResponseDto> Handle(
        long machineId, IReadOnlyList<int> eventIds, CancellationToken cancellationToken)
    {
        var results = new List<NayaxStockEventApplyResultDto>();

        foreach (var eventId in eventIds.Distinct())
        {
            results.Add(await ApplyOneAsync(machineId, eventId, cancellationToken));
        }

        return new NayaxMachineStockApplyResponseDto(results);
    }

    private async Task<NayaxStockEventApplyResultDto> ApplyOneAsync(
        long machineId, int eventId, CancellationToken cancellationToken)
    {
        var state = await _store.FindEventAsync(machineId, eventId, cancellationToken);
        if (state is null)
            return new(eventId, NayaxStockEventApplyOutcome.Error, "Event not found for this machine.", null);

        var product = state.MatchedProductId is long productId
            ? await _store.FindStorageProductAsync(productId, cancellationToken)
            : null;

        var ruling = NayaxMachineStockApplyPolicy.Decide(
            state.ProcessingStatus,
            state.MatchStatus,
            state.NeedsReviewReason,
            state.MatchedProductId,
            state.ParsedQuantity,
            product?.QuantityInStock);

        switch (ruling.Decision)
        {
            case NayaxStockEventApplyDecision.AlreadyApplied:
                return new(eventId, NayaxStockEventApplyOutcome.Applied, ruling.Message, state.StockAdjustmentId);

            case NayaxStockEventApplyDecision.NeedsReview:
                return new(eventId, NayaxStockEventApplyOutcome.NotMatched, ruling.Message, null);

            case NayaxStockEventApplyDecision.NegativeDiscrepancy:
                return new(eventId, NayaxStockEventApplyOutcome.NotApplicable, ruling.Message, null);

            case NayaxStockEventApplyDecision.InsufficientStorage:
                return new(eventId, NayaxStockEventApplyOutcome.InsufficientStock, ruling.Message, null);

            case NayaxStockEventApplyDecision.MatchedProductMissing:
                return new(eventId, NayaxStockEventApplyOutcome.Error, ruling.Message, null);

            default:
                var application = await _store.ApplyRefillAsync(
                    state.Id,
                    machineId,
                    state.NayaxEventLogId,
                    state.MatchedProductId!.Value,
                    state.ParsedQuantity!.Value,
                    cancellationToken);

                return application.Succeeded
                    ? new(eventId, NayaxStockEventApplyOutcome.Applied, ruling.Message, application.StockAdjustmentId)
                    : new(eventId, NayaxStockEventApplyOutcome.Error,
                        "Failed to apply this event; no inventory movement was recorded.", null);
        }
    }
}
