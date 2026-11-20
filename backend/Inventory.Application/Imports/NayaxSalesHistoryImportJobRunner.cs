namespace Inventory.Application.Imports;

/// <summary>
/// How the sales history import worker waits, behind a port so its cadence can be driven by a test
/// rather than by elapsed time (issue #1304) - <see cref="IReimbursementImportJobSchedule"/>'s shape.
/// </summary>
public interface INayaxSalesHistoryImportJobSchedule
{
    /// <summary>Waits <paramref name="delay"/>, or until the host stops.</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>
/// One business's scope for one sales history import job (issue #1304). Disposing it closes the
/// service scope and everything resolved in it.
/// </summary>
public interface INayaxSalesHistoryImportJobScope : IAsyncDisposable
{
    /// <summary>Runs one attempt on one job of that business.</summary>
    Task<NayaxSalesHistoryImportJobRunResult> ProcessAsync(
        int jobId, CancellationToken cancellationToken);
}

/// <summary>
/// The two service scopes the sales history import worker needs (issue #1304), opened by the host -
/// <see cref="IReimbursementImportJobScopes"/>'s shape exactly.
/// </summary>
public interface INayaxSalesHistoryImportJobScopes
{
    /// <summary>
    /// Finds the next due job across every business, in a scope that has deliberately resolved
    /// <em>no</em> business - the state <see cref="INayaxSalesHistoryImportJobQueue"/>'s one reviewed
    /// cross-business statement is written for.
    /// </summary>
    Task<DueNayaxSalesHistoryImportJob?> FindNextDueJobAsync(CancellationToken cancellationToken);

    /// <summary>Opens a scope resolved to exactly one business, for the job that business owns.</summary>
    INayaxSalesHistoryImportJobScope CreateForBusiness(int businessId);
}

/// <summary>
/// What the sales history import worker reports, behind a port so nothing in
/// <c>Inventory.Application</c> depends on a logging framework (issue #1304).
///
/// <para><strong>No method here takes a sale, a figure or a payment detail.</strong> A file name may
/// appear, which is the one thing about an import that may: an operator has to be able to find the
/// file a job is talking about.</para>
/// </summary>
public interface INayaxSalesHistoryImportJobLog
{
    /// <summary>The worker's loop has started.</summary>
    void WorkerStarted();

    /// <summary>The worker's loop has stopped, because the host is stopping.</summary>
    void WorkerStopped();

    /// <summary>One attempt on one job finished, with its outcome and progress.</summary>
    void JobProcessed(int businessId, int jobId, NayaxSalesHistoryImportJobRunResult result);

    /// <summary>Something escaped one job's own handling. The type name only, never the message.</summary>
    void JobFailed(int businessId, int jobId, string failureType);

    /// <summary>The discovery read itself failed, so this pass found nobody's job. The type name only.</summary>
    void DiscoveryFailed(string failureType);
}

/// <summary>
/// The background worker that imports uploaded Nayax sales histories (issue #1304): the oldest
/// unfinished job, one at a time, across every business.
///
/// <para><strong>One job at a time, on purpose.</strong> The production store is a single SQLite
/// file with one writer (docs/architecture.md § SQLite operating assumptions), and an import is
/// almost entirely writing. Running jobs in parallel would contend for the one writer for no gain:
/// nothing is waiting on a history import, and the upload has already been acknowledged. It is also
/// what makes the batch write-lock budget mean anything - two jobs would double the time every other
/// writer waits.</para>
///
/// <para><strong>One job's trouble never stops another.</strong> The use case turns anything about a
/// job into a recorded state rather than an exception, and this loop catches whatever still escapes -
/// so a business with a damaged file or a held write lock costs the others nothing. The <em>only</em>
/// thing that ends the loop is cancellation.</para>
///
/// <para><strong>It is in-process, which is the current decision rather than an oversight.</strong>
/// docs/architecture.md § Azure Functions and background workloads is explicit about work like this:
/// put it in <c>Inventory.Application</c> as a use case behind narrow ports and invoke it from the
/// thinnest possible adapter. Running here keeps one writer on the one database file, needs no
/// second host, identity or configuration surface, and reads each business's own data exactly as a
/// request does. App Service <strong>Always On</strong> is what keeps the process alive to pick jobs
/// up; without it a queued job waits until the next request wakes the app.</para>
/// </summary>
public sealed class NayaxSalesHistoryImportJobRunner
{
    /// <summary>
    /// How long after startup the worker first looks for work: about forty-five seconds, a little
    /// after the reimbursement worker's own first pass, so a deployment restart is not also two
    /// imports competing for the write lock while the API serves the requests that were waiting for
    /// it.
    /// </summary>
    public static readonly TimeSpan FirstPassDelay = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long the worker waits after finding nothing to do: fifteen seconds. An upload is
    /// acknowledged immediately and its importing starts within seconds, which is what the feature
    /// promises; polling more often would spend reads on an idle queue for no benefit.
    /// </summary>
    public static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(15);

    private readonly INayaxSalesHistoryImportJobScopes _scopes;
    private readonly INayaxSalesHistoryImportJobSchedule _schedule;
    private readonly INayaxSalesHistoryImportJobLog _log;

    public NayaxSalesHistoryImportJobRunner(
        INayaxSalesHistoryImportJobScopes scopes,
        INayaxSalesHistoryImportJobSchedule schedule,
        INayaxSalesHistoryImportJobLog log)
    {
        _scopes = scopes;
        _schedule = schedule;
        _log = log;
    }

    /// <summary>
    /// Works until <paramref name="stoppingToken"/> is cancelled.
    ///
    /// <para>Public and driveable so it can be exercised as what it is - a loop with a beginning and
    /// an end - rather than through a hosted service's start/stop timing, exactly as
    /// <see cref="ReimbursementImportJobRunner.RunAsync"/> is.</para>
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        _log.WorkerStarted();

        try
        {
            await _schedule.DelayAsync(FirstPassDelay, stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                var didWork = await ProcessNextAsync(stoppingToken).ConfigureAwait(false);

                // A pass that did something looks again immediately: a backlog of uploads drains in
                // one go rather than one job every fifteen seconds.
                if (!didWork)
                    await _schedule.DelayAsync(IdleDelay, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown. A job left Processing is resumed by the next process to run this loop, from
            // the batches it had already committed.
        }
        finally
        {
            _log.WorkerStopped();
        }
    }

    /// <summary>
    /// One pass: find the oldest unfinished job and run one attempt on it. <see langword="false"/>
    /// means there was nothing waiting. Public so a test can drive exactly one pass.
    /// </summary>
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        DueNayaxSalesHistoryImportJob? due;
        try
        {
            due = await _scopes.FindNextDueJobAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (NotCancellation(exception, cancellationToken))
        {
            _log.DiscoveryFailed(exception.GetType().Name);
            return false;
        }

        if (due is null) return false;

        var job = due.Value;

        try
        {
            await using var scope = _scopes.CreateForBusiness(job.BusinessId);

            var result = await scope.ProcessAsync(job.JobId, cancellationToken).ConfigureAwait(false);

            _log.JobProcessed(job.BusinessId, job.JobId, result);
        }
        catch (Exception exception) when (NotCancellation(exception, cancellationToken))
        {
            // The use case records its own failures, so this is the scope itself or something it
            // could not translate. The job keeps whatever state it has - which, for a job left
            // Processing, is resumed with its next attempt - and the loop carries on.
            //
            // It is reported as "no work done" deliberately: a job whose scope cannot even be
            // opened is still waiting, so looking again immediately would spin on it. Waiting the
            // idle delay turns that into one bounded attempt every fifteen seconds instead.
            _log.JobFailed(job.BusinessId, job.JobId, exception.GetType().Name);
            return false;
        }

        return true;
    }

    private static bool NotCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested;
}
