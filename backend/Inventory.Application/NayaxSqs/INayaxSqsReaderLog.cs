namespace Inventory.Application.NayaxSqs;

/// <summary>
/// Everything the Nayax SQS sales reader says about itself (issue #1103), as a narrow Application
/// port implemented over <c>ILogger</c> in <c>Inventory.Infrastructure.NayaxSqs</c> - the same
/// arrangement as the platform audit sinks.
///
/// <para><strong>The port is the privacy rule written as a type.</strong> A queue message is a
/// consumer's card transaction, and a receipt handle is a capability to change or delete a queue
/// message. Not one method here has a parameter that could carry a message body, a receipt handle, a
/// card or customer value, an AWS key, a queue URL or a sender id: what a reader log line can hold is
/// a queue message id, a business id, a count, a duration, a fixed reason code and an exception
/// <em>type name</em>. That is structural rather than a matter of a sink's discretion, which is what
/// AGENTS.md § Nayax integration requires.</para>
///
/// <para><strong>Exception type names rather than exceptions.</strong> An exception's own message can
/// quote what it failed on - a JSON parse error names the offset inside the body, an AWS error can
/// name what it was given - so the reader passes the type's name and never the exception. The reason
/// code beside it is what makes a line diagnosable.</para>
/// </summary>
public interface INayaxSqsReaderLog
{
    /// <summary>
    /// The one information line a deployment with no queue configured gets. The API starts normally
    /// and the reader does not start at all; this is what says so, rather than silence that cannot be
    /// told apart from a reader that crashed.
    /// </summary>
    void ReaderNotConfigured();

    /// <summary>The reader has started polling.</summary>
    void ReaderStarted();

    /// <summary>The reader has stopped, because the host is shutting down.</summary>
    void ReaderStopped();

    /// <summary>
    /// A <c>ReceiveMessage</c> failed, and the reader is waiting <paramref name="backoff"/> before
    /// trying again.
    /// </summary>
    void PollFailed(string failureType, int consecutivePollFailures, TimeSpan backoff);

    /// <summary>One message was handled, with the fixed codes that say how and why.</summary>
    void MessageHandled(
        string messageId,
        int? businessId,
        NayaxSqsDisposition disposition,
        NayaxSqsProcessingReason reason);

    /// <summary>
    /// A message's visibility could not be extended, so it is not processed in this batch: another
    /// receiver may now hold it.
    /// </summary>
    void VisibilityExtensionFailed(string messageId, string failureType);

    /// <summary>
    /// A message's visibility could not be set to its backoff or to zero. Nothing is lost - the
    /// message's own timeout expires on its own - so this is reported and the batch continues.
    /// </summary>
    void VisibilityChangeFailed(string messageId, string failureType);

    /// <summary>
    /// A finished message could not be deleted. The sale is durably stored, so the redelivery finds
    /// it as a duplicate with its rebuilds clear and deletes it then.
    /// </summary>
    void DeleteFailed(string messageId, string failureType);

    /// <summary>The dead-letter queue holds messages. Reported as a warning at every depth above zero.</summary>
    void DeadLetterQueueNotEmpty(int depth);

    /// <summary>The dead-letter queue's depth could not be read. The reader keeps polling the main queue.</summary>
    void DeadLetterDepthReadFailed(string failureType);
}
