using Inventory.Application.Imports;
using Microsoft.Extensions.Logging;

namespace Inventory.Infrastructure.Imports;

/// <summary>
/// <see cref="INayaxSalesHistoryImportJobLog"/> over <c>ILogger</c> (issue #1304) - the arrangement
/// <see cref="LoggingReimbursementImportJobLog"/> has: the Application layer declares the fixed set
/// of events, and this is the one place they become log lines.
///
/// <para><strong>What cannot be logged is not a parameter anywhere above.</strong> No method of the
/// port accepts a sale, a transaction id, an amount, a payment method, a product name or a machine
/// name, so none of them can reach a sink by anybody forgetting to withhold it. What a line carries
/// is the business's own key, the job's key, a fixed outcome and counts of rows.</para>
///
/// <para>Structured names rather than interpolation, so the fields are queryable in Azure Monitor
/// and so no formatted string can accidentally be assembled from something wider than the parameter
/// list.</para>
/// </summary>
public sealed class LoggingNayaxSalesHistoryImportJobLog : INayaxSalesHistoryImportJobLog
{
    private readonly ILogger<LoggingNayaxSalesHistoryImportJobLog> _logger;

    public LoggingNayaxSalesHistoryImportJobLog(ILogger<LoggingNayaxSalesHistoryImportJobLog> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public void WorkerStarted() =>
        _logger.LogInformation(
            "The Nayax sales history import worker has started; it first looks for work in "
                + "{FirstPassSeconds} seconds and then every {IdleSeconds} seconds while idle.",
            (int)NayaxSalesHistoryImportJobRunner.FirstPassDelay.TotalSeconds,
            (int)NayaxSalesHistoryImportJobRunner.IdleDelay.TotalSeconds);

    /// <inheritdoc />
    public void WorkerStopped() =>
        _logger.LogInformation("The Nayax sales history import worker has stopped.");

    /// <inheritdoc />
    public void JobProcessed(int businessId, int jobId, NayaxSalesHistoryImportJobRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        _logger.LogInformation(
            "Nayax sales history import job {JobId} of business {BusinessId}: {Outcome}, "
                + "rowsRead={RowsRead}, created={Created}, updated={Updated}, skipped={Skipped}, "
                + "invalid={Invalid}, rowsNow={RowsNow}, batchesNow={BatchesNow}, "
                + "salesDateRangeStatistics={StatisticsRefresh}.",
            jobId,
            businessId,
            result.Outcome,
            result.Progress.RowsRead,
            result.Progress.Created,
            result.Progress.Updated,
            result.Progress.Skipped,
            result.Progress.Invalid,
            result.RowsProcessedNow,
            result.BatchesCommittedNow,
            // Issue #1287: what the finished job's statistics refresh did, or that it was not asked.
            result.StatisticsRefresh?.ToString() ?? "notAsked");
    }

    /// <inheritdoc />
    public void JobFailed(int businessId, int jobId, string failureType) =>
        _logger.LogWarning(
            "Nayax sales history import job {JobId} of business {BusinessId} ended on "
                + "{FailureType}; the batches it committed are imported, the other jobs continue and "
                + "this one is picked up again on a later pass.",
            jobId,
            businessId,
            failureType);

    /// <inheritdoc />
    public void DiscoveryFailed(string failureType) =>
        _logger.LogWarning(
            "Looking for the next Nayax sales history import job failed with {FailureType}; "
                + "nothing was processed and the next pass is the retry.",
            failureType);
}
