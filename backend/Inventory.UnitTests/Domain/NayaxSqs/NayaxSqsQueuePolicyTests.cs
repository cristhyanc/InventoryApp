using Inventory.Domain.NayaxSqs;
using Xunit;

namespace InventoryApi.Tests.Domain.NayaxSqs;

/// <summary>
/// Issue #1103: the arithmetic the Nayax SQS sales reader moves messages by. Every one of these
/// numbers is a correctness rule rather than a tuning knob - a window that is not extended in time
/// hands a message another receiver is still working on to a second one, and an unbounded backoff
/// turns one AWS outage into a hot loop - so they are asserted at their boundaries here, with no
/// queue, no clock and no host.
/// </summary>
public class NayaxSqsQueuePolicyTests
{
    /// <summary>
    /// The default is parent #1100's own AWS setting for the main queue. The application has a value
    /// for it because the two have to agree: every extension, every backoff and the extension
    /// interval are measured against this one number.
    /// </summary>
    [Fact]
    public void An_unset_visibility_timeout_is_the_queues_own_two_minutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), NayaxSqsQueuePolicy.VisibilityTimeout(0));
        Assert.Equal(TimeSpan.FromSeconds(120), NayaxSqsQueuePolicy.VisibilityTimeout(-5));
    }

    /// <summary>
    /// Below the floor a batch of ten messages cannot be worked through even with every extension
    /// landing, and above the ceiling SQS refuses the call - so a configured value outside the range
    /// is clamped rather than obeyed.
    /// </summary>
    [Theory]
    [InlineData(1, NayaxSqsQueuePolicy.MinVisibilityTimeoutSeconds)]
    [InlineData(29, NayaxSqsQueuePolicy.MinVisibilityTimeoutSeconds)]
    [InlineData(30, 30)]
    [InlineData(120, 120)]
    [InlineData(43200, NayaxSqsQueuePolicy.MaxVisibilityTimeoutSeconds)]
    [InlineData(100000, NayaxSqsQueuePolicy.MaxVisibilityTimeoutSeconds)]
    public void A_configured_visibility_timeout_is_clamped_into_the_usable_range(
        int configured, int expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds), NayaxSqsQueuePolicy.VisibilityTimeout(configured));
    }

    /// <summary>
    /// Half the timeout, so a message always has at least half its window left when the extension
    /// lands and an extension that is itself slow still has room to be retried.
    /// </summary>
    [Fact]
    public void The_extension_is_due_after_half_the_visibility_timeout()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            NayaxSqsQueuePolicy.ExtensionInterval(TimeSpan.FromSeconds(120)));
    }

    /// <summary>
    /// Thirty seconds per receive so far, capped at the visibility timeout. Counting by the queue's
    /// own receive count rather than by a stored attempt is what makes the backoff survive a restart.
    /// </summary>
    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 90)]
    [InlineData(4, 120)]
    [InlineData(5, 120)]
    [InlineData(50, 120)]
    public void A_transient_failure_backs_off_thirty_seconds_per_receive_capped_at_the_timeout(
        int receiveCount, int expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            NayaxSqsQueuePolicy.TransientVisibility(receiveCount, TimeSpan.FromSeconds(120)));
    }

    /// <summary>
    /// A receive count the queue did not report is treated as the first receive, which is the
    /// <em>shortest</em> wait: a message nothing knows about is retried promptly rather than parked
    /// for the whole window.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void An_unreported_receive_count_backs_off_as_the_first_receive(int receiveCount)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            NayaxSqsQueuePolicy.TransientVisibility(receiveCount, TimeSpan.FromSeconds(120)));
    }

    /// <summary>
    /// A message that can never succeed is released immediately, so it reaches the dead-letter queue
    /// as soon as the queue's own maxReceiveCount is spent. Zero, never a delete: a message nobody
    /// kept is a sample nobody can look at.
    /// </summary>
    [Fact]
    public void A_permanent_failure_releases_the_message_immediately()
    {
        Assert.Equal(TimeSpan.Zero, NayaxSqsQueuePolicy.PermanentVisibility);
    }

    /// <summary>
    /// No failures means no wait: a successful poll goes straight back to long-polling the queue.
    /// </summary>
    [Fact]
    public void A_successful_poll_waits_for_nothing()
    {
        Assert.Equal(TimeSpan.Zero, NayaxSqsQueuePolicy.PollBackoff(0, 0.5));
    }

    /// <summary>
    /// The backoff doubles and is capped at a minute, and the jitter spreads the wait over the upper
    /// half of that ceiling - never down to nothing, because a reader that jittered to zero would
    /// hammer a failing endpoint exactly when it is least able to answer.
    /// </summary>
    [Theory]
    [InlineData(1, 0.5, 1.0)]
    [InlineData(1, 1.0, 1.0)]
    [InlineData(2, 0.0, 2.0)]
    [InlineData(2, 1.0, 2.0)]
    [InlineData(7, 0.0, 60.0)]
    [InlineData(7, 1.0, 60.0)]
    [InlineData(1000, 0.0, 60.0)]
    [InlineData(1000, 1.0, 60.0)]
    public void The_poll_backoff_doubles_within_half_the_ceiling_and_never_passes_a_minute(
        int consecutiveFailures, double jitterSample, double ceilingSeconds)
    {
        var backoff = NayaxSqsQueuePolicy.PollBackoff(consecutiveFailures, jitterSample);

        Assert.InRange(
            backoff,
            TimeSpan.FromSeconds(ceilingSeconds / 2),
            TimeSpan.FromSeconds(ceilingSeconds));
        Assert.True(backoff <= NayaxSqsQueuePolicy.MaxPollBackoff);
    }

    /// <summary>
    /// A jitter sample outside <c>[0,1)</c> - or one that is not a number at all - is clamped rather
    /// than trusted, so a faulty pacing adapter cannot produce a negative or infinite wait.
    /// </summary>
    [Theory]
    [InlineData(-1.0)]
    [InlineData(2.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void An_impossible_jitter_sample_still_produces_a_bounded_wait(double jitterSample)
    {
        var backoff = NayaxSqsQueuePolicy.PollBackoff(4, jitterSample);

        Assert.InRange(backoff, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8));
    }

    /// <summary>
    /// The receive is SQS's own documented maximum long poll and batch size, and the dead-letter
    /// depth is read every five minutes - the interval the issue states.
    /// </summary>
    [Fact]
    public void The_receive_and_dead_letter_intervals_are_the_documented_ones()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), NayaxSqsQueuePolicy.ReceiveWaitTime);
        Assert.Equal(10, NayaxSqsQueuePolicy.MaxMessagesPerReceive);
        Assert.Equal(TimeSpan.FromMinutes(5), NayaxSqsQueuePolicy.DeadLetterDepthInterval);
    }
}
