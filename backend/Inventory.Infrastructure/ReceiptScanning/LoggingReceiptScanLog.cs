using Inventory.Application.ReceiptScanning;
using Inventory.Domain.ReceiptScanning;
using Microsoft.Extensions.Logging;

namespace Inventory.Infrastructure.ReceiptScanning;

/// <summary>
/// Writes the one log line a receipt scan produces (issue #1163), through <c>ILogger</c>.
///
/// <para><strong>What it writes is the whole of what it can write.</strong> The business, the
/// outcome, the duration, the page count and the token counts - every one of them a number or a
/// declared enum member. A receipt is a business document and the products on it are the business's
/// own data, so nothing about either is here, and the port's own shape is what makes that true rather
/// than this implementation's restraint.</para>
///
/// <para><strong>An unavailable scan writes one warning beside that line</strong> (issue #1236), so
/// the owner can read why a scan answered "Receipt scanning is temporarily unavailable" from the App
/// Service log stream instead of a developer reproducing the request. Both lines begin "Receipt scan
/// for business", which is the one search that finds them together. The warning's fields are
/// structured names rather than interpolation, so they are queryable in Azure Monitor and no
/// formatted string can be assembled from anything wider than the failure itself - which can hold
/// only a status, the provider's error type, the provider's message capped on one line (or, when
/// that message quoted the request, the fact that it was withheld) and the provider's request id.
/// <strong>The upstream error body is never logged</strong>: it can quote the request, and the
/// request held a receipt.</para>
/// </summary>
public sealed class LoggingReceiptScanLog : IReceiptScanLog
{
    private readonly ILogger<LoggingReceiptScanLog> _logger;

    public LoggingReceiptScanLog(ILogger<LoggingReceiptScanLog> logger) => _logger = logger;

    /// <inheritdoc />
    public void Completed(
        int businessId,
        ScanReceiptOutcome outcome,
        TimeSpan duration,
        int? pageCount,
        int? inputTokens,
        int? outputTokens) =>
        _logger.LogInformation(
            "Receipt scan for business {BusinessId} ended as {Outcome} in {DurationMs} ms "
                + "(pages: {PageCount}, input tokens: {InputTokens}, output tokens: {OutputTokens}).",
            businessId,
            outcome,
            (long)duration.TotalMilliseconds,
            pageCount,
            inputTokens,
            outputTokens);

    /// <inheritdoc />
    public void Unavailable(int businessId, ReceiptReaderFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        if (failure.StatusCode is { } statusCode)
        {
            // A refusal the reader answered. The error type, the message and the request id are
            // whatever the answer actually stated, and are absent when it stated none of them.
            _logger.LogWarning(
                "Receipt scan for business {BusinessId} was refused by the receipt reader with "
                    + "status {StatusCode} (error type: {ErrorType}, error message: {ErrorMessage}, "
                    + "message withheld: {MessageWithheld}, request id: {RequestId}).",
                businessId,
                statusCode,
                failure.ErrorType,
                failure.ErrorMessage,
                failure.MessageWithheld,
                failure.RequestId);

            return;
        }

        // No answer was read, so there is no status to record and nothing the provider said: which
        // failure it was is the whole of what is known.
        _logger.LogWarning(
            "Receipt scan for business {BusinessId} could not use the receipt reader: {Failure}. "
                + "No upstream status was answered.",
            businessId,
            failure.Kind);
    }
}
