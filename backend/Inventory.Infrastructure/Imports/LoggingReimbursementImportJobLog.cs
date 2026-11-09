using Inventory.Application.Imports;
using Microsoft.Extensions.Logging;

namespace Inventory.Infrastructure.Imports;

/// <summary>
/// <see cref="IReimbursementImportJobLog"/> over <c>ILogger</c> (issue #1149) - the arrangement
/// <c>LoggingNayaxSalesReconciliationLog</c> has: the Application layer declares the fixed set of
/// events, and this is the one place they become log lines.
///
/// <para><strong>What cannot be logged is not a parameter anywhere above.</strong> No method of the
/// port accepts XML content, a reimbursement figure, a fee, a payment method, a customer id or a
/// company name, so none of them can reach a sink by anybody forgetting to withhold it. What a line
/// carries is the business's own key, the job's key, a fixed outcome and counts - and a file name may
/// appear, which is the one thing about an import that may, because an operator has to be able to
/// find the file a job is talking about.</para>
///
/// <para>Structured names rather than interpolation, so the fields are queryable in Azure Monitor
/// and so no formatted string can accidentally be assembled from something wider than the parameter
/// list.</para>
/// </summary>
public sealed class LoggingReimbursementImportJobLog : IReimbursementImportJobLog
{
    private readonly ILogger<LoggingReimbursementImportJobLog> _logger;

    public LoggingReimbursementImportJobLog(ILogger<LoggingReimbursementImportJobLog> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public void WorkerStarted() =>
        _logger.LogInformation(
            "The reimbursement report import worker has started; it first looks for work in "
                + "{FirstPassSeconds} seconds and then every {IdleSeconds} seconds while idle.",
            (int)ReimbursementImportJobRunner.FirstPassDelay.TotalSeconds,
            (int)ReimbursementImportJobRunner.IdleDelay.TotalSeconds);

    /// <inheritdoc />
    public void WorkerStopped() =>
        _logger.LogInformation("The reimbursement report import worker has stopped.");

    /// <inheritdoc />
    public void JobProcessed(int businessId, int jobId, ReimbursementImportJobRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        _logger.LogInformation(
            "Reimbursement report import job {JobId} of business {BusinessId}: {Outcome}, "
                + "imported={Imported}, skipped={Skipped}, failed={Failed}, processedNow={ProcessedNow}.",
            jobId,
            businessId,
            result.Outcome,
            result.Counts.Imported,
            result.Counts.Skipped,
            result.Counts.Failed,
            result.FilesProcessedNow);
    }

    /// <inheritdoc />
    public void JobFailed(int businessId, int jobId, string failureType) =>
        _logger.LogWarning(
            "Reimbursement report import job {JobId} of business {BusinessId} ended on "
                + "{FailureType}; the other jobs continue and this one is picked up again when it is "
                + "next due.",
            jobId,
            businessId,
            failureType);

    /// <inheritdoc />
    public void DiscoveryFailed(string failureType) =>
        _logger.LogWarning(
            "Looking for the next due reimbursement report import job failed with {FailureType}; "
                + "nothing was processed and the next pass is the retry.",
            failureType);
}
