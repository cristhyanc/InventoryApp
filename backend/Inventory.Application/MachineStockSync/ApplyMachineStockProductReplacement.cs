using System.Globalization;
using Inventory.Application.Machines;
using Inventory.Domain.Nayax;

namespace Inventory.Application.MachineStockSync;

/// <summary>
/// The Sync Restock <strong>product replacement</strong> apply use case (issue #1326): one operator
/// confirms what a Nayax Event 510 "Machine Product Changed" alert actually did to the units - which
/// product was loaded and how many, and what happened to the units taken out - and this records all
/// of it, or none of it.
///
/// <para>Issue #1325 imported and showed those events but could only mark them "already recorded".
/// This is the other resolution, and it is deliberately a <em>separate</em> action from the ordinary
/// apply of issue #183: a 501 stock adjustment carries its own quantity and matched product, while a
/// replacement carries two product names and no quantity at all, so the operator supplies what is
/// written. <see cref="ApplyMachineStockSync"/> and
/// <see cref="ResolveMachineStockDuplicate"/> are untouched and still refuse a 510 event, and this
/// use case refuses a 501 one for the mirror-image reason.</para>
///
/// <para><strong>One event, one transaction, in the shape the other write paths already use.</strong>
/// The eligibility reads are inside the write lock, because they decide a write: the event's own
/// status says whether it may still be applied, and the incoming product's storage quantity says
/// whether it may be applied in full. The transaction is therefore <c>BEGIN IMMEDIATE</c> on SQLite -
/// the same <c>IMachineStockEventStore.BeginRefillWriteTransactionAsync</c> lock an apply batch and
/// both duplicate resolutions take - the event is re-read inside it, and two simultaneous applies of
/// the same event serialise: the loser waits, re-reads the winner's committed state and reports the
/// established <c>Already applied.</c> having written nothing. A validation refusal, a failed
/// movement, a fatal costing data-quality issue and a failed commit all leave the event pending with
/// no movement at all, because the transaction's disposal is what discards everything the write
/// staged.</para>
///
/// <para><strong>The slot's product link is never written.</strong> This use case holds no Nayax
/// client and no product-link port, so applying a replacement - including an old one - makes no
/// Nayax call and cannot change which local product an MDB position maps to. Which product a slot
/// holds keeps being read live from Nayax, exactly as before, and the replacement's own
/// <c>MatchStatus</c>/<c>MatchedProductId</c> columns are left exactly as the import left them: the
/// products the operator confirmed are recorded in the replacement's own columns, as the record of
/// what was applied rather than as a match anything else reads.</para>
/// </summary>
public sealed class ApplyMachineStockProductReplacement
{
    /// <summary>
    /// Why this action refuses a 501 stock adjustment: the mirror image of
    /// <see cref="NayaxMachineStockApplyPolicy.ProductReplacementMessage"/>, so each apply path says
    /// which one the event belongs to instead of failing obscurely.
    /// </summary>
    public const string NotAReplacementMessage =
        "This event is a stock adjustment, not a product replacement. Apply it as a refill instead.";

    private readonly IMachineStockEventStore _store;
    private readonly IMachineStockSyncLog _log;

    public ApplyMachineStockProductReplacement(IMachineStockEventStore store, IMachineStockSyncLog log)
    {
        _store = store;
        _log = log;
    }

    /// <summary>
    /// Applies one replacement. The event is the provider's and is found by the machine's Nayax
    /// number; the movements are this application's and record its local id (issue #741).
    /// </summary>
    public async Task<NayaxStockEventApplyResultDto> Handle(
        MachineIdentity machine, NayaxApplyProductReplacementRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(request);

        var choice = new NayaxProductReplacementChoice(
            request.IncomingProductId,
            request.IncomingQuantity,
            request.OutgoingDisposition,
            request.OutgoingProductId,
            request.OutgoingQuantity);

        // The choice's own shape needs no row and no lock, so a nonsensical request is refused
        // before either is taken.
        if (NayaxProductReplacementWritePolicy.Refusal(choice) is { } refusal)
            return NotApplicable(request.EventId, refusal);

        await using var transaction = await _store
            .BeginRefillWriteTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Authoritative, inside the write lock: this is the row the applied state below is written
        // to, and an apply or a resolution that committed since the request started must be seen
        // rather than written over.
        var locked = await _store.FindEventAsync(machine.RequiredNayaxMachineId, request.EventId, cancellationToken);
        if (locked is null)
            return NotFound(request.EventId);

        if (Settled(locked) is { } settled)
            return settled;

        if (locked.EventCode != NayaxMachineAlertEventCodes.MachineProductChanged)
            return NotApplicable(locked.Id, NotAReplacementMessage);

        // Both are read under the same lock, because both decide the write: whether each product is
        // still an active product of this business, and what the incoming one's storage holds.
        var incoming = await _store.FindActiveStorageProductAsync(choice.IncomingProductId, cancellationToken);
        var outgoing = choice.OutgoingProductId is long outgoingProductId
            && choice.OutgoingDisposition != NayaxReplacementOutgoingDisposition.None
                ? await _store.FindActiveStorageProductAsync(outgoingProductId, cancellationToken)
                : null;

        var ruling = NayaxProductReplacementWritePolicy.Decide(choice, incoming, outgoing);
        switch (ruling.Decision)
        {
            case NayaxReplacementWriteDecision.Invalid:
            case NayaxReplacementWriteDecision.ProductUnavailable:
                return NotApplicable(locked.Id, ruling.Message);

            case NayaxReplacementWriteDecision.InsufficientStorage:
                return new(locked.Id, NayaxStockEventApplyOutcome.InsufficientStock, ruling.Message, null);

            default:
                break;
        }

        var application = await _store
            .ApplyProductReplacementAsync(
                machine,
                new MachineStockReplacementWrite(locked.Id, locked.NayaxEventLogId, choice),
                cancellationToken)
            .ConfigureAwait(false);

        if (!application.Succeeded)
        {
            // Returned without committing: the transaction's disposal is what discards every
            // movement and the applied state together, so the event stays pending and can be
            // applied again. Logged and explained exactly as a refill's failure is (issue #1240).
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
            // A commit that fails wrote nothing either: the disposal rolls back the movements and
            // the applied state together and the event stays pending. A cancelled request is not
            // caught here. Only the exception's type name is carried: a provider's own message can
            // quote the statement or the data it failed on.
            _log.ApplyFailed(locked.Id, exception.GetType().Name, null);
            return ApplyMachineStockSync.Failed(locked.Id);
        }

        return new(
            locked.Id,
            NayaxStockEventApplyOutcome.Applied,
            AppliedMessage(choice),
            application.Applied!.IncomingStockAdjustmentId);
    }

    /// <summary>
    /// How an already-settled event answers this action, or <see langword="null"/> when it is still
    /// pending. An event already applied is the idempotent replay - it reports the movement it
    /// recorded and writes nothing a second time - and one reconciled as already recorded manually
    /// is refused, because the operator decided that replacement needs no movement at all and
    /// applying it now would move stock for units that were already accounted for.
    /// </summary>
    private static NayaxStockEventApplyResultDto? Settled(MachineStockEventState state) =>
        state switch
        {
            { DuplicateResolution: NayaxDuplicateResolution.ReconciledManually } => new(
                state.Id,
                NayaxStockEventApplyOutcome.ResolutionConflict,
                MachineStockResolutionMessages.ReconciledCannotBeApplied,
                null),

            { ProcessingStatus: NayaxStockEventProcessingStatus.Applied } => new(
                state.Id,
                NayaxStockEventApplyOutcome.Applied,
                MachineStockResolutionMessages.AlreadyApplied,
                state.StockAdjustmentId),

            _ => null,
        };

    /// <summary>
    /// What was recorded, in the operator's own terms: the units loaded, the units returned, and
    /// whether those returned units were then written off. A replacement that moved nothing says so
    /// rather than reporting an empty success.
    /// </summary>
    internal static string AppliedMessage(NayaxProductReplacementChoice choice)
    {
        var plan = NayaxProductReplacementWritePolicy.Movements(choice);
        List<string> parts = [];

        if (plan.WritesIncomingRefill)
            parts.Add($"{Units(choice.IncomingQuantity)} loaded");

        if (plan.WritesOutgoingReturn)
        {
            var returned = $"{Units(choice.OutgoingQuantity!.Value)} returned to storage";
            parts.Add(plan.WriteOffReason is { } reason
                ? $"{returned} and written off as {reason.ToString().ToLowerInvariant()}"
                : returned);
        }

        return parts.Count == 0
            ? "Product replacement recorded; no units were moved."
            : $"Product replacement recorded: {string.Join(", ", parts)}.";
    }

    private static string Units(int quantity) =>
        quantity == 1 ? "1 unit" : $"{quantity.ToString(CultureInfo.InvariantCulture)} units";

    private static NayaxStockEventApplyResultDto NotApplicable(int eventId, string message) =>
        new(eventId, NayaxStockEventApplyOutcome.NotApplicable, message, null);

    private static NayaxStockEventApplyResultDto NotFound(int eventId) =>
        new(eventId, NayaxStockEventApplyOutcome.Error, MachineStockResolutionMessages.NotFound, null);
}
