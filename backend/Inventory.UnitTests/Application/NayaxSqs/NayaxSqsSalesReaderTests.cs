using Inventory.Application.NayaxSqs;
using Inventory.Domain.NayaxSqs;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.NayaxSqs;

/// <summary>
/// Issue #1103: the queue reader's own loop, against a fake SQS client. What is asserted here is
/// everything that happens to a <em>message</em> - which gate refused it, whether it was deleted,
/// what visibility it was left with, and which receipt handle was used - because that is the half of
/// this feature a database cannot show.
///
/// <para>Deleting last is the invariant most of these cases are about. A standard queue can deliver a
/// message twice and cannot be trusted to deliver it once, so the reader may delete only what is
/// durably finished; every other outcome leaves the message on the queue with a visibility that says
/// when to come back to it.</para>
/// </summary>
public class NayaxSqsSalesReaderTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);

    private const string Sender = "AIDAFICTIONALSENDER01";
    private const int BusinessId = 7;

    /// <summary>
    /// A message whose sale and rebuilds are durably done is the one thing that is deleted, and it is
    /// deleted with the handle from its latest receive.
    /// </summary>
    [Fact]
    public async Task A_finished_message_is_deleted_with_its_latest_receipt_handle()
    {
        var harness = new Harness();
        harness.Queue.Returns(Message("m1", "rh-1"));
        harness.Scopes.WithSender(Sender, BusinessId);

        await harness.Reader.PollOnceAsync(CancellationToken.None);

        Assert.Equal(["rh-1"], harness.Queue.Deleted);
        Assert.Equal([(BusinessId, "m1")], harness.Scopes.Processed);
    }

    /// <summary>
    /// A standard queue may hand the same message over twice in one receive. The later copy's handle
    /// is the usable one - SQS refuses the earlier one - and the transaction is worked through once.
    /// </summary>
    [Fact]
    public async Task A_message_repeated_in_one_batch_is_processed_once_with_the_later_handle()
    {
        var harness = new Harness();
        harness.Queue.Returns(Message("m1", "rh-old"), Message("m1", "rh-new"));
        harness.Scopes.WithSender(Sender, BusinessId);

        await harness.Reader.PollOnceAsync(CancellationToken.None);

        Assert.Single(harness.Scopes.Processed);
        Assert.Equal(["rh-new"], harness.Queue.Deleted);
    }

    /// <summary>
    /// Transient: not deleted, and its visibility set to the bounded backoff the policy decides from
    /// the queue's own receive count.
    /// </summary>
    [Fact]
    public async Task A_transient_outcome_is_not_deleted_and_is_backed_off_by_its_receive_count()
    {
        var harness = new Harness();
        harness.Queue.Returns(Message("m1", "rh-1", receiveCount: 3));
        harness.Scopes.WithSender(Sender, BusinessId);
        harness.Scopes.Outcome =
            _ => NayaxSqsProcessingOutcome.Transient(NayaxSqsProcessingReason.StoreUnavailable);

        await harness.Reader.PollOnceAsync(CancellationToken.None);

        Assert.Empty(harness.Queue.Deleted);
        Assert.Equal(TimeSpan.FromSeconds(90), harness.LastVisibilityOf("rh-1"));
    }

    /// <summary>
    /// Permanent: not deleted either, and released immediately so the queue's own maxReceiveCount
    /// carries it to the dead-letter queue as a sample.
    /// </summary>
    [Fact]
    public async Task A_permanent_outcome_is_not_deleted_and_is_released_towards_the_dead_letter_queue()
    {
        var harness = new Harness();
        harness.Queue.Returns(Message("m1", "rh-1"));
        harness.Scopes.WithSender(Sender, BusinessId);
        harness.Scopes.Outcome =
            _ => NayaxSqsProcessingOutcome.Permanent(NayaxSqsProcessingReason.MessageRefused);

        await harness.Reader.PollOnceAsync(CancellationToken.None);

        Assert.Empty(harness.Queue.Deleted);
        Assert.Equal(TimeSpan.Zero, harness.LastVisibilityOf("rh-1"));
    }

    /// <summary>
    /// The sender gates, every one of them decided before a business scope is opened at all - so a
    /// message nobody may own never reaches a context that could read or write anything.
    /// </summary>
    [Theory]
    [InlineData(null, NayaxSqsProcessingReason.SenderMissing)]
    [InlineData("", NayaxSqsProcessingReason.SenderMissing)]
    [InlineData("   ", NayaxSqsProcessingReason.SenderMissing)]
    [InlineData("AIDAUNKNOWNSENDER0001", NayaxSqsProcessingReason.SenderUnknown)]
    public async Task A_message_with_no_usable_sender_is_refused_before_any_business_scope_exists(
        string? senderId, NayaxSqsProcessingReason expected)
    {
        var harness = new Harness();
        harness.Queue.Returns(Message("m1", "rh-1", senderId: senderId));
        harness.Scopes.WithSender(Sender, BusinessId);

        await harness.Reader.PollOnceAsync(CancellationToken.None);

        Assert.Equal(expected, harness.Log.Handled.Single().Reason);
        Assert.Equal(NayaxSqsDisposition.Permanent, harness.Log.Handled.Single().Disposition);
        Assert.Equal(0, harness.Scopes.ScopesOpened);
        Assert.Empty(harness.Queue.Deleted);
    }

    /// <summary>
    /// A recorded sender whose mapping is switched off, and one whose business is no longer trading,
    /// are both permanent and both refused before a scope exists. A business that has been
    /// deactivated must not have sales written into it by a queue.
    /// </summary>
    [Theory]
    [InlineData(false, BusinessStatus.Active, NayaxSqsProcessingReason.SenderDisabled)]
    [InlineData(true, BusinessStatus.Pending, NayaxSqsProcessingReason.BusinessNotActive)]
    [InlineData(true, BusinessStatus.Rejected, NayaxSqsProcessingReason.BusinessNotActive)]
    [InlineData(true, BusinessStatus.Deactivated, NayaxSqsProcessingReason.BusinessNotActive)]
    public async Task A_disabled_sender_or_an_inactive_business_is_permanently_refused(
        bool isEnabled, BusinessStatus status, NayaxSqsProcessingReason expected)
    {
        var harness = new Harness();
        harness.Queue.Returns(Message("m1", "rh-1"));
        harness.Scopes.WithSender(Sender, BusinessId, isEnabled, status);

        await harness.Reader.PollOnceAsync(CancellationToken.None);

        Assert.Equal(expected, harness.Log.Handled.Single().Reason);
        Assert.Equal(NayaxSqsDisposition.Permanent, harness.Log.Handled.Single().Disposition);
        Assert.Equal(0, harness.Scopes.ScopesOpened);
        Assert.Empty(harness.Queue.Deleted);
        Assert.Equal(TimeSpan.Zero, harness.LastVisibilityOf("rh-1"));
    }

    /// <summary>
    /// A sender lookup that failed says nothing about the message, so it is transient: the sale is
    /// parked for the next receive rather than pushed towards the dead-letter queue.
    /// </summary>
    [Fact]
    public async Task A_sender_lookup_that_fails_is_transient_rather_than_permanent()
    {
        var harness = new Harness();
        harness.Queue.Returns(Message("m1", "rh-1"));
        harness.Scopes.LookupFailure = new InvalidOperationException("database is locked");

        await harness.Reader.PollOnceAsync(CancellationToken.None);

        Assert.Equal(NayaxSqsDisposition.Transient, harness.Log.Handled.Single().Disposition);
        Assert.Equal(NayaxSqsProcessingReason.StoreUnavailable, harness.Log.Handled.Single().Reason);
        Assert.Empty(harness.Queue.Deleted);
    }

    /// <summary>
    /// Every message of a batch has its visibility reset to the full timeout immediately after the
    /// receive, <em>including the ones that will wait</em> behind the earlier messages. The store is
    /// a single SQLite file with one writer, so the tenth message of a batch can wait a long time,
    /// and a window that ran out while it waited would hand it to a second receiver.
    /// </summary>
    [Fact]
    public async Task Every_message_in_a_batch_is_extended_immediately_after_the_receive()
    {
        var harness = new Harness();
        harness.Queue.Returns(Message("m1", "rh-1"), Message("m2", "rh-2"), Message("m3", "rh-3"));
        harness.Scopes.WithSender(Sender, BusinessId);

        await harness.Reader.PollOnceAsync(CancellationToken.None);

        var fullTimeout = harness.Settings.VisibilityTimeout;
        Assert.Equal(
            ["rh-1", "rh-2", "rh-3"],
            harness.Queue.VisibilityChanges
                .Where(change => change.Visibility == fullTimeout)
                .Select(change => change.ReceiptHandle)
                .Take(3));
    }

    /// <summary>
    /// And again whenever half the timeout has elapsed, for every message that is still not finished.
    /// The clock is moved by the processing itself, which is how a slow batch really spends its
    /// window.
    /// </summary>
    [Fact]
    public async Task A_waiting_message_is_extended_again_once_half_the_timeout_has_elapsed()
    {
        var harness = new Harness();
        harness.Queue.Returns(Message("m1", "rh-1"), Message("m2", "rh-2"));
        harness.Scopes.WithSender(Sender, BusinessId);
        harness.Scopes.Outcome = messageId =>
        {
            // The first message takes longer than half the visibility timeout, so the second one's
            // window has to be reset before it is processed.
            if (string.Equals(messageId, "m1", StringComparison.Ordinal))
            {
                harness.Clock.UtcNow = harness.Clock.UtcNow.AddSeconds(61);
            }

            return NayaxSqsProcessingOutcome.Done(NayaxSqsProcessingReason.Saved);
        };

        await harness.Reader.PollOnceAsync(CancellationToken.None);

        var fullTimeout = harness.Settings.VisibilityTimeout;
        Assert.Equal(
            2,
            harness.Queue.VisibilityChanges
                .Count(change =>
                    string.Equals(change.ReceiptHandle, "rh-2", StringComparison.Ordinal)
                    && change.Visibility == fullTimeout));

        // The first message was already finished by then, so it is not extended a second time.
        Assert.Single(
            harness.Queue.VisibilityChanges
                .Where(change =>
                    string.Equals(change.ReceiptHandle, "rh-1", StringComparison.Ordinal)
                    && change.Visibility == fullTimeout));
    }

    /// <summary>
    /// A message whose visibility could not be extended is not processed in this batch at all: the
    /// failure means another receiver may now hold it, and two readers working on one transaction is
    /// the race the extension exists to prevent. The rest of the batch carries on.
    /// </summary>
    [Fact]
    public async Task A_message_whose_extension_failed_is_not_processed_in_this_batch()
    {
        var harness = new Harness();
        harness.Queue.Returns(Message("m1", "rh-1"), Message("m2", "rh-2"));
        harness.Queue.VisibilityFailures.Add("rh-2");
        harness.Scopes.WithSender(Sender, BusinessId);

        var result = await harness.Reader.PollOnceAsync(CancellationToken.None);

        Assert.Equal([(BusinessId, "m1")], harness.Scopes.Processed);
        Assert.Equal(["rh-1"], harness.Queue.Deleted);
        Assert.Equal(["m2"], harness.Log.ExtensionFailures);
        Assert.Equal(1, result.MessagesHandled);
        Assert.Equal(1, result.MessagesSkipped);
    }

    /// <summary>
    /// A message that was processed but could not be deleted is reported and nothing else: the sale is
    /// durably stored, so its redelivery finds it as a duplicate with its rebuilds clear and deletes
    /// it then. Refusing to continue would stop the rest of the batch for a message that is finished.
    /// </summary>
    [Fact]
    public async Task A_delete_that_fails_is_reported_and_the_batch_continues()
    {
        var harness = new Harness();
        harness.Queue.Returns(Message("m1", "rh-1"), Message("m2", "rh-2"));
        harness.Queue.DeleteFailures.Add("rh-1");
        harness.Scopes.WithSender(Sender, BusinessId);

        await harness.Reader.PollOnceAsync(CancellationToken.None);

        Assert.Equal(["m1"], harness.Log.DeleteFailures);
        Assert.Equal(["rh-2"], harness.Queue.Deleted);
    }

    /// <summary>
    /// One scope per message, and every one of them closed. A scope that outlived its message would
    /// hold a context - and a business - open for the next one.
    /// </summary>
    [Fact]
    public async Task Each_message_gets_its_own_business_scope_and_every_scope_is_closed()
    {
        var harness = new Harness();
        harness.Queue.Returns(Message("m1", "rh-1"), Message("m2", "rh-2"));
        harness.Scopes.WithSender(Sender, BusinessId);

        await harness.Reader.PollOnceAsync(CancellationToken.None);

        Assert.Equal(2, harness.Scopes.ScopesOpened);
        Assert.Equal(2, harness.Scopes.ScopesDisposed);
    }

    /// <summary>
    /// An empty receive is a successful poll, not silence: it is the queue answering and having
    /// nothing to hand over, which is the normal state of a quiet fleet. Recording it is what keeps a
    /// healthy idle reader from looking stalled on the health record (#1106).
    /// </summary>
    [Fact]
    public async Task An_empty_receive_is_recorded_as_a_successful_poll()
    {
        var harness = new Harness();
        harness.Queue.Returns();

        var result = await harness.Reader.PollOnceAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(Now, harness.Health.Current().LastSuccessfulPollUtc);
        Assert.Equal(0, harness.Health.Current().ConsecutivePollFailures);
        Assert.Null(harness.Health.Current().LastMessageProcessedUtc);
    }

    /// <summary>
    /// A receive that failed is counted, and the next successful one clears the count - so the record
    /// is a current statement about the reader rather than a lifetime tally.
    /// </summary>
    [Fact]
    public async Task A_failed_receive_is_counted_and_a_later_success_clears_the_count()
    {
        var harness = new Harness();
        harness.Queue
            .Fails(new InvalidOperationException("network unreachable"))
            .Fails(new InvalidOperationException("network unreachable"))
            .Returns();

        Assert.Equal(1, (await harness.Reader.PollOnceAsync(CancellationToken.None)).ConsecutivePollFailures);
        Assert.Equal(2, (await harness.Reader.PollOnceAsync(CancellationToken.None)).ConsecutivePollFailures);
        Assert.Equal(2, harness.Health.Current().ConsecutivePollFailures);

        await harness.Reader.PollOnceAsync(CancellationToken.None);

        Assert.Equal(0, harness.Health.Current().ConsecutivePollFailures);
    }

    /// <summary>
    /// The loop backs off with jitter after a failing receive, up to a minute, and keeps going: a
    /// reader that threw would take the host with it, and one that retried instantly would hammer a
    /// failing endpoint.
    /// </summary>
    [Fact]
    public async Task A_failing_receive_backs_off_with_jitter_and_never_crashes_the_loop()
    {
        var harness = new Harness();
        harness.Queue
            .Fails(new InvalidOperationException("AWS 503"))
            .Fails(new InvalidOperationException("AWS 503"))
            .Fails(new InvalidOperationException("AWS 503"));

        using var stopping = new CancellationTokenSource();
        harness.Pacing.OnDelay = _ =>
        {
            if (harness.Pacing.Delays.Count >= 3)
            {
                stopping.Cancel();
            }
        };

        await harness.Reader.RunAsync(stopping.Token);

        Assert.All(harness.Pacing.Delays, delay =>
            Assert.InRange(delay, TimeSpan.FromMilliseconds(500), NayaxSqsQueuePolicy.MaxPollBackoff));
        Assert.Equal(3, harness.Log.PollFailures.Count);
        Assert.Equal(1, harness.Log.Stopped);
    }

    /// <summary>
    /// With no queue configured the reader does not start: it logs one information line, polls
    /// nothing, and returns - which is what makes an API with no <c>NayaxSqs</c> settings start
    /// exactly as it did before.
    /// </summary>
    [Fact]
    public async Task With_no_queue_configured_the_reader_logs_one_line_and_polls_nothing()
    {
        var harness = new Harness();
        harness.Settings.IsConfigured = false;

        await harness.Reader.RunAsync(CancellationToken.None);

        Assert.Equal(1, harness.Log.NotConfiguredLines);
        Assert.Equal(0, harness.Log.Started);
        Assert.Equal(0, harness.Queue.Receives);
        Assert.Null(harness.Health.Current().LastSuccessfulPollUtc);
    }

    /// <summary>
    /// Shutdown stops polling and ends the loop without an error. A message not reached keeps its
    /// extended visibility and is redelivered, which is safe precisely because nothing is deleted
    /// before it is durably done.
    /// </summary>
    [Fact]
    public async Task Shutdown_stops_polling_and_leaves_unreached_messages_for_redelivery()
    {
        var harness = new Harness();
        harness.Queue.Returns(Message("m1", "rh-1"), Message("m2", "rh-2"));
        harness.Scopes.WithSender(Sender, BusinessId);

        using var stopping = new CancellationTokenSource();
        harness.Scopes.Outcome = messageId =>
        {
            if (string.Equals(messageId, "m1", StringComparison.Ordinal))
            {
                stopping.Cancel();
            }

            return NayaxSqsProcessingOutcome.Done(NayaxSqsProcessingReason.Saved);
        };

        await harness.Reader.RunAsync(stopping.Token);

        Assert.Equal([(BusinessId, "m1")], harness.Scopes.Processed);
        Assert.Equal(1, harness.Log.Stopped);
    }

    /// <summary>
    /// The dead-letter queue's depth is read on the first poll and then only every five minutes, it
    /// is warned about at any depth above zero, and it reaches the health record. The reader never
    /// receives from or deletes from that queue - there is no method on the port that could.
    /// </summary>
    [Fact]
    public async Task The_dead_letter_depth_is_read_every_five_minutes_and_warned_about_above_zero()
    {
        var harness = new Harness();
        harness.Queue.DeadLetterDepth = 2;

        await harness.Reader.PollOnceAsync(CancellationToken.None);
        await harness.Reader.PollOnceAsync(CancellationToken.None);

        Assert.Equal(1, harness.Queue.DeadLetterDepthReads);
        Assert.Equal([2], harness.Log.DeadLetterWarnings);
        Assert.Equal(2, harness.Health.Current().DeadLetterQueueDepth);

        harness.Clock.UtcNow = harness.Clock.UtcNow.Add(NayaxSqsQueuePolicy.DeadLetterDepthInterval);
        await harness.Reader.PollOnceAsync(CancellationToken.None);

        Assert.Equal(2, harness.Queue.DeadLetterDepthReads);
    }

    /// <summary>An empty dead-letter queue is read and reported, and warned about not at all.</summary>
    [Fact]
    public async Task An_empty_dead_letter_queue_is_recorded_without_a_warning()
    {
        var harness = new Harness();
        harness.Queue.DeadLetterDepth = 0;

        await harness.Reader.PollOnceAsync(CancellationToken.None);

        Assert.Empty(harness.Log.DeadLetterWarnings);
        Assert.Equal(0, harness.Health.Current().DeadLetterQueueDepth);
    }

    /// <summary>
    /// With no dead-letter queue configured none is read at all, and a depth read that fails is
    /// reported and changes nothing about polling the main queue.
    /// </summary>
    [Fact]
    public async Task A_missing_or_unreadable_dead_letter_queue_does_not_stop_the_main_poll()
    {
        var withoutDlq = new Harness();
        withoutDlq.Settings.HasDeadLetterQueue = false;

        await withoutDlq.Reader.PollOnceAsync(CancellationToken.None);

        Assert.Equal(0, withoutDlq.Queue.DeadLetterDepthReads);

        var failing = new Harness();
        failing.Queue.DeadLetterDepthFailure = new InvalidOperationException("AccessDenied");

        var result = await failing.Reader.PollOnceAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Single(failing.Log.DeadLetterReadFailures);
        Assert.Null(failing.Health.Current().DeadLetterQueueDepth);
    }

    /// <summary>
    /// Finishing with a message is recorded on the health singleton whatever the disposition was: the
    /// record says whether the reader is working, not whether every message was a sale.
    /// </summary>
    [Fact]
    public async Task Finishing_with_a_message_is_recorded_on_the_health_singleton()
    {
        var harness = new Harness();
        harness.Queue.Returns(Message("m1", "rh-1"));
        harness.Scopes.WithSender(Sender, BusinessId);
        harness.Scopes.Outcome =
            _ => NayaxSqsProcessingOutcome.Permanent(NayaxSqsProcessingReason.MessageRefused);

        await harness.Reader.PollOnceAsync(CancellationToken.None);

        Assert.Equal(Now, harness.Health.Current().LastMessageProcessedUtc);
    }

    private static NayaxSqsReceivedMessage Message(
        string messageId,
        string receiptHandle,
        string? senderId = Sender,
        int receiveCount = 1) =>
        new(messageId, receiptHandle, "{}", senderId, receiveCount);

    /// <summary>
    /// The reader with every port faked, and the pieces a case needs to drive or read exposed.
    /// </summary>
    private sealed class Harness
    {
        public Harness()
        {
            Reader = new NayaxSqsSalesReader(
                Queue, Scopes, Settings, Pacing, Health, Log, Clock);
        }

        public FakeNayaxSqsQueueClient Queue { get; } = new();

        public FakeNayaxSqsReaderScopes Scopes { get; } = new();

        public FakeNayaxSqsReaderSettings Settings { get; } = new();

        public FakeNayaxSqsReaderPacing Pacing { get; } = new();

        public NayaxSqsReaderHealth Health { get; } = new();

        public FakeNayaxSqsReaderLog Log { get; } = new();

        public FakeClock Clock { get; } = new(Now);

        public NayaxSqsSalesReader Reader { get; }

        /// <summary>The last visibility this handle was set to, which is the reader's final word on it.</summary>
        public TimeSpan LastVisibilityOf(string receiptHandle) =>
            Queue.VisibilityChanges
                .Where(change => string.Equals(change.ReceiptHandle, receiptHandle, StringComparison.Ordinal))
                .Select(change => change.Visibility)
                .Last();
    }
}
