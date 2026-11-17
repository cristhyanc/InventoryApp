namespace Inventory.Domain.NayaxSqs;

/// <summary>
/// The arithmetic the Nayax SQS sales reader moves messages by (issue #1103): how long a message
/// stays invisible, when its visibility has to be extended, and how long the reader waits after a
/// receive that failed.
///
/// <para><strong>It is here rather than in the reader because every one of these numbers is a
/// correctness rule, not a tuning knob.</strong> A visibility window that is not extended in time
/// hands a message another receiver is still working on to a second one, and a backoff that is not
/// bounded turns one AWS outage into a hot loop. Keeping them in one deterministic place is what
/// makes them testable at their boundaries without a queue, a clock or a host.</para>
///
/// <para>Nothing here reads a clock, makes a call or knows what a message means. The reader asks it
/// questions and obeys the answers.</para>
/// </summary>
public static class NayaxSqsQueuePolicy
{
    /// <summary>
    /// The visibility timeout a deployment that configures none gets: the 120 seconds parent #1100's
    /// AWS setup specifies for the main queue. The application and the queue must agree about it,
    /// which is why the default is this value rather than SQS's own 30-second one.
    /// </summary>
    public const int DefaultVisibilityTimeoutSeconds = 120;

    /// <summary>
    /// The narrowest visibility timeout the reader accepts. Below this a batch of ten messages
    /// cannot be worked through even with every extension landing, so a shorter configured value is
    /// clamped up rather than obeyed.
    /// </summary>
    public const int MinVisibilityTimeoutSeconds = 30;

    /// <summary>
    /// SQS's own documented maximum visibility timeout (12 hours). A larger configured value is
    /// clamped down, because the API would refuse it on every call.
    /// </summary>
    public const int MaxVisibilityTimeoutSeconds = 43200;

    /// <summary>The long-poll wait SQS holds a <c>ReceiveMessage</c> open for: its documented maximum.</summary>
    public static TimeSpan ReceiveWaitTime { get; } = TimeSpan.FromSeconds(20);

    /// <summary>At most ten messages per receive, which is SQS's own documented maximum.</summary>
    public const int MaxMessagesPerReceive = 10;

    /// <summary>
    /// The step a transient failure's backoff is counted in: 30 seconds for every time the message
    /// has been received, capped at the visibility timeout by
    /// <see cref="TransientVisibility"/>.
    /// </summary>
    public static TimeSpan TransientBackoffStep { get; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The wait after a failure caused by something outside this application being unavailable
    /// (issue #1305), one step per time the message has been received.
    ///
    /// <para><strong>It is a schedule rather than a multiple because the two failures it has to cover
    /// are hours apart.</strong> A blip of a few seconds - one dropped connection, one slow Nayax
    /// call - must clear on the first retry, so the first step is under a minute. An outage that
    /// nobody is awake for - the owner's example is a Nayax token that expired overnight - has to be
    /// ridden out until somebody fixes it, so the later steps are hours: with the dead-letter queue's
    /// documented <c>maxReceiveCount</c> of five, these four waits put the fifth and last attempt
    /// 12 h 30 m 45 s after the first receive, and the fifth wait keeps the message on the main queue
    /// for another 11 hours before AWS moves it.</para>
    ///
    /// <para>No step may reach <see cref="MaxVisibilityTimeoutSeconds"/>: SQS refuses a
    /// <c>ChangeMessageVisibility</c> past its 12-hour ceiling outright, and a refused call would
    /// leave the message on the queue's own 120-second timeout - the one outcome worse than waiting
    /// too long. The last step is 11 hours, which leaves a message that was in flight for a while
    /// before it failed an hour of room under that ceiling.</para>
    /// </summary>
    public static IReadOnlyList<TimeSpan> TransientOutageSchedule { get; } =
    [
        TimeSpan.FromSeconds(45),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(3),
        TimeSpan.FromHours(9),
        TimeSpan.FromHours(11),
    ];

    /// <summary>How often the dead-letter queue's depth is read.</summary>
    public static TimeSpan DeadLetterDepthInterval { get; } = TimeSpan.FromMinutes(5);

    /// <summary>The longest the reader waits between two failing receives.</summary>
    public static TimeSpan MaxPollBackoff { get; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The visibility timeout to use, with a configured value clamped into the range SQS and a
    /// ten-message batch can both live with.
    /// </summary>
    public static TimeSpan VisibilityTimeout(int configuredSeconds) =>
        TimeSpan.FromSeconds(Math.Clamp(
            configuredSeconds <= 0 ? DefaultVisibilityTimeoutSeconds : configuredSeconds,
            MinVisibilityTimeoutSeconds,
            MaxVisibilityTimeoutSeconds));

    /// <summary>
    /// How long after the last extension the reader has to extend again: half the visibility
    /// timeout, so a message always has at least half its window left when the extension lands and
    /// an extension that is itself slow still has room to be retried.
    /// </summary>
    public static TimeSpan ExtensionInterval(TimeSpan visibilityTimeout) =>
        TimeSpan.FromTicks(visibilityTimeout.Ticks / 2);

    /// <summary>
    /// The visibility a message whose processing failed transiently is set to: 30 seconds for every
    /// receive so far, never longer than the visibility timeout itself.
    ///
    /// <para>Counting by the receive count rather than by a stored attempt is what makes the backoff
    /// survive a restart: SQS is the thing that remembers, and
    /// <c>ApproximateReceiveCount</c> is already on the message. A receive count the queue did not
    /// report is treated as the first receive, which is the shortest wait rather than the longest -
    /// a message nothing knows about is retried promptly instead of being parked.</para>
    /// </summary>
    public static TimeSpan TransientVisibility(int approximateReceiveCount, TimeSpan visibilityTimeout)
    {
        var receives = Math.Max(1, approximateReceiveCount);
        var backoff = TransientBackoffStep * receives;

        return backoff < visibilityTimeout ? backoff : visibilityTimeout;
    }

    /// <summary>
    /// The visibility a message whose processing failed because something it needs is unavailable is
    /// set to (issue #1305): the step <see cref="TransientOutageSchedule"/> holds for this receive
    /// count, with a receive count past the schedule staying on its last step and one the queue did
    /// not report treated as the first receive - the shortest wait, so a message nothing knows about
    /// is retried promptly rather than parked for half a day.
    ///
    /// <para>Unlike <see cref="TransientVisibility"/> this is not bounded by the queue's visibility
    /// timeout: a wait of two minutes is exactly what made an overnight outage end in the dead-letter
    /// queue. It is bounded by <see cref="MaxVisibilityTimeoutSeconds"/>, which SQS itself enforces,
    /// and the clamp is applied here as well so a future edit to the schedule cannot produce a call
    /// AWS would refuse.</para>
    /// </summary>
    public static TimeSpan TransientOutageVisibility(int approximateReceiveCount)
    {
        var receives = Math.Max(1, approximateReceiveCount);
        var step = TransientOutageSchedule[Math.Min(receives, TransientOutageSchedule.Count) - 1];
        var ceiling = TimeSpan.FromSeconds(MaxVisibilityTimeoutSeconds);

        return step < ceiling ? step : ceiling;
    }

    /// <summary>
    /// The visibility a message that can never succeed is set to: zero, so it is immediately
    /// available again and reaches the dead-letter queue as soon as the queue's own
    /// <c>maxReceiveCount</c> is spent. Nothing deletes such a message, because a message nobody
    /// kept is a sample nobody can look at.
    /// </summary>
    public static TimeSpan PermanentVisibility { get; } = TimeSpan.Zero;

    /// <summary>
    /// How long to wait after <paramref name="consecutiveFailures"/> receives in a row have failed:
    /// a doubling backoff capped at <see cref="MaxPollBackoff"/>, with
    /// <paramref name="jitterSample"/> (a value in <c>[0,1)</c>) spreading the actual wait over the
    /// upper half of that ceiling.
    ///
    /// <para>The jitter is the caller's sample rather than a random draw made here, so the rule stays
    /// deterministic and its boundaries are testable. Half the ceiling is the floor on purpose: a
    /// reader that jittered down to nothing would hammer a failing endpoint exactly when it is
    /// least able to answer.</para>
    /// </summary>
    public static TimeSpan PollBackoff(int consecutiveFailures, double jitterSample)
    {
        if (consecutiveFailures <= 0)
        {
            return TimeSpan.Zero;
        }

        // Doubling from one second, computed on the exponent so a long outage cannot overflow the
        // multiplication before the cap is applied.
        var exponent = Math.Min(consecutiveFailures - 1, 10);
        var ceiling = TimeSpan.FromSeconds(Math.Pow(2, exponent));
        if (ceiling > MaxPollBackoff)
        {
            ceiling = MaxPollBackoff;
        }

        var sample = double.IsFinite(jitterSample) ? Math.Clamp(jitterSample, 0, 1) : 0;

        return TimeSpan.FromTicks((long)(ceiling.Ticks * (0.5 + (0.5 * sample))));
    }
}
