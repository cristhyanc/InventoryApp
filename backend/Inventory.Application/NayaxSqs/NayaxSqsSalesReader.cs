using Inventory.Application.Time;
using Inventory.Domain.NayaxSqs;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.NayaxSqs;

/// <summary>
/// What one poll of the shared Nayax SQS queue did (issue #1103).
/// </summary>
/// <param name="Succeeded">Whether the <c>ReceiveMessage</c> itself returned without error, empty or not.</param>
/// <param name="ConsecutivePollFailures">How many receives in a row have now failed; zero after a success.</param>
/// <param name="MessagesHandled">How many messages this poll finished with, deleted or not.</param>
/// <param name="MessagesSkipped">
/// How many messages this poll deliberately left alone because their visibility could not be
/// extended, so another receiver may hold them.
/// </param>
/// <param name="FailureType">
/// The <em>type name</em> of the failure a receive ended on, or <see langword="null"/> when it
/// succeeded. Never the exception and never its message: an AWS error can quote what it was given.
/// </param>
public sealed record NayaxSqsPollResult(
    bool Succeeded,
    int ConsecutivePollFailures,
    int MessagesHandled,
    int MessagesSkipped,
    string? FailureType);

/// <summary>
/// Moves messages off the shared Nayax SQS queue, one at a time, and acknowledges each one only once
/// its sale and every inventory-cost rebuild that sale needs are durably done (issue #1103, child of
/// #1100).
///
/// <para><strong>It holds the queue rules and nothing else.</strong> What a message <em>means</em> is
/// <c>NayaxSqsTransactionParser</c>'s (issue #1102); whose sale it is is
/// <see cref="INayaxSqsSenderDirectory"/>'s (issue #1101); what happens to the sale is
/// <see cref="ProcessNayaxSqsSale"/>'s, inside that business's own scope; and the arithmetic of
/// visibility and backoff is <see cref="NayaxSqsQueuePolicy"/>'s. This class decides when to receive,
/// when to extend, when to delete and when to wait.</para>
///
/// <para><strong>Deleting last is the whole correctness argument.</strong> A standard SQS queue can
/// deliver a message more than once and cannot be trusted to deliver it exactly once, so nothing here
/// relies on that: duplicates are refused by the database's unique index on
/// <c>(BusinessId, TransactionID)</c>, each save is one write-locked transaction, what still has to
/// be replayed is a durable row rather than a value in memory, and the message is deleted only after
/// all of it. A message deleted one step too early is a sale or a cost rebuild nothing will ever
/// do again.</para>
///
/// <para><strong>Every message in a batch keeps its full visibility window.</strong> Messages are
/// processed one at a time - the production store is a single SQLite file with one writer - so the
/// tenth message of a batch may wait a long time behind the first nine. The reader therefore extends
/// the visibility of every <em>not-yet-finished</em> message immediately after the receive and again
/// whenever half the timeout has elapsed, waiting ones included. A message whose extension failed is
/// not processed in this batch at all: the failure means another receiver may now hold it, and two
/// readers working on one transaction is the race the extension exists to prevent.</para>
///
/// <para><strong>Nothing it does can crash the host.</strong> A failed receive is counted, logged by
/// type and backed off with jitter up to a minute; every per-message failure becomes a disposition.
/// The only thing that ends the loop is cancellation.</para>
/// </summary>
public sealed class NayaxSqsSalesReader
{
    private readonly INayaxSqsQueueClient _queue;
    private readonly INayaxSqsReaderScopes _scopes;
    private readonly INayaxSqsReaderSettings _settings;
    private readonly INayaxSqsReaderPacing _pacing;
    private readonly NayaxSqsReaderHealth _health;
    private readonly INayaxSqsReaderLog _log;
    private readonly IClock _clock;

    private DateTime? _deadLetterDepthReadAtUtc;

    public NayaxSqsSalesReader(
        INayaxSqsQueueClient queue,
        INayaxSqsReaderScopes scopes,
        INayaxSqsReaderSettings settings,
        INayaxSqsReaderPacing pacing,
        NayaxSqsReaderHealth health,
        INayaxSqsReaderLog log,
        IClock clock)
    {
        _queue = queue;
        _scopes = scopes;
        _settings = settings;
        _pacing = pacing;
        _health = health;
        _log = log;
        _clock = clock;
    }

    /// <summary>
    /// Polls until <paramref name="stoppingToken"/> is cancelled. With no queue configured it logs one
    /// information line and returns, which is what makes an environment with no
    /// <c>NayaxSqs</c> settings start exactly as it did before.
    ///
    /// <para>Public and driveable so it can be exercised as what it is - a loop with a beginning and
    /// an end - rather than through a hosted service's start/stop timing, exactly as
    /// <c>MemberProfileRefreshWorker.DrainAsync</c> is.</para>
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (!_settings.IsConfigured)
        {
            _log.ReaderNotConfigured();
            return;
        }

        _log.ReaderStarted();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var result = await PollOnceAsync(stoppingToken).ConfigureAwait(false);
                if (result.Succeeded)
                {
                    continue;
                }

                var backoff = NayaxSqsQueuePolicy.PollBackoff(
                    result.ConsecutivePollFailures, _pacing.NextJitterSample());

                _log.PollFailed(
                    result.FailureType ?? nameof(Exception), result.ConsecutivePollFailures, backoff);

                await _pacing.DelayAsync(backoff, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown. An in-flight message is simply left for redelivery, which is safe precisely
            // because nothing is deleted before it is durably done.
        }
        finally
        {
            _log.ReaderStopped();
        }
    }

    /// <summary>
    /// One receive and the batch it returned, plus the dead-letter depth read when one is due.
    /// Public so a test can drive exactly one poll.
    /// </summary>
    public async Task<NayaxSqsPollResult> PollOnceAsync(CancellationToken cancellationToken)
    {
        await ReadDeadLetterDepthIfDueAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<NayaxSqsReceivedMessage> received;
        try
        {
            received = await _queue.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (NotCancellation(exception, cancellationToken))
        {
            return new NayaxSqsPollResult(
                Succeeded: false,
                ConsecutivePollFailures: _health.RecordPollFailure(),
                MessagesHandled: 0,
                MessagesSkipped: 0,
                FailureType: exception.GetType().Name);
        }

        // An empty receive is a successful poll: the queue answered and had nothing to hand over,
        // which is the normal state of a quiet fleet. Recording it is what keeps a healthy idle
        // reader from looking stalled.
        _health.RecordSuccessfulPoll(_clock.UtcNow);

        var (handled, skipped) = await ProcessBatchAsync(received, cancellationToken).ConfigureAwait(false);

        return new NayaxSqsPollResult(
            Succeeded: true,
            ConsecutivePollFailures: 0,
            MessagesHandled: handled,
            MessagesSkipped: skipped,
            FailureType: null);
    }

    /// <summary>
    /// Reads the dead-letter queue's depth every
    /// <see cref="NayaxSqsQueuePolicy.DeadLetterDepthInterval"/>, logs a warning while it is above
    /// zero, and publishes it on the health record.
    ///
    /// <para>It never receives from or deletes from that queue, and there is no method on the queue
    /// port that could: replaying it is the owner's own AWS console procedure after the cause has
    /// been fixed (README.md § Nayax SQS sales reader). A depth that cannot be read is reported and
    /// changes nothing about polling the main queue.</para>
    /// </summary>
    private async Task ReadDeadLetterDepthIfDueAsync(CancellationToken cancellationToken)
    {
        if (!_settings.HasDeadLetterQueue)
        {
            return;
        }

        var now = _clock.UtcNow;
        if (_deadLetterDepthReadAtUtc is { } last
            && now - last < NayaxSqsQueuePolicy.DeadLetterDepthInterval)
        {
            return;
        }

        // Stamped before the read, not after: a read that fails must not make the next poll try
        // again immediately, which would turn an unavailable queue into an extra call per poll.
        _deadLetterDepthReadAtUtc = now;

        try
        {
            if (await _queue.GetDeadLetterDepthAsync(cancellationToken).ConfigureAwait(false) is not { } depth)
            {
                return;
            }

            _health.RecordDeadLetterDepth(depth, now);

            if (depth > 0)
            {
                _log.DeadLetterQueueNotEmpty(depth);
            }
        }
        catch (Exception exception) when (NotCancellation(exception, cancellationToken))
        {
            _log.DeadLetterDepthReadFailed(exception.GetType().Name);
        }
    }

    /// <summary>
    /// Works through one received batch in order, keeping every unfinished message's visibility
    /// window full.
    /// </summary>
    private async Task<(int Handled, int Skipped)> ProcessBatchAsync(
        IReadOnlyList<NayaxSqsReceivedMessage> received, CancellationToken cancellationToken)
    {
        var batch = Deduplicate(received);
        if (batch.Count == 0)
        {
            return (0, 0);
        }

        var visibility = _settings.VisibilityTimeout;
        var extensionInterval = NayaxSqsQueuePolicy.ExtensionInterval(visibility);

        // Immediately after the receive, before the first message is touched: the clock on every
        // message in this batch started when SQS handed it over, including the ones that will wait.
        var extendedAtUtc = await ExtendUnfinishedAsync(batch, visibility, cancellationToken)
            .ConfigureAwait(false);

        var handled = 0;

        foreach (var item in batch)
        {
            // Shutdown stops the batch rather than rushing it: the messages not reached keep their
            // extended visibility and are redelivered.
            cancellationToken.ThrowIfCancellationRequested();

            if (item.Skipped)
            {
                continue;
            }

            if (_clock.UtcNow - extendedAtUtc >= extensionInterval)
            {
                extendedAtUtc = await ExtendUnfinishedAsync(batch, visibility, cancellationToken)
                    .ConfigureAwait(false);

                if (item.Skipped)
                {
                    continue;
                }
            }

            await HandleOneAsync(item, visibility, cancellationToken).ConfigureAwait(false);
            item.Finished = true;
            handled++;
        }

        return (handled, batch.Count(item => item.Skipped));
    }

    /// <summary>
    /// Resets every message of the batch that is neither finished nor skipped to the full visibility
    /// timeout, and answers the instant the extension was made at.
    ///
    /// <para>A message whose extension failed is marked skipped and is not processed in this batch:
    /// the failure means this reader can no longer be sure it still holds the message, and the one
    /// thing worse than processing it late is processing it beside another receiver.</para>
    /// </summary>
    private async Task<DateTime> ExtendUnfinishedAsync(
        List<BatchItem> batch, TimeSpan visibility, CancellationToken cancellationToken)
    {
        var extendedAtUtc = _clock.UtcNow;

        foreach (var item in batch.Where(candidate => !candidate.Finished && !candidate.Skipped))
        {
            try
            {
                await _queue
                    .ChangeVisibilityAsync(item.Message.ReceiptHandle, visibility, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (NotCancellation(exception, cancellationToken))
            {
                item.Skipped = true;
                _log.VisibilityExtensionFailed(item.Message.MessageId, exception.GetType().Name);
            }
        }

        return extendedAtUtc;
    }

    /// <summary>
    /// Attributes one message to a business, processes it in that business's own scope, and then does
    /// exactly one of the three things the outcome table allows: delete it, back it off, or release it
    /// towards the dead-letter queue.
    /// </summary>
    private async Task HandleOneAsync(
        BatchItem item, TimeSpan visibility, CancellationToken cancellationToken)
    {
        var message = item.Message;

        var attribution = await AttributeAsync(message.SenderId, cancellationToken).ConfigureAwait(false);

        var outcome = attribution.Refusal
            ?? await ProcessInBusinessScopeAsync(attribution.BusinessId!.Value, message, cancellationToken)
                .ConfigureAwait(false);

        _health.RecordMessageProcessed(_clock.UtcNow);
        _log.MessageHandled(
            message.MessageId, attribution.BusinessId, outcome.Disposition, outcome.Reason);

        switch (outcome.Disposition)
        {
            case NayaxSqsDisposition.Done:
                await DeleteAsync(message, cancellationToken).ConfigureAwait(false);
                break;

            case NayaxSqsDisposition.Transient:
                await ChangeVisibilityAsync(
                        message,
                        NayaxSqsQueuePolicy.TransientVisibility(message.ApproximateReceiveCount, visibility),
                        cancellationToken)
                    .ConfigureAwait(false);
                break;

            case NayaxSqsDisposition.Permanent:
                await ChangeVisibilityAsync(
                        message, NayaxSqsQueuePolicy.PermanentVisibility, cancellationToken)
                    .ConfigureAwait(false);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// The sender gates, every one of them before a business scope exists and therefore before
    /// anything of that business is read or written: a message with no sender attribute, one no
    /// business has recorded, one whose mapping is switched off, and one whose business is no longer
    /// trading are each refused here.
    /// </summary>
    private async Task<(int? BusinessId, NayaxSqsProcessingOutcome? Refusal)> AttributeAsync(
        string? senderId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(senderId))
        {
            return (null, NayaxSqsProcessingOutcome.Permanent(NayaxSqsProcessingReason.SenderMissing));
        }

        NayaxSqsSenderAttribution? attribution;
        try
        {
            attribution = await _scopes
                .FindBusinessBySenderAsync(senderId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (NotCancellation(exception, cancellationToken))
        {
            return (null, NayaxSqsProcessingOutcome.Transient(NayaxSqsProcessingReason.StoreUnavailable));
        }

        if (attribution is null)
        {
            return (null, NayaxSqsProcessingOutcome.Permanent(NayaxSqsProcessingReason.SenderUnknown));
        }

        if (!attribution.IsEnabled)
        {
            return (
                attribution.BusinessId,
                NayaxSqsProcessingOutcome.Permanent(NayaxSqsProcessingReason.SenderDisabled));
        }

        if (attribution.BusinessStatus != BusinessStatus.Active)
        {
            return (
                attribution.BusinessId,
                NayaxSqsProcessingOutcome.Permanent(NayaxSqsProcessingReason.BusinessNotActive));
        }

        return (attribution.BusinessId, null);
    }

    private async Task<NayaxSqsProcessingOutcome> ProcessInBusinessScopeAsync(
        int businessId, NayaxSqsReceivedMessage message, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateForBusiness(businessId);

        return await scope
            .ProcessAsync(message.MessageId, message.Body, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task DeleteAsync(NayaxSqsReceivedMessage message, CancellationToken cancellationToken)
    {
        try
        {
            // The handle from this message's latest receive, which is the only one SQS will accept.
            await _queue.DeleteAsync(message.ReceiptHandle, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (NotCancellation(exception, cancellationToken))
        {
            _log.DeleteFailed(message.MessageId, exception.GetType().Name);
        }
    }

    private async Task ChangeVisibilityAsync(
        NayaxSqsReceivedMessage message, TimeSpan visibility, CancellationToken cancellationToken)
    {
        try
        {
            await _queue
                .ChangeVisibilityAsync(message.ReceiptHandle, visibility, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (NotCancellation(exception, cancellationToken))
        {
            _log.VisibilityChangeFailed(message.MessageId, exception.GetType().Name);
        }
    }

    /// <summary>
    /// One entry per message id, in the order the queue returned them, carrying the handle and the
    /// receive count of that message's <em>latest</em> occurrence in the batch.
    ///
    /// <para>A standard queue may hand the same message over twice in one receive. Working through
    /// both copies would process one transaction twice - harmless, because the database refuses the
    /// duplicate, but it would also delete with the older handle, which SQS no longer accepts.</para>
    /// </summary>
    private static List<BatchItem> Deduplicate(IReadOnlyList<NayaxSqsReceivedMessage> received)
    {
        var items = new List<BatchItem>(received.Count);
        var positions = new Dictionary<string, int>(received.Count, StringComparer.Ordinal);

        foreach (var message in received)
        {
            if (positions.TryGetValue(message.MessageId, out var position))
            {
                items[position].Message = message;
                continue;
            }

            positions[message.MessageId] = items.Count;
            items.Add(new BatchItem(message));
        }

        return items;
    }

    /// <summary>
    /// Whether <paramref name="exception"/> is something to handle rather than the host shutting
    /// down.
    /// </summary>
    private static bool NotCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested;

    /// <summary>One message of the batch and where the reader has got to with it.</summary>
    private sealed class BatchItem
    {
        public BatchItem(NayaxSqsReceivedMessage message) => Message = message;

        /// <summary>The latest receive of this message id, whose receipt handle is the usable one.</summary>
        public NayaxSqsReceivedMessage Message { get; set; }

        /// <summary>The reader has finished with it, so it no longer needs extending.</summary>
        public bool Finished { get; set; }

        /// <summary>Its visibility could not be extended, so it is left entirely alone this batch.</summary>
        public bool Skipped { get; set; }
    }
}
