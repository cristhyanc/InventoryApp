namespace Inventory.Application.NayaxReconciliation;

/// <summary>
/// How the hourly reconciliation waits, behind a port so its cadence can be driven by a test rather
/// than by elapsed time (issue #1104) - the shape <c>INayaxSqsReaderPacing</c> has.
/// </summary>
public interface INayaxSalesReconciliationSchedule
{
    /// <summary>Waits <paramref name="delay"/>, or until the host stops.</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>What one sweep over the eligible businesses did (issue #1104).</summary>
/// <param name="EligibleBusinesses">How many businesses the eligibility read answered with.</param>
/// <param name="Succeeded">How many of their runs counted as a full success.</param>
/// <param name="Skipped">
/// How many were skipped - the lock was held, the allowance was spent, or a gate refused them.
/// </param>
/// <param name="Failed">How many ended on a failure.</param>
/// <param name="EligibilityFailure">
/// The type name of the failure the eligibility read ended on, or <see langword="null"/>. A sweep
/// that could not find out who was eligible ran for nobody and changed nothing.
/// </param>
public sealed record NayaxSalesReconciliationSweep(
    int EligibleBusinesses,
    int Succeeded,
    int Skipped,
    int Failed,
    string? EligibilityFailure);

/// <summary>
/// The hourly schedule for the Nayax sales reconciliation (issue #1104, child of #1100): every
/// eligible business, one at a time, once an hour.
///
/// <para><strong>One business at a time, on purpose.</strong> The production store is a single SQLite
/// file with one writer (docs/architecture.md § SQLite operating assumptions), and each run also
/// spends one Nayax request per machine plus one against that business's own operator account.
/// Running businesses in parallel would contend for the one writer and burst the upstream for no
/// gain: an hour is a long time and nothing is waiting on this.</para>
///
/// <para><strong>One business's failure never stops the others.</strong> The use case turns anything
/// about a business into an outcome rather than an exception, and this loop catches whatever still
/// escapes - so a business with an unreachable Nayax account, an unreplayable costing history or a
/// held lock costs the others nothing. The <em>only</em> thing that ends the loop is cancellation.</para>
///
/// <para><strong>It is in-process, which is the current decision rather than an oversight.</strong>
/// docs/architecture.md § Azure Functions and background workloads is explicit about scheduled work:
/// put it in <c>Inventory.Application</c> as a use case behind narrow ports and invoke it from the
/// thinnest possible adapter. This runs beside the SQS reader of issue #1103 in the API process,
/// which keeps one writer on the one database file, needs no second host, identity or configuration
/// surface, and reads each business's credentials exactly as a request does. App Service
/// <strong>Always On</strong> is the human prerequisite that keeps the process alive to run it.</para>
///
/// <para><strong>The first sweep is five minutes after startup</strong>, not immediately: a
/// deployment restart is exactly when the API is busiest serving the requests that were waiting for
/// it, and the reconciliation has nothing urgent to do. After that it is every sixty minutes,
/// measured from the end of the previous sweep - so a long sweep delays the next one rather than
/// overlapping it, which a fixed wall-clock schedule would allow.</para>
/// </summary>
public sealed class NayaxSalesReconciliationRunner
{
    /// <summary>
    /// How long after startup the first sweep runs: about five minutes, so a restart is not also a
    /// burst of Nayax reads while the API is serving the requests that were waiting for it.
    /// </summary>
    public static readonly TimeSpan FirstSweepDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long between sweeps: sixty minutes, the repository owner's decision of 5 November 2026.
    /// </summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(60);

    private readonly INayaxSalesReconciliationScopes _scopes;
    private readonly INayaxSalesReconciliationSchedule _schedule;
    private readonly INayaxSalesReconciliationLog _log;

    public NayaxSalesReconciliationRunner(
        INayaxSalesReconciliationScopes scopes,
        INayaxSalesReconciliationSchedule schedule,
        INayaxSalesReconciliationLog log)
    {
        _scopes = scopes;
        _schedule = schedule;
        _log = log;
    }

    /// <summary>
    /// Sweeps until <paramref name="stoppingToken"/> is cancelled.
    ///
    /// <para>Public and driveable so it can be exercised as what it is - a loop with a beginning and
    /// an end - rather than through a hosted service's start/stop timing, exactly as
    /// <c>NayaxSqsSalesReader.RunAsync</c> and <c>MemberProfileRefreshWorker.DrainAsync</c> are.</para>
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        _log.ReconciliationStarted();

        try
        {
            await _schedule.DelayAsync(FirstSweepDelay, stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
                await _schedule.DelayAsync(SweepInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown. A business not reached keeps its last success instant, so the next process to
            // run this sweep reconciles from the state that really exists.
        }
        finally
        {
            _log.ReconciliationStopped();
        }
    }

    /// <summary>
    /// One sweep: every eligible business, in turn. Public so a test can drive exactly one.
    /// </summary>
    public async Task<NayaxSalesReconciliationSweep> SweepOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<int> businessIds;
        try
        {
            businessIds = await _scopes
                .ListEligibleBusinessIdsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (NotCancellation(exception, cancellationToken))
        {
            _log.EligibilityReadFailed(exception.GetType().Name);
            return new NayaxSalesReconciliationSweep(0, 0, 0, 0, exception.GetType().Name);
        }

        _log.SweepStarted(businessIds.Count);

        var succeeded = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var businessId in businessIds)
        {
            // Shutdown stops the sweep rather than rushing it. The businesses not reached simply keep
            // their last success instant.
            cancellationToken.ThrowIfCancellationRequested();

            var result = await ReconcileAsync(businessId, cancellationToken).ConfigureAwait(false);

            if (result is null)
            {
                failed++;
                continue;
            }

            _log.BusinessReconciled(businessId, result);

            switch (result.Outcome)
            {
                case NayaxSalesReconciliationOutcome.Succeeded:
                    succeeded++;
                    break;
                case NayaxSalesReconciliationOutcome.Failed:
                    failed++;
                    break;
                default:
                    skipped++;
                    break;
            }
        }

        return new NayaxSalesReconciliationSweep(businessIds.Count, succeeded, skipped, failed, null);
    }

    /// <summary>
    /// One business's run in its own scope, or <see langword="null"/> when something escaped the use
    /// case - which it should not, and which is caught here anyway because the alternative is one
    /// business ending the whole sweep.
    /// </summary>
    private async Task<NayaxSalesReconciliationResult?> ReconcileAsync(
        int businessId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopes.CreateForBusiness(businessId);

            return await scope.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (NotCancellation(exception, cancellationToken))
        {
            _log.BusinessFailed(businessId, exception.GetType().Name);
            return null;
        }
    }

    private static bool NotCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested;
}
