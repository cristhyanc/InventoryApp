namespace Inventory.Application.Imports;

/// <summary>
/// How the reimbursement import worker waits, behind a port so its cadence can be driven by a test
/// rather than by elapsed time (issue #1149) - the shape <c>INayaxSalesReconciliationSchedule</c>
/// has.
/// </summary>
public interface IReimbursementImportJobSchedule
{
    /// <summary>Waits <paramref name="delay"/>, or until the host stops.</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>
/// One business's scope for one reimbursement import job (issue #1149). Disposing it closes the
/// service scope and everything resolved in it.
/// </summary>
public interface IReimbursementImportJobScope : IAsyncDisposable
{
    /// <summary>Runs one attempt on one job of that business.</summary>
    Task<ReimbursementImportJobRunResult> ProcessAsync(
        int jobId, CancellationToken cancellationToken);
}

/// <summary>
/// The two service scopes the reimbursement import worker needs (issue #1149), opened by the host -
/// <c>INayaxSalesReconciliationScopes</c>'s shape exactly.
/// </summary>
public interface IReimbursementImportJobScopes
{
    /// <summary>
    /// Finds the next due job across every business, in a scope that has deliberately resolved
    /// <em>no</em> business - the state <see cref="IReimbursementImportJobQueue"/>'s one reviewed
    /// cross-business statement is written for.
    /// </summary>
    Task<DueReimbursementImportJob?> FindNextDueJobAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Opens a scope resolved to exactly one business, for the job that business owns.
    /// </summary>
    IReimbursementImportJobScope CreateForBusiness(int businessId);
}

/// <summary>
/// What the reimbursement import worker reports, behind a port so nothing in
/// <c>Inventory.Application</c> depends on a logging framework (issue #1149).
///
/// <para><strong>No method here takes XML content, a figure or a payment detail.</strong> A file
/// name may appear, which is the one thing about an import that may: an operator has to be able to
/// find the file a job is talking about.</para>
/// </summary>
public interface IReimbursementImportJobLog
{
    /// <summary>The worker's loop has started.</summary>
    void WorkerStarted();

    /// <summary>The worker's loop has stopped, because the host is stopping.</summary>
    void WorkerStopped();

    /// <summary>One attempt on one job finished, with its outcome and counts.</summary>
    void JobProcessed(int businessId, int jobId, ReimbursementImportJobRunResult result);

    /// <summary>
    /// Something escaped one job's own handling. The type name only, never the message.
    /// </summary>
    void JobFailed(int businessId, int jobId, string failureType);

    /// <summary>
    /// The discovery read itself failed, so this pass found nobody's job. The type name only.
    /// </summary>
    void DiscoveryFailed(string failureType);
}

/// <summary>
/// The background worker that imports uploaded reimbursement report archives (issue #1149): the
/// oldest due job, one at a time, across every business.
///
/// <para><strong>One job at a time, on purpose.</strong> The production store is a single SQLite
/// file with one writer (docs/architecture.md § SQLite operating assumptions), and an import is
/// almost entirely writing. Running jobs in parallel would contend for the one writer for no gain:
/// nothing is waiting on a settlement import, and the upload has already been acknowledged.</para>
///
/// <para><strong>One job's trouble never stops another.</strong> The use case turns anything about a
/// job into a recorded outcome rather than an exception, and this loop catches whatever still
/// escapes - so a business with a damaged archive or a held write lock costs the others nothing. The
/// <em>only</em> thing that ends the loop is cancellation.</para>
///
/// <para><strong>A job waiting to retry never holds anybody up.</strong> The discovery read answers
/// with the oldest job that is <em>due</em>, so a job backing off is simply not offered until its
/// not-before instant passes, and the jobs behind it - of its own business or of any other - are
/// worked through meanwhile.</para>
///
/// <para><strong>It is in-process, which is the current decision rather than an oversight.</strong>
/// docs/architecture.md § Azure Functions and background workloads is explicit about work like this:
/// put it in <c>Inventory.Application</c> as a use case behind narrow ports and invoke it from the
/// thinnest possible adapter. Running here keeps one writer on the one database file, needs no
/// second host, identity or configuration surface, and reads each business's own data exactly as a
/// request does. App Service <strong>Always On</strong> is what keeps the process alive to pick jobs
/// up; without it a queued job waits until the next request wakes the app.</para>
/// </summary>
public sealed class ReimbursementImportJobRunner
{
    /// <summary>
    /// How long after startup the worker first looks for work: about thirty seconds, so a
    /// deployment restart is not also a burst of import writing while the API is serving the
    /// requests that were waiting for it.
    /// </summary>
    public static readonly TimeSpan FirstPassDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the worker waits after finding nothing to do: fifteen seconds. An upload is
    /// acknowledged immediately and its importing starts within seconds, which is what the feature
    /// promises; polling more often would spend reads on an idle queue for no benefit.
    /// </summary>
    public static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(15);

    private readonly IReimbursementImportJobScopes _scopes;
    private readonly IReimbursementImportJobSchedule _schedule;
    private readonly IReimbursementImportJobLog _log;

    public ReimbursementImportJobRunner(
        IReimbursementImportJobScopes scopes,
        IReimbursementImportJobSchedule schedule,
        IReimbursementImportJobLog log)
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
    /// <c>NayaxSalesReconciliationRunner.RunAsync</c> is.</para>
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
            // Shutdown. A job left Processing is resumed by the next process to run this loop, and
            // the files it had already committed keep the outcomes they were committed with.
        }
        finally
        {
            _log.WorkerStopped();
        }
    }

    /// <summary>
    /// One pass: find the oldest due job and run one attempt on it. <see langword="false"/> means
    /// there was nothing due. Public so a test can drive exactly one pass.
    /// </summary>
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        DueReimbursementImportJob? due;
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
            // opened is still due, so looking again immediately would spin on it. Waiting the idle
            // delay turns that into one bounded attempt every fifteen seconds instead.
            _log.JobFailed(job.BusinessId, job.JobId, exception.GetType().Name);
            return false;
        }

        return true;
    }

    private static bool NotCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested;
}
