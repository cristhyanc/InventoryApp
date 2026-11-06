using Inventory.Application.NayaxSqs;
using Microsoft.Extensions.Logging;

namespace Inventory.Infrastructure.NayaxSqs;

/// <summary>
/// <see cref="INayaxSqsReaderLog"/> over <c>ILogger</c> (issue #1103) - the same arrangement as the
/// platform audit sinks: the Application layer declares the fixed set of events, and this is the one
/// place they become log lines.
///
/// <para><strong>What cannot be logged is not a parameter anywhere above.</strong> There is no
/// overload here that accepts a message body, a receipt handle, a card or customer value, an AWS key,
/// a queue URL or a sender id, so none of them can reach a sink by anybody forgetting to withhold it
/// (AGENTS.md § Nayax integration). What a line carries is a queue message id, a business id, a
/// count, a duration, a fixed reason code and an exception type name.</para>
///
/// <para>Structured names rather than interpolation, so the fields are queryable in Azure Monitor and
/// so no formatted string can accidentally be assembled from something wider than the parameter
/// list.</para>
/// </summary>
public sealed class LoggingNayaxSqsReaderLog : INayaxSqsReaderLog
{
    private readonly ILogger<LoggingNayaxSqsReaderLog> _logger;

    public LoggingNayaxSqsReaderLog(ILogger<LoggingNayaxSqsReaderLog> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public void ReaderNotConfigured() =>
        _logger.LogInformation(
            "The Nayax SQS sales reader is not started: no {Setting} is configured. Nayax sales are "
                + "imported by the request-driven synchronization only.",
            $"{NayaxSqsOptions.SectionName}:{nameof(NayaxSqsOptions.QueueUrl)}");

    /// <inheritdoc />
    public void ReaderStarted() =>
        _logger.LogInformation("The Nayax SQS sales reader has started polling.");

    /// <inheritdoc />
    public void ReaderStopped() =>
        _logger.LogInformation("The Nayax SQS sales reader has stopped.");

    /// <inheritdoc />
    public void PollFailed(string failureType, int consecutivePollFailures, TimeSpan backoff) =>
        _logger.LogWarning(
            "Reading the Nayax SQS queue failed with {FailureType}; {ConsecutivePollFailures} in a "
                + "row. Waiting {BackoffSeconds}s before the next attempt.",
            failureType,
            consecutivePollFailures,
            (int)backoff.TotalSeconds);

    /// <inheritdoc />
    public void MessageHandled(
        string messageId,
        int? businessId,
        NayaxSqsDisposition disposition,
        NayaxSqsProcessingReason reason) =>
        _logger.LogInformation(
            "Nayax SQS message {MessageId} for business {BusinessId}: {Disposition} ({Reason}).",
            messageId,
            businessId,
            disposition,
            reason);

    /// <inheritdoc />
    public void VisibilityExtensionFailed(string messageId, string failureType) =>
        _logger.LogWarning(
            "The visibility of Nayax SQS message {MessageId} could not be extended ({FailureType}), "
                + "so it was not processed in this batch; another receiver may now hold it.",
            messageId,
            failureType);

    /// <inheritdoc />
    public void VisibilityChangeFailed(string messageId, string failureType) =>
        _logger.LogWarning(
            "The visibility of Nayax SQS message {MessageId} could not be set ({FailureType}); it "
                + "becomes available again when its current window expires.",
            messageId,
            failureType);

    /// <inheritdoc />
    public void DeleteFailed(string messageId, string failureType) =>
        _logger.LogWarning(
            "Nayax SQS message {MessageId} was processed but could not be deleted ({FailureType}); "
                + "its redelivery will find the sale already stored.",
            messageId,
            failureType);

    /// <inheritdoc />
    public void DeadLetterQueueNotEmpty(int depth) =>
        _logger.LogWarning(
            "The Nayax SQS dead-letter queue holds {DeadLetterQueueDepth} message(s). They are "
                + "samples of transactions this application refused; replaying them is an authorized "
                + "console procedure after the cause has been fixed.",
            depth);

    /// <inheritdoc />
    public void DeadLetterDepthReadFailed(string failureType) =>
        _logger.LogWarning(
            "The Nayax SQS dead-letter queue depth could not be read ({FailureType}). The main queue "
                + "is still being polled.",
            failureType);
}
