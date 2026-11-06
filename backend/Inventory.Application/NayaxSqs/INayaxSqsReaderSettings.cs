using Inventory.Domain.NayaxSqs;

namespace Inventory.Application.NayaxSqs;

/// <summary>
/// What the Nayax SQS sales reader needs to know about its queue, published as a narrow port so the
/// reader never reads configuration itself (issue #1103).
///
/// <para><strong>No credential and no URL crosses this boundary.</strong> The queue URLs, the AWS
/// region and the access key pair are the composition root's and the queue client's business; what
/// the reader needs is whether there is a queue to read at all, whether there is a dead-letter queue
/// to report the depth of, and the visibility timeout its own arithmetic is decided on
/// (<see cref="NayaxSqsQueuePolicy"/>). Keeping the secret out of the port is what makes it
/// structurally impossible for a reader log line, an exception message or a health record to carry
/// one.</para>
/// </summary>
public interface INayaxSqsReaderSettings
{
    /// <summary>
    /// Whether a main queue URL is configured. With none, the reader does not start at all, the API
    /// starts normally and one information line says so - which is the shipped state and the state
    /// of every environment that has not been given a queue.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Whether a dead-letter queue URL is configured, and therefore whether its depth is read every
    /// <see cref="NayaxSqsQueuePolicy.DeadLetterDepthInterval"/>. The reader never receives from or
    /// deletes from that queue whatever this says.
    /// </summary>
    bool HasDeadLetterQueue { get; }

    /// <summary>
    /// The queue's visibility timeout, already clamped by
    /// <see cref="NayaxSqsQueuePolicy.VisibilityTimeout"/>. Every extension, every transient backoff
    /// and the extension interval are measured against this one value, so the application and the
    /// queue cannot disagree about it.
    /// </summary>
    TimeSpan VisibilityTimeout { get; }
}
