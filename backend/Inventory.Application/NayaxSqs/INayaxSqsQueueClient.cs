namespace Inventory.Application.NayaxSqs;

/// <summary>
/// One message as the shared Nayax SQS queue delivered it (issue #1103).
///
/// <para><strong>Only what the reader acts on is carried.</strong> The body is the transaction
/// document <c>NayaxSqsTransactionParser</c> reads; <see cref="SenderId"/> is the SQS
/// <c>SenderId</c> system attribute, the AWS IAM user id a message is attributed by; and
/// <see cref="ApproximateReceiveCount"/> is the system attribute the transient backoff is counted
/// from. No message attribute, queue attribute, MD5 or timestamp is modelled, because nothing reads
/// one.</para>
///
/// <para><strong>The body and the receipt handle never reach a log.</strong> The body is a
/// consumer's card transaction and the receipt handle is a capability to change or delete a queue
/// message; the reader logs <see cref="MessageId"/>, the transaction id, the machine id, the
/// business id, counts and reason codes, and nothing else (AGENTS.md § Nayax integration).</para>
/// </summary>
/// <param name="MessageId">
/// The queue's own id for this delivery. It is the one identifier of a message that is safe to log,
/// and it is what the reader records as a stored sale's <c>SqsFirstMessageId</c>.
/// </param>
/// <param name="ReceiptHandle">
/// The handle this receive issued, which is what a visibility change or a delete must present. A
/// later receive of the same message issues a different one, so the reader always uses the handle
/// from the <em>latest</em> receive.
/// </param>
/// <param name="Body">The raw message body, or <see langword="null"/> when the delivery carried none.</param>
/// <param name="SenderId">
/// The SQS <c>SenderId</c> system attribute: the AWS IAM user id that sent the message, and the only
/// thing that says whose sale it is. A message the queue reported no sender for is attributable to
/// nobody and is never saved.
/// </param>
/// <param name="ApproximateReceiveCount">
/// The SQS <c>ApproximateReceiveCount</c> system attribute: how many times this message has been
/// received. It is the queue's own memory of how often a message has been tried, which is what makes
/// the transient backoff survive a restart.
/// </param>
public sealed record NayaxSqsReceivedMessage(
    string MessageId,
    string ReceiptHandle,
    string? Body,
    string? SenderId,
    int ApproximateReceiveCount);

/// <summary>
/// The four SQS operations the Nayax sales reader is allowed to perform, as an Application-owned
/// port (issue #1103). The AWS SDK lives behind it, in
/// <c>Inventory.Infrastructure.NayaxSqs</c>, so no use case depends on <c>AWSSDK.SQS</c>.
///
/// <para><strong>The port is the permission boundary written in code.</strong> The reader's IAM user
/// is granted <c>ReceiveMessage</c>, <c>DeleteMessage</c>, <c>ChangeMessageVisibility</c> and
/// <c>GetQueueAttributes</c> on the main queue and only <c>GetQueueAttributes</c> on the dead-letter
/// queue - and this interface offers exactly those operations and no way to name a queue, so the
/// application cannot receive from, delete from or send to anything else. In particular there is no
/// method that reads a message from the dead-letter queue: its depth is all this application ever
/// learns about it.</para>
/// </summary>
public interface INayaxSqsQueueClient
{
    /// <summary>
    /// Long-polls the main queue for at most
    /// <see cref="Domain.NayaxSqs.NayaxSqsQueuePolicy.MaxMessagesPerReceive"/> messages, requesting
    /// the <c>SenderId</c> and <c>ApproximateReceiveCount</c> system attributes. An empty result is a
    /// successful poll, not a failure: it is the queue saying there is nothing to do.
    /// </summary>
    Task<IReadOnlyList<NayaxSqsReceivedMessage>> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Resets one message's visibility timeout to <paramref name="visibility"/>, presenting the
    /// handle from its latest receive. A zero visibility makes the message immediately available
    /// again, which is how a message this application can never save reaches the dead-letter queue.
    /// </summary>
    Task ChangeVisibilityAsync(
        string receiptHandle, TimeSpan visibility, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes one message, presenting the handle from its latest receive. Called only once the sale
    /// and every inventory-cost rebuild it needs are durably done, or once the message is known to
    /// need no sale at all.
    /// </summary>
    Task DeleteAsync(string receiptHandle, CancellationToken cancellationToken);

    /// <summary>
    /// The dead-letter queue's <c>ApproximateNumberOfMessages</c>, or <see langword="null"/> when no
    /// dead-letter queue is configured. It is a <em>depth</em> and never a message: nothing in this
    /// application may receive from or delete from that queue, and replaying it is the owner's own
    /// console procedure (README.md § Nayax SQS sales reader).
    /// </summary>
    Task<int?> GetDeadLetterDepthAsync(CancellationToken cancellationToken);
}
