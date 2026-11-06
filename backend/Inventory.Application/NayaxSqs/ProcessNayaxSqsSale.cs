using Inventory.Application.Nayax;
using Inventory.Application.SalesSync;
using Inventory.Application.Time;

namespace Inventory.Application.NayaxSqs;

/// <summary>
/// Everything one Nayax SQS message causes inside <strong>one</strong> business (issue #1103): the
/// gates it has to pass, the sale it is saved as, and the inventory-cost rebuilds that sale needs,
/// ending in the one answer the reader acts on.
///
/// <para><strong>It is resolved from a service scope whose current business is already that
/// business</strong>, published the way <c>ConnectedMachineSnapshotCommand</c> publishes its
/// per-business context. Everything below therefore happens exactly as it would for a member of that
/// business: the same <c>AppDbContext</c> with the same tenant query filters, the same
/// <c>BusinessOwnershipEnforcer</c> on every save, the same credential read, the same status gate and
/// the same plan check. Nothing here takes a business id, and there is no unscoped context anywhere
/// on the path - the only thing that crossed a business boundary is
/// <see cref="INayaxSqsSenderDirectory"/>, before this use case was reached.</para>
///
/// <para><strong>The order is the rule.</strong> The message is read first, because a message that is
/// not a sale at all needs no connection, no plan and no fleet read - and asking Nayax about one
/// would spend a business's own request on a prepaid top-up. Then the gates, all of them, before
/// anything is written. Then one durable write. Then the rebuild drain. A message is reported as
/// finished only when the sale and every rebuild it needs are both done.</para>
///
/// <para><strong>It never throws for a message.</strong> The reader has to decide what to do with
/// every message, so every failure becomes a disposition: a Nayax or database failure is
/// <see cref="NayaxSqsDisposition.Transient"/> and the message is kept, and the only thing that
/// propagates is cancellation, which is the host shutting down. A failure that is not recognised is
/// transient too, on purpose: deleting a message because this application threw something unexpected
/// would lose a real sale, and the queue's own <c>maxReceiveCount</c> is what stops a message
/// retrying forever.</para>
///
/// <para><strong>Nothing it logs or raises can carry a payload.</strong> It never formats a message
/// body, a receipt handle, a card value or a customer value into anything: what leaves it is a
/// disposition and a fixed reason code.</para>
/// </summary>
public sealed class ProcessNayaxSqsSale
{
    private readonly ILatestNayaxSalesStore _store;
    private readonly INayaxRequestCredentialProvider _credentials;
    private readonly INayaxLynxClient _nayax;
    private readonly INayaxSqsSlotProductResolver _slots;
    private readonly INayaxSqsSenderActivityStore _senderActivity;
    private readonly IClock _clock;

    public ProcessNayaxSqsSale(
        ILatestNayaxSalesStore store,
        INayaxRequestCredentialProvider credentials,
        INayaxLynxClient nayax,
        INayaxSqsSlotProductResolver slots,
        INayaxSqsSenderActivityStore senderActivity,
        IClock clock)
    {
        _store = store;
        _credentials = credentials;
        _nayax = nayax;
        _slots = slots;
        _senderActivity = senderActivity;
        _clock = clock;
    }

    /// <summary>
    /// Reads, gates, saves and replays one message, and answers what the reader should do with it.
    /// </summary>
    /// <param name="messageId">
    /// The queue's own id for the delivery, which is persisted as the sale's <c>SqsFirstMessageId</c>
    /// and is the only identifier of a message that is safe to keep.
    /// </param>
    /// <param name="messageBody">The raw body, read only by the parser and never logged or echoed.</param>
    /// <param name="cancellationToken">Shutdown. The one thing that propagates out of here.</param>
    public async Task<NayaxSqsProcessingOutcome> Handle(
        string messageId,
        string? messageBody,
        CancellationToken cancellationToken)
    {
        var parsed = NayaxSqsTransactionParser.Parse(messageBody);

        switch (parsed.Kind)
        {
            case NayaxSqsOutcomeKind.Invalid:
            case NayaxSqsOutcomeKind.Unsupported:
            case NayaxSqsOutcomeKind.FreeVend:
                // Retrying cannot change what the message says, so it is released towards the
                // dead-letter queue as a sample rather than deleted: a free vend in particular is
                // waiting for a human to look at a real one (issue #1102).
                return NayaxSqsProcessingOutcome.Permanent(NayaxSqsProcessingReason.MessageRefused);

            case NayaxSqsOutcomeKind.Revalue:
            case NayaxSqsOutcomeKind.Multivend:
                // Understood, and nothing to save: a prepaid credit top-up is not a product sale, and
                // a multivend is left to the hourly lastSales run by parent #1100.
                await RecordSenderActivityAsync(cancellationToken).ConfigureAwait(false);
                return NayaxSqsProcessingOutcome.Done(NayaxSqsProcessingReason.NotASaleToStore);

            default:
                break;
        }

        // Only the two saved kinds reach here, and the parser guarantees both carry a report and a
        // classified status.
        var report = parsed.Report!;

        var gate = await ResolveProviderFactsAsync(report, parsed.ProductSlot, cancellationToken)
            .ConfigureAwait(false);
        if (gate is { } refusal)
        {
            return refusal;
        }

        return await SaveAndReplayAsync(messageId, report, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The gates that need the business's own Nayax account, and the one lookup a message cannot
    /// answer itself. Answers <see langword="null"/> when every gate passed.
    ///
    /// <para>The credential read is the existing <see cref="INayaxRequestCredentialProvider"/> gate,
    /// applied exactly as the request-driven sync applies it: the connection has to be usable and the
    /// business's plan has to include the sales provider, and a refusal happens before the token is
    /// decrypted. It is reported as <em>transient</em> rather than permanent, which is the one place
    /// this reader's table needs a judgement: an unknown sender and a foreign machine are statements
    /// about the message, while a disconnected business or a downgraded plan is a statement about
    /// today - reconnecting makes the very same message saveable, so the sale is parked for the next
    /// receive instead of being pushed at the dead-letter queue.</para>
    ///
    /// <para>The machine gate is what stops a message naming any machine in the world from writing a
    /// sale into this business. The cached fleet list answers first, because a queue draining a
    /// hundred messages must not be a hundred fleet reads; a machine the cached list does not hold is
    /// then checked against <em>one</em> fresh read, so a machine added minutes ago is not refused
    /// for ten minutes. A machine neither list holds belongs to somebody else, and no amount of
    /// retrying changes that.</para>
    /// </summary>
    private async Task<NayaxSqsProcessingOutcome?> ResolveProviderFactsAsync(
        NayaxLastSalesReport report,
        int? productSlot,
        CancellationToken cancellationToken)
    {
        try
        {
            await _credentials.GetForOperationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (NayaxNotConnectedException)
        {
            return NayaxSqsProcessingOutcome.Transient(NayaxSqsProcessingReason.ConnectionUnavailable);
        }
        catch (Exception exception) when (NotCancellation(exception, cancellationToken))
        {
            return NayaxSqsProcessingOutcome.Transient(NayaxSqsProcessingReason.ProviderUnavailable);
        }

        try
        {
            if (!await OwnsMachineAsync(report.MachineID, cancellationToken).ConfigureAwait(false))
            {
                return NayaxSqsProcessingOutcome.Permanent(NayaxSqsProcessingReason.MachineNotInFleet);
            }

            // The slot fallback of issue #1102, and only when the message stated no usable product
            // id of its own. A slot that resolves to nothing leaves the sale for the existing
            // unambiguous-name match and then the uncosted path, exactly as an unmatched lastSales
            // item already is.
            if (report.NayaxProductId is null && productSlot is not null)
            {
                report.NayaxProductId = await _slots
                    .ResolveAsync(report.MachineID, productSlot, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (NotCancellation(exception, cancellationToken))
        {
            return NayaxSqsProcessingOutcome.Transient(NayaxSqsProcessingReason.ProviderUnavailable);
        }

        return null;
    }

    private async Task<bool> OwnsMachineAsync(long machineId, CancellationToken cancellationToken)
    {
        var cached = await _nayax.GetMachinesAsync(cancellationToken).ConfigureAwait(false);
        if (cached.Exists(machine => machine.MachineID == machineId))
        {
            return true;
        }

        var fresh = await _nayax.GetMachinesFreshAsync(cancellationToken).ConfigureAwait(false);
        return fresh.Exists(machine => machine.MachineID == machineId);
    }

    /// <summary>
    /// The durable half: one write-locked transaction that stores or enriches the sale and records
    /// what has to be replayed, then the drain that replays it.
    ///
    /// <para>The message is finished with only when <em>this sale's</em> products have no pending
    /// rebuild left. A different product of the same business still failing deliberately does not
    /// hold this message: that message's own sale is durably correct, and keeping it would make one
    /// unreplayable product's history stop every later sale from being acknowledged.</para>
    /// </summary>
    private async Task<NayaxSqsProcessingOutcome> SaveAndReplayAsync(
        string messageId,
        NayaxLastSalesReport report,
        CancellationToken cancellationToken)
    {
        NayaxSqsSalePersistResult persisted;
        InventoryCostRebuildDrainResult drained;

        try
        {
            persisted = await _store
                .PersistSqsSaleAsync(
                    new NayaxSqsSaleInput(
                        report,

                        // The message's own Transaction Status ID, which the store saves exactly as
                        // given. The parser has already refused every status that is not one of the
                        // application's established ones, so an unexpected value never reaches here
                        // and can never become a completed sale.
                        report.TransactionStatusId!.Value,
                        messageId,
                        _clock.UtcNow),
                    cancellationToken)
                .ConfigureAwait(false);

            // After the commit, never inside it: a replay is a long chronological read-and-write over
            // a product's whole history, and holding the write lock for it would block every
            // interactive request on the one SQLite file.
            drained = await _store.RebuildInventoryCostsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (NotCancellation(exception, cancellationToken))
        {
            return NayaxSqsProcessingOutcome.Transient(NayaxSqsProcessingReason.StoreUnavailable);
        }

        if (persisted.AffectedProductIds.Any(drained.StillPendingProductIds.Contains))
        {
            // The sale is saved and the message is not: the next receive finds the stored sale, sees
            // the pending rebuild and retries exactly that.
            return NayaxSqsProcessingOutcome.Transient(NayaxSqsProcessingReason.RebuildPending);
        }

        await RecordSenderActivityAsync(cancellationToken).ConfigureAwait(false);

        return NayaxSqsProcessingOutcome.Done(
            persisted.Inserted ? NayaxSqsProcessingReason.Saved : NayaxSqsProcessingReason.AlreadyStored);
    }

    /// <summary>
    /// Stamps the business's sender row with "a message arrived just now" (issue #1101's column).
    ///
    /// <para>A failure here is swallowed rather than turned into a disposition, and that is the right
    /// trade: the sale is already durably stored and replayed, and refusing to acknowledge the
    /// message over a cosmetic instant would redeliver a transaction that is finished with. The next
    /// message's stamp is the repair.</para>
    /// </summary>
    private async Task RecordSenderActivityAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _senderActivity
                .RecordMessageReceivedAsync(_clock.UtcNow, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (NotCancellation(exception, cancellationToken))
        {
            // Deliberately ignored; see the summary.
        }
    }

    /// <summary>
    /// Whether <paramref name="exception"/> is something to turn into a disposition rather than the
    /// host shutting down. Shutdown has to propagate: an in-flight message is either finished and
    /// deleted or left for redelivery, never reported as a failure of its own.
    /// </summary>
    private static bool NotCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested;
}
