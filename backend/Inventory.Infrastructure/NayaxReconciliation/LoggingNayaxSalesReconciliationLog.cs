using Inventory.Application.NayaxReconciliation;
using Microsoft.Extensions.Logging;

namespace Inventory.Infrastructure.NayaxReconciliation;

/// <summary>
/// <see cref="INayaxSalesReconciliationLog"/> over <c>ILogger</c> (issue #1104) - the arrangement
/// <c>LoggingNayaxSqsReaderLog</c> has: the Application layer declares the fixed set of events, and
/// this is the one place they become log lines.
///
/// <para><strong>What cannot be logged is not a parameter anywhere above.</strong> There is no
/// overload here that accepts a transaction id, an amount, a currency, a machine id or name, a
/// product, a sender id or a Nayax credential, so none of them can reach a sink by anybody forgetting
/// to withhold it (AGENTS.md § Nayax integration). What a business's line carries is its own key, a
/// fixed outcome and counts - which is exactly what the issue asks for.</para>
///
/// <para>Structured names rather than interpolation, so the fields are queryable in Azure Monitor and
/// so no formatted string can accidentally be assembled from something wider than the parameter
/// list.</para>
/// </summary>
public sealed class LoggingNayaxSalesReconciliationLog : INayaxSalesReconciliationLog
{
    private readonly ILogger<LoggingNayaxSalesReconciliationLog> _logger;

    public LoggingNayaxSalesReconciliationLog(ILogger<LoggingNayaxSalesReconciliationLog> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public void ReconciliationStarted() =>
        _logger.LogInformation(
            "The hourly Nayax sales reconciliation has started; the first sweep runs in "
                + "{FirstSweepMinutes} minutes, then every {SweepIntervalMinutes} minutes.",
            (int)NayaxSalesReconciliationRunner.FirstSweepDelay.TotalMinutes,
            (int)NayaxSalesReconciliationRunner.SweepInterval.TotalMinutes);

    /// <inheritdoc />
    public void ReconciliationStopped() =>
        _logger.LogInformation("The hourly Nayax sales reconciliation has stopped.");

    /// <inheritdoc />
    public void SweepStarted(int eligibleBusinesses) =>
        _logger.LogInformation(
            "Reconciling Nayax sales for {EligibleBusinesses} businesses.", eligibleBusinesses);

    /// <inheritdoc />
    public void BusinessReconciled(int businessId, NayaxSalesReconciliationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        _logger.LogInformation(
            "Reconciled Nayax sales for business {BusinessId}: {Outcome}, succeeded={Succeeded}, "
                + "read={ItemsRead}, added={SalesAdded}, verified={Verified}, conflict={Conflicts}, "
                + "needsInvestigation={NeedsInvestigation}, absence={AbsenceRows}, "
                + "skipped={EvidenceSkipped}, failure={Failure}.",
            businessId,
            result.Outcome,
            result.Succeeded,
            result.ItemsRead,
            result.SalesAdded,
            result.Verified,
            result.Conflicts,
            result.NeedsInvestigation,
            result.AbsenceRows,
            result.EvidenceSkipped,
            result.Failure ?? "none");
    }

    /// <inheritdoc />
    public void BusinessFailed(int businessId, string failureType) =>
        _logger.LogWarning(
            "Reconciling Nayax sales for business {BusinessId} ended on {FailureType}; the other "
                + "businesses continue and the next sweep retries this one.",
            businessId,
            failureType);

    /// <inheritdoc />
    public void EligibilityReadFailed(string failureType) =>
        _logger.LogWarning(
            "Reading which businesses are eligible for the hourly Nayax sales reconciliation failed "
                + "with {FailureType}; this sweep ran for nobody and the next one is the retry.",
            failureType);
}
