namespace Inventory.Application.NayaxSqs;

/// <summary>
/// What the Nayax SQS sales reader has been doing, as one immutable reading (issue #1103, for the
/// Dashboard of issue #1106).
///
/// <para>Every field is about the <em>reader</em> and about no business: how recently the queue
/// answered at all, how many receives in a row have failed, when a message was last processed, and
/// how deep the dead-letter queue was when it was last read. A business id, a transaction, a machine
/// or a count of sales is deliberately not here - a reading somebody may be shown must say whether
/// sales are arriving, not whose.</para>
/// </summary>
/// <param name="LastSuccessfulPollUtc">
/// The last <c>ReceiveMessage</c> that returned without error, <em>including an empty one</em>. An
/// empty poll is the queue working and having nothing to hand over, which is the normal state of a
/// quiet machine fleet; treating it as "no news" would make a healthy reader look stalled.
/// </param>
/// <param name="ConsecutivePollFailures">
/// How many receives in a row have failed. Reset to zero by the next successful poll, which is what
/// makes it a current statement rather than a lifetime tally.
/// </param>
/// <param name="LastMessageProcessedUtc">
/// When the reader last finished with a message - saved, acknowledged or deliberately refused - or
/// <see langword="null"/> when it has not finished one since the process started.
/// </param>
/// <param name="DeadLetterQueueDepth">
/// The dead-letter queue's <c>ApproximateNumberOfMessages</c> as of
/// <paramref name="DeadLetterDepthReadAtUtc"/>, or <see langword="null"/> when no dead-letter queue
/// is configured or none has been read yet.
/// </param>
/// <param name="DeadLetterDepthReadAtUtc">When that depth was read.</param>
public sealed record NayaxSqsReaderHealthReading(
    DateTime? LastSuccessfulPollUtc,
    int ConsecutivePollFailures,
    DateTime? LastMessageProcessedUtc,
    int? DeadLetterQueueDepth,
    DateTime? DeadLetterDepthReadAtUtc);

/// <summary>
/// The process-wide record of how the Nayax SQS sales reader is getting on (issue #1103).
///
/// <para><strong>In memory, and that is correct here.</strong> The API runs as a single instance on a
/// single SQLite file (docs/architecture.md § SQLite operating assumptions), so there is exactly one
/// reader and one place its state could live. After a restart the record is empty until the first
/// poll, which is a true statement - the reader has not polled yet - rather than a stale one
/// recovered from somewhere.</para>
///
/// <para>It is a singleton and is written from the reader's own loop and read by whatever reports it.
/// A lock rather than interlocked fields, because a reading has to be internally consistent: a
/// consumer that saw a fresh successful-poll instant beside a stale failure count would be shown a
/// state the reader was never in.</para>
/// </summary>
public sealed class NayaxSqsReaderHealth
{
    private readonly object _gate = new();

    private DateTime? _lastSuccessfulPollUtc;
    private int _consecutivePollFailures;
    private DateTime? _lastMessageProcessedUtc;
    private int? _deadLetterQueueDepth;
    private DateTime? _deadLetterDepthReadAtUtc;

    /// <summary>
    /// Records a <c>ReceiveMessage</c> that returned without error, empty or not, and clears the
    /// consecutive-failure count.
    /// </summary>
    public void RecordSuccessfulPoll(DateTime atUtc)
    {
        lock (_gate)
        {
            _lastSuccessfulPollUtc = atUtc;
            _consecutivePollFailures = 0;
        }
    }

    /// <summary>Records a <c>ReceiveMessage</c> that failed, and answers how many have failed in a row.</summary>
    public int RecordPollFailure()
    {
        lock (_gate)
        {
            return ++_consecutivePollFailures;
        }
    }

    /// <summary>Records that the reader finished with one message.</summary>
    public void RecordMessageProcessed(DateTime atUtc)
    {
        lock (_gate)
        {
            _lastMessageProcessedUtc = atUtc;
        }
    }

    /// <summary>Records the dead-letter queue depth that was just read, and when.</summary>
    public void RecordDeadLetterDepth(int depth, DateTime atUtc)
    {
        lock (_gate)
        {
            _deadLetterQueueDepth = depth;
            _deadLetterDepthReadAtUtc = atUtc;
        }
    }

    /// <summary>One internally consistent reading of everything above.</summary>
    public NayaxSqsReaderHealthReading Current()
    {
        lock (_gate)
        {
            return new NayaxSqsReaderHealthReading(
                _lastSuccessfulPollUtc,
                _consecutivePollFailures,
                _lastMessageProcessedUtc,
                _deadLetterQueueDepth,
                _deadLetterDepthReadAtUtc);
        }
    }
}
