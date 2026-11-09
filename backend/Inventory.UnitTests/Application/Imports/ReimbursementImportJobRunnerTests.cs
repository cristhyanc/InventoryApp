using Inventory.Application.Imports;
using Inventory.Domain.Imports;
using Xunit;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// How the reimbursement import worker of issue #1149 picks work up: the oldest due job, one at a
/// time, each in its own business's scope, and one job's trouble never stopping another's.
///
/// <para><strong>The waiting is a port, so none of these cases waits.</strong> The runner asks its
/// schedule to delay and a fake records the duration and returns, which is what makes "thirty
/// seconds, then every fifteen while idle" assertable at all.</para>
/// </summary>
public class ReimbursementImportJobRunnerTests
{
    [Fact]
    public async Task The_first_pass_is_thirty_seconds_after_startup_and_then_every_fifteen_when_idle()
    {
        using var stopping = new CancellationTokenSource();
        var schedule = new FakeReimbursementImportJobSchedule(stopping, delaysBeforeStopping: 3);
        var scopes = new FakeReimbursementImportJobScopes();
        var log = new FakeReimbursementImportJobLog();

        await new ReimbursementImportJobRunner(scopes, schedule, log).RunAsync(stopping.Token);

        Assert.Equal(
            [
                ReimbursementImportJobRunner.FirstPassDelay,
                ReimbursementImportJobRunner.IdleDelay,
                ReimbursementImportJobRunner.IdleDelay,
            ],
            schedule.Delays);

        Assert.Equal(TimeSpan.FromSeconds(30), ReimbursementImportJobRunner.FirstPassDelay);
        Assert.Equal(TimeSpan.FromSeconds(15), ReimbursementImportJobRunner.IdleDelay);
        Assert.Equal(1, log.Started);
        Assert.Equal(1, log.Stopped);
    }

    /// <summary>Nothing is looked for before the first delay has passed.</summary>
    [Fact]
    public async Task No_work_is_looked_for_before_the_first_delay()
    {
        using var stopping = new CancellationTokenSource();
        var schedule = new FakeReimbursementImportJobSchedule(stopping, delaysBeforeStopping: 1);
        var scopes = new FakeReimbursementImportJobScopes();

        await new ReimbursementImportJobRunner(
                scopes, schedule, new FakeReimbursementImportJobLog())
            .RunAsync(stopping.Token);

        Assert.Equal([ReimbursementImportJobRunner.FirstPassDelay], schedule.Delays);
        Assert.Equal(0, scopes.DiscoveryCalls);
    }

    /// <summary>
    /// A backlog drains in one go: a pass that did something looks again immediately rather than
    /// waiting out the idle delay between every job.
    /// </summary>
    [Fact]
    public async Task A_backlog_is_drained_without_waiting_between_jobs()
    {
        using var stopping = new CancellationTokenSource();
        var scopes = new FakeReimbursementImportJobScopes();
        scopes.Enqueue(new DueReimbursementImportJob(10, 1));
        scopes.Enqueue(new DueReimbursementImportJob(11, 2));
        scopes.Enqueue(new DueReimbursementImportJob(12, 1));
        var schedule = new FakeReimbursementImportJobSchedule(stopping, delaysBeforeStopping: 2);

        await new ReimbursementImportJobRunner(
                scopes, schedule, new FakeReimbursementImportJobLog())
            .RunAsync(stopping.Token);

        // One delay before the first pass and one after the queue ran dry: the three jobs in
        // between were processed back to back.
        Assert.Equal(
            [ReimbursementImportJobRunner.FirstPassDelay, ReimbursementImportJobRunner.IdleDelay],
            schedule.Delays);
        Assert.Equal([(1, 10), (2, 11), (1, 12)], scopes.Processed);
    }

    /// <summary>
    /// Each job runs in a scope opened for its own business, one at a time, and every scope is
    /// closed afterwards - so no context outlives the business it was opened for.
    /// </summary>
    [Fact]
    public async Task Each_job_runs_in_its_own_business_scope_one_at_a_time()
    {
        var scopes = new FakeReimbursementImportJobScopes();
        scopes.Enqueue(new DueReimbursementImportJob(10, 7));
        scopes.Enqueue(new DueReimbursementImportJob(11, 9));

        var runner = Runner(scopes);

        Assert.True(await runner.ProcessNextAsync(CancellationToken.None));
        Assert.True(await runner.ProcessNextAsync(CancellationToken.None));
        Assert.False(await runner.ProcessNextAsync(CancellationToken.None));

        Assert.Equal([7, 9], scopes.ScopesOpened);
        Assert.Equal(1, scopes.MaxConcurrentScopes);
        Assert.Equal(2, scopes.ScopesDisposed);
        Assert.Equal(0, scopes.OpenScopes);
    }

    /// <summary>
    /// One job's trouble costs the others nothing: what escaped is logged and the loop carries on
    /// with the next job.
    /// </summary>
    [Fact]
    public async Task One_jobs_failure_never_stops_another()
    {
        var scopes = new FakeReimbursementImportJobScopes();
        scopes.Enqueue(new DueReimbursementImportJob(10, 7));
        scopes.Enqueue(new DueReimbursementImportJob(11, 9));
        scopes.ThrowFor.Add(10);
        var log = new FakeReimbursementImportJobLog();

        var runner = new ReimbursementImportJobRunner(
            scopes, new ImmediateSchedule(), log);

        await runner.ProcessNextAsync(CancellationToken.None);
        await runner.ProcessNextAsync(CancellationToken.None);

        Assert.Equal([(9, 11)], scopes.Processed);
        Assert.Equal([(7, 10, nameof(InvalidOperationException))], log.Failures);
        Assert.Equal(2, scopes.ScopesDisposed);
    }

    /// <summary>
    /// A discovery read that fails is logged and nothing is processed; the next pass is the retry.
    /// </summary>
    [Fact]
    public async Task A_failed_discovery_read_processes_nothing()
    {
        var scopes = new FakeReimbursementImportJobScopes { DiscoveryThrows = true };
        var log = new FakeReimbursementImportJobLog();

        var didWork = await new ReimbursementImportJobRunner(
                scopes, new ImmediateSchedule(), log)
            .ProcessNextAsync(CancellationToken.None);

        Assert.False(didWork);
        Assert.Empty(scopes.Processed);
        Assert.Equal([nameof(InvalidOperationException)], log.DiscoveryFailures);
    }

    /// <summary>Nothing due is simply nothing to do, and no scope is opened for it.</summary>
    [Fact]
    public async Task Nothing_due_opens_no_scope()
    {
        var scopes = new FakeReimbursementImportJobScopes();

        Assert.False(await Runner(scopes).ProcessNextAsync(CancellationToken.None));
        Assert.Empty(scopes.ScopesOpened);
    }

    private static ReimbursementImportJobRunner Runner(FakeReimbursementImportJobScopes scopes) =>
        new(scopes, new ImmediateSchedule(), new FakeReimbursementImportJobLog());
}

/// <summary>A schedule that returns at once, for the cases that drive passes by hand.</summary>
internal sealed class ImmediateSchedule : IReimbursementImportJobSchedule
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// A schedule that records what it was asked to wait and stops the loop after a fixed number of
/// delays, so the runner's cadence is assertable without any test actually waiting.
/// </summary>
internal sealed class FakeReimbursementImportJobSchedule : IReimbursementImportJobSchedule
{
    private readonly CancellationTokenSource _stopping;
    private readonly int _delaysBeforeStopping;

    public FakeReimbursementImportJobSchedule(
        CancellationTokenSource stopping, int delaysBeforeStopping)
    {
        _stopping = stopping;
        _delaysBeforeStopping = delaysBeforeStopping;
    }

    public List<TimeSpan> Delays { get; } = [];

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Delays.Add(delay);

        if (Delays.Count >= _delaysBeforeStopping) _stopping.Cancel();

        cancellationToken.ThrowIfCancellationRequested();

        return Task.CompletedTask;
    }
}

/// <summary>
/// The worker's two scopes, in memory: a queue of due jobs, and a per-business scope that records
/// that it was opened, used and closed.
/// </summary>
internal sealed class FakeReimbursementImportJobScopes : IReimbursementImportJobScopes
{
    private readonly Queue<DueReimbursementImportJob> _due = new();

    public List<int> ScopesOpened { get; } = [];

    public List<(int BusinessId, int JobId)> Processed { get; } = [];

    public HashSet<int> ThrowFor { get; } = [];

    public bool DiscoveryThrows { get; set; }

    public int DiscoveryCalls { get; private set; }

    public int OpenScopes { get; private set; }

    public int MaxConcurrentScopes { get; private set; }

    public int ScopesDisposed { get; private set; }

    public void Enqueue(DueReimbursementImportJob job) => _due.Enqueue(job);

    public Task<DueReimbursementImportJob?> FindNextDueJobAsync(CancellationToken cancellationToken)
    {
        DiscoveryCalls++;

        if (DiscoveryThrows) throw new InvalidOperationException("the database is unavailable");

        return Task.FromResult(_due.Count == 0 ? null : (DueReimbursementImportJob?)_due.Dequeue());
    }

    public IReimbursementImportJobScope CreateForBusiness(int businessId)
    {
        ScopesOpened.Add(businessId);
        OpenScopes++;
        MaxConcurrentScopes = Math.Max(MaxConcurrentScopes, OpenScopes);

        return new Scope(this, businessId);
    }

    private sealed class Scope : IReimbursementImportJobScope
    {
        private readonly FakeReimbursementImportJobScopes _owner;
        private readonly int _businessId;

        public Scope(FakeReimbursementImportJobScopes owner, int businessId)
        {
            _owner = owner;
            _businessId = businessId;
        }

        public Task<ReimbursementImportJobRunResult> ProcessAsync(
            int jobId, CancellationToken cancellationToken)
        {
            if (_owner.ThrowFor.Contains(jobId))
                throw new InvalidOperationException("the scope could not do the job");

            _owner.Processed.Add((_businessId, jobId));

            return Task.FromResult(new ReimbursementImportJobRunResult(
                ReimbursementImportJobRunOutcome.Finished,
                new ReimbursementImportFileCounts(1, 0, 0),
                1));
        }

        public ValueTask DisposeAsync()
        {
            _owner.OpenScopes--;
            _owner.ScopesDisposed++;

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>The worker's log, in memory.</summary>
internal sealed class FakeReimbursementImportJobLog : IReimbursementImportJobLog
{
    public int Started { get; private set; }

    public int Stopped { get; private set; }

    public List<(int BusinessId, int JobId, ReimbursementImportJobRunOutcome Outcome)> Processed { get; } = [];

    public List<(int BusinessId, int JobId, string FailureType)> Failures { get; } = [];

    public List<string> DiscoveryFailures { get; } = [];

    public void WorkerStarted() => Started++;

    public void WorkerStopped() => Stopped++;

    public void JobProcessed(int businessId, int jobId, ReimbursementImportJobRunResult result) =>
        Processed.Add((businessId, jobId, result.Outcome));

    public void JobFailed(int businessId, int jobId, string failureType) =>
        Failures.Add((businessId, jobId, failureType));

    public void DiscoveryFailed(string failureType) => DiscoveryFailures.Add(failureType);
}
