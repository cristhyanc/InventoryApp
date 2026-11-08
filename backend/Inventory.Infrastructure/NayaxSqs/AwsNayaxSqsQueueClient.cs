using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Inventory.Application.NayaxSqs;
using Inventory.Domain.NayaxSqs;

namespace Inventory.Infrastructure.NayaxSqs;

/// <summary>
/// The AWS SDK implementation of <see cref="INayaxSqsQueueClient"/> (issue #1103): the only type in
/// this application that holds <c>AWSSDK.SQS</c>, a queue URL or an AWS credential.
///
/// <para><strong>It performs the four operations the reader's IAM user is granted and nothing
/// else.</strong> <c>ReceiveMessage</c>, <c>ChangeMessageVisibility</c> and <c>DeleteMessage</c> on
/// the main queue, and <c>GetQueueAttributes</c> on the dead-letter queue. There is no send, no
/// purge, no receive from the dead-letter queue and no way for a caller to name a queue: the two URLs
/// come from configuration and are captured here. The IAM policy is the outer boundary and this class
/// is the inner one, so neither alone is what keeps the application off the other queues.</para>
///
/// <para><strong>Nothing it returns or throws carries a credential.</strong> The access key pair is
/// handed to the SDK's own credential type and is never read back, logged or formatted into a
/// message; the reader logs exception <em>type names</em> rather than exceptions for the same
/// reason.</para>
///
/// <para><strong>The receive asks for exactly two system attributes.</strong> <c>SenderId</c> - the
/// AWS IAM user id a message is attributed by, which is the whole of how a shared queue says whose
/// sale it is (issue #1101) - and <c>ApproximateReceiveCount</c>, which is the queue's own memory of
/// how often a message has been tried and therefore what makes the transient backoff survive a
/// restart. No message attribute is requested, because nothing reads one.</para>
///
/// <para>It is registered as a singleton beside one <see cref="AmazonSQSClient"/>, which is the SDK's
/// own guidance: the client is thread-safe, holds the HTTP connection pool, and creating one per poll
/// would renegotiate TLS every twenty seconds.</para>
/// </summary>
public sealed class AwsNayaxSqsQueueClient : INayaxSqsQueueClient, IDisposable
{
    private readonly IAmazonSQS _sqs;
    private readonly bool _ownsClient;
    private readonly string _queueUrl;
    private readonly string? _deadLetterQueueUrl;
    private readonly int _visibilityTimeoutSeconds;

    /// <summary>
    /// Builds the client from a validated configuration, creating the one
    /// <see cref="AmazonSQSClient"/> it owns.
    /// </summary>
    /// <exception cref="InvalidOperationException">The configuration has no queue at all.</exception>
    public AwsNayaxSqsQueueClient(NayaxSqsConfiguration configuration)
        : this(CreateClient(configuration), ownsClient: true, configuration)
    {
    }

    /// <summary>
    /// Builds the client over an <see cref="IAmazonSQS"/> somebody else owns, which is what lets the
    /// queue operations be exercised against a double without the composition root growing a second
    /// shape.
    /// </summary>
    public AwsNayaxSqsQueueClient(IAmazonSQS sqs, NayaxSqsConfiguration configuration)
        : this(sqs, ownsClient: false, configuration)
    {
    }

    private AwsNayaxSqsQueueClient(
        IAmazonSQS sqs, bool ownsClient, NayaxSqsConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(sqs);
        ArgumentNullException.ThrowIfNull(configuration);

        _sqs = sqs;
        _ownsClient = ownsClient;
        _queueUrl = configuration.QueueUrl
            ?? throw new InvalidOperationException(
                "The Nayax SQS queue client was built with no queue configured. The reader must not "
                    + "be registered at all in that state.");
        _deadLetterQueueUrl = configuration.DeadLetterQueueUrl;
        _visibilityTimeoutSeconds = (int)configuration.VisibilityTimeout.TotalSeconds;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NayaxSqsReceivedMessage>> ReceiveAsync(
        CancellationToken cancellationToken)
    {
        var response = await _sqs.ReceiveMessageAsync(
            new ReceiveMessageRequest
            {
                QueueUrl = _queueUrl,
                MaxNumberOfMessages = NayaxSqsQueuePolicy.MaxMessagesPerReceive,
                WaitTimeSeconds = (int)NayaxSqsQueuePolicy.ReceiveWaitTime.TotalSeconds,
                VisibilityTimeout = _visibilityTimeoutSeconds,
                MessageSystemAttributeNames =
                [
                    MessageSystemAttributeName.SenderId.Value,
                    MessageSystemAttributeName.ApproximateReceiveCount.Value,
                ],
            },
            cancellationToken).ConfigureAwait(false);

        var messages = response.Messages;
        if (messages is null or { Count: 0 })
        {
            return [];
        }

        var received = new List<NayaxSqsReceivedMessage>(messages.Count);
        foreach (var message in messages)
        {
            received.Add(new NayaxSqsReceivedMessage(
                message.MessageId,
                message.ReceiptHandle,
                message.Body,
                SystemAttribute(message, MessageSystemAttributeName.SenderId.Value),
                ReceiveCount(message)));
        }

        return received;
    }

    /// <inheritdoc />
    public Task ChangeVisibilityAsync(
        string receiptHandle, TimeSpan visibility, CancellationToken cancellationToken) =>
        _sqs.ChangeMessageVisibilityAsync(
            new ChangeMessageVisibilityRequest
            {
                QueueUrl = _queueUrl,
                ReceiptHandle = receiptHandle,

                // Clamped rather than passed through: SQS refuses a negative value and anything past
                // its own 12-hour maximum, and a refused call here would be reported as a failure to
                // extend - which makes the reader skip a message it still holds.
                VisibilityTimeout = Math.Clamp(
                    (int)visibility.TotalSeconds, 0, NayaxSqsQueuePolicy.MaxVisibilityTimeoutSeconds),
            },
            cancellationToken);

    /// <inheritdoc />
    public Task DeleteAsync(string receiptHandle, CancellationToken cancellationToken) =>
        _sqs.DeleteMessageAsync(
            new DeleteMessageRequest
            {
                QueueUrl = _queueUrl,
                ReceiptHandle = receiptHandle,
            },
            cancellationToken);

    /// <inheritdoc />
    public async Task<int?> GetDeadLetterDepthAsync(CancellationToken cancellationToken)
    {
        if (_deadLetterQueueUrl is null)
        {
            return null;
        }

        var response = await _sqs.GetQueueAttributesAsync(
            new GetQueueAttributesRequest
            {
                QueueUrl = _deadLetterQueueUrl,
                AttributeNames = [QueueAttributeName.ApproximateNumberOfMessages.Value],
            },
            cancellationToken).ConfigureAwait(false);

        return response.ApproximateNumberOfMessages;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _sqs.Dispose();
        }
    }

    private static IAmazonSQS CreateClient(NayaxSqsConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.Region is null
            || configuration.AccessKeyId is null
            || configuration.SecretAccessKey is null)
        {
            throw new InvalidOperationException(
                "The Nayax SQS queue client needs a region and the reader credential pair. "
                    + "NayaxSqsConfiguration.From refuses that state, so reaching here means the "
                    + "reader was registered without being configured.");
        }

        return new AmazonSQSClient(
            new BasicAWSCredentials(configuration.AccessKeyId, configuration.SecretAccessKey),
            RegionEndpoint.GetBySystemName(configuration.Region));
    }

    private static string? SystemAttribute(Message message, string name) =>
        message.Attributes is not null && message.Attributes.TryGetValue(name, out var value)
            ? value
            : null;

    /// <summary>
    /// The message's <c>ApproximateReceiveCount</c>, or <c>1</c> when the queue reported none or an
    /// unreadable one. One is the fail-safe reading: it is the <em>shortest</em> backoff, so a message
    /// nothing knows about is retried promptly rather than parked for the full visibility timeout.
    /// </summary>
    private static int ReceiveCount(Message message) =>
        int.TryParse(
            SystemAttribute(message, MessageSystemAttributeName.ApproximateReceiveCount.Value),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var count) && count > 0
            ? count
            : 1;
}
