using Inventory.Application.Machines;
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
///
/// <para><strong>Each event is resolved inside the same write lock the single resolution takes</strong>
/// (issue #1248). "Is this event still pending?" decides a write, so each id's own
/// <c>BEGIN IMMEDIATE</c> transaction is taken before the event is re-read and stamped: an apply of
/// the same event that commits in between is seen, and that id is reported <c>NotApplicable</c>
/// rather than marked as never applied on top of its movement. The batch's per-event contract is
/// unchanged - every id still answers for itself and one settled id never blocks the others.</para>
/// </summary>
public sealed class ResolveMachineStockEventsAsAlreadyRecorded
{
    private readonly IMachineStockEventStore _store;

    public ResolveMachineStockEventsAsAlreadyRecorded(IMachineStockEventStore store)
    {
        _store = store;
    }

    public async Task<NayaxMachineStockApplyResponseDto> Handle(
        MachineIdentity machine, IReadOnlyList<int> eventIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);

        // Events by the provider's number, manual refills by the local machine (issue #741).
        var manualRefills = await _store.GetManualMachineRefillsAsync(machine.LocalMachineId, cancellationToken);
        var results = new List<NayaxStockEventApplyResultDto>();

        foreach (var eventId in eventIds.Distinct())
        {
            results.Add(await ResolveOneAsync(machine.RequiredNayaxMachineId, eventId, manualRefills, cancellationToken));
        }

        return new NayaxMachineStockApplyResponseDto(results);
    }

    private async Task<NayaxStockEventApplyResultDto> ResolveOneAsync(
        long nayaxMachineId,
        int eventId,
        IReadOnlyList<ManualRefillEvidence> manualRefills,
        CancellationToken cancellationToken)
    {
        var state = await _store.FindEventAsync(nayaxMachineId, eventId, cancellationToken);
        if (state is null)
            return NotFound(eventId);

        if (Settled(state) is { } settled)
            return settled;

        // Evidence only: which manual refill this reconciliation is recorded against. It decides no
        // write, so it is read outside the lock, exactly as the single resolution reads it.
        var duplicate = state.MatchStatus == NayaxStockEventMatchStatus.Matched
            && state.MatchedProductId is long matchedProductId
            && state.ParsedQuantity is > 0
                ? NayaxMachineStockDuplicatePolicy.FindPossibleDuplicate(
                    matchedProductId, state.EventDateTimeGmt, manualRefills)
                : null;

        await using var transaction = await _store
            .BeginRefillWriteTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Authoritative, inside the write lock (issue #1248): this is the row the stamp below is
        // written to, and an apply that committed since the read above must be seen rather than
        // overwritten.
        var locked = await _store.FindEventAsync(nayaxMachineId, eventId, cancellationToken);
        if (locked is null)
            return NotFound(eventId);

        if (Settled(locked) is { } raced)
            return raced;

        await _store.ReconcileAsManualDuplicateAsync(
            locked.Id, nayaxMachineId, duplicate?.Match.StockAdjustmentId, cancellationToken);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new(
            locked.Id,
            NayaxStockEventApplyOutcome.Reconciled,
            MachineStockResolutionMessages.Reconciled,
            null);
    }

    /// <summary>
    /// How an already-settled event answers this bulk resolution, or <see langword="null"/> when it
    /// is still pending. Repeating the request on a reconciled event is the idempotent replay; an
    /// event that already has a real inventory movement - an ordinary apply, or a duplicate
    /// resolved as a separate restock - can never be retroactively marked as already recorded
    /// manually instead, and says so for itself without affecting the other ids in the request.
    /// </summary>
    private static NayaxStockEventApplyResultDto? Settled(MachineStockEventState state) =>
        state switch
        {
            { DuplicateResolution: NayaxDuplicateResolution.ReconciledManually } => new(
                state.Id,
                NayaxStockEventApplyOutcome.Reconciled,
                MachineStockResolutionMessages.AlreadyReconciled,
                null),

            { ProcessingStatus: NayaxStockEventProcessingStatus.Applied } => new(
                state.Id,
                NayaxStockEventApplyOutcome.NotApplicable,
                MachineStockResolutionMessages.AppliedCannotBeReconciled,
                null),

            _ => null,
        };

    private static NayaxStockEventApplyResultDto NotFound(int eventId) =>
        new(eventId, NayaxStockEventApplyOutcome.Error, MachineStockResolutionMessages.NotFound, null);
}
