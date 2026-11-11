using Inventory.Application.ReceiptScanning;
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
}
