using Inventory.Application.Imports;
using Xunit;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// How the sales history import worker of issue #1304 picks work up: the oldest unfinished job, one
/// at a time, each in its own business's scope, and one job's trouble never stopping another's.
///
/// <para>It is <see cref="ReimbursementImportJobRunnerTests"/>'s shape, for the same reasons: the
/// waiting is a port, so none of these cases waits, and the loop is driven one pass at a time rather
/// than through a hosted service's start and stop.</para>
/// </summary>
public class NayaxSalesHistoryImportJobRunnerTests
{
    [Fact]
    public async Task The_first_pass_is_after_startup_and_then_every_fifteen_seconds_when_idle()
    {
        using var stopping = new CancellationTokenSource();
        var schedule = new FakeSalesHistorySchedule(stopping, delaysBeforeStopping: 3);
        var scopes = new FakeSalesHistoryScopes();
        var log = new FakeSalesHistoryLog();

        await new NayaxSalesHistoryImportJobRunner(scopes, schedule, log).RunAsync(stopping.Token);

        Assert.Equal(
            [
                NayaxSalesHistoryImportJobRunner.FirstPassDelay,
                NayaxSalesHistoryImportJobRunner.IdleDelay,
                NayaxSalesHistoryImportJobRunner.IdleDelay,
            ],
            schedule.Delays);

        // Deliberately a little after the reimbursement worker's own first pass, so a restart is not
        // two imports competing for the one write lock.
        Assert.Equal(TimeSpan.FromSeconds(45), NayaxSalesHistoryImportJobRunner.FirstPassDelay);
        Assert.True(
            NayaxSalesHistoryImportJobRunner.FirstPassDelay > ReimbursementImportJobRunner.FirstPassDelay);
        Assert.Equal(TimeSpan.FromSeconds(15), NayaxSalesHistoryImportJobRunner.IdleDelay);
        Assert.Equal(1, log.Started);
        Assert.Equal(1, log.Stopped);
    }

    [Fact]
    public async Task No_work_is_looked_for_before_the_first_delay()
    {
        using var stopping = new CancellationTokenSource();
        var schedule = new FakeSalesHistorySchedule(stopping, delaysBeforeStopping: 1);
        var scopes = new FakeSalesHistoryScopes();

        await new NayaxSalesHistoryImportJobRunner(scopes, schedule, new FakeSalesHistoryLog())
            .RunAsync(stopping.Token);

        Assert.Equal([NayaxSalesHistoryImportJobRunner.FirstPassDelay], schedule.Delays);
        Assert.Equal(0, scopes.DiscoveryCalls);
    }

    /// <summary>
    /// Each job runs in a scope opened for its own business, one at a time, and every scope is
    /// closed afterwards - so no context outlives the business it was opened for, and two histories
    /// never import at once.
    /// </summary>
    [Fact]
    public async Task Each_job_runs_in_its_own_business_scope_one_at_a_time()
    {
        var scopes = new FakeSalesHistoryScopes();
        scopes.Enqueue(new DueNayaxSalesHistoryImportJob(10, 7));
        scopes.Enqueue(new DueNayaxSalesHistoryImportJob(11, 9));

        var runner = Runner(scopes);

        Assert.True(await runner.ProcessNextAsync(CancellationToken.None));
        Assert.True(await runner.ProcessNextAsync(CancellationToken.None));
        Assert.False(await runner.ProcessNextAsync(CancellationToken.None));

        Assert.Equal([7, 9], scopes.ScopesOpened);
        Assert.Equal([(7, 10), (9, 11)], scopes.Processed);
        Assert.Equal(1, scopes.MaxConcurrentScopes);
        Assert.Equal(2, scopes.ScopesDisposed);
        Assert.Equal(0, scopes.OpenScopes);
    }

    [Fact]
    public async Task One_jobs_failure_never_stops_another()
    {
        var scopes = new FakeSalesHistoryScopes();
        scopes.Enqueue(new DueNayaxSalesHistoryImportJob(10, 7));
        scopes.Enqueue(new DueNayaxSalesHistoryImportJob(11, 9));
        scopes.ThrowFor.Add(10);
        var log = new FakeSalesHistoryLog();

        var runner = new NayaxSalesHistoryImportJobRunner(scopes, new ImmediateSalesHistorySchedule(), log);

        await runner.ProcessNextAsync(CancellationToken.None);
        await runner.ProcessNextAsync(CancellationToken.None);

        Assert.Equal([(9, 11)], scopes.Processed);
        Assert.Equal([(7, 10, nameof(InvalidOperationException))], log.Failures);
        Assert.Equal(2, scopes.ScopesDisposed);
    }

    [Fact]
    public async Task A_failed_discovery_read_processes_nothing()
    {
        var scopes = new FakeSalesHistoryScopes { DiscoveryThrows = true };
        var log = new FakeSalesHistoryLog();

        var didWork = await new NayaxSalesHistoryImportJobRunner(
                scopes, new ImmediateSalesHistorySchedule(), log)
            .ProcessNextAsync(CancellationToken.None);

        Assert.False(didWork);
        Assert.Empty(scopes.Processed);
        Assert.Equal([nameof(InvalidOperationException)], log.DiscoveryFailures);
    }

    [Fact]
    public async Task Nothing_waiting_opens_no_scope()
    {
        var scopes = new FakeSalesHistoryScopes();

        Assert.False(await Runner(scopes).ProcessNextAsync(CancellationToken.None));
        Assert.Empty(scopes.ScopesOpened);
    }

    private static NayaxSalesHistoryImportJobRunner Runner(FakeSalesHistoryScopes scopes) =>
        new(scopes, new ImmediateSalesHistorySchedule(), new FakeSalesHistoryLog());
}

/// <summary>A schedule that returns at once, for the cases that drive passes by hand.</summary>
internal sealed class ImmediateSalesHistorySchedule : INayaxSalesHistoryImportJobSchedule
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// A schedule that records what it was asked to wait and stops the loop after a fixed number of
/// delays, so the runner's cadence is assertable without any test actually waiting.
/// </summary>
internal sealed class FakeSalesHistorySchedule : INayaxSalesHistoryImportJobSchedule
{
    private readonly CancellationTokenSource _stopping;
    private readonly int _delaysBeforeStopping;

    public FakeSalesHistorySchedule(CancellationTokenSource stopping, int delaysBeforeStopping)
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
/// The worker's two scopes, in memory: a queue of waiting jobs, and a per-business scope that
/// records that it was opened, used and closed.
/// </summary>
internal sealed class FakeSalesHistoryScopes : INayaxSalesHistoryImportJobScopes
{
    private readonly Queue<DueNayaxSalesHistoryImportJob> _due = new();

    public List<int> ScopesOpened { get; } = [];

    public List<(int BusinessId, int JobId)> Processed { get; } = [];

    public HashSet<int> ThrowFor { get; } = [];

    public bool DiscoveryThrows { get; set; }

    public int DiscoveryCalls { get; private set; }

    public int OpenScopes { get; private set; }

    public int MaxConcurrentScopes { get; private set; }

    public int ScopesDisposed { get; private set; }

    public void Enqueue(DueNayaxSalesHistoryImportJob job) => _due.Enqueue(job);

    public Task<DueNayaxSalesHistoryImportJob?> FindNextDueJobAsync(CancellationToken cancellationToken)
    {
        DiscoveryCalls++;

        if (DiscoveryThrows) throw new InvalidOperationException("the database is unavailable");

        return Task.FromResult(
            _due.Count == 0 ? null : (DueNayaxSalesHistoryImportJob?)_due.Dequeue());
    }

    public INayaxSalesHistoryImportJobScope CreateForBusiness(int businessId)
    {
        ScopesOpened.Add(businessId);
        OpenScopes++;
        MaxConcurrentScopes = Math.Max(MaxConcurrentScopes, OpenScopes);

        return new Scope(this, businessId);
    }

    private sealed class Scope : INayaxSalesHistoryImportJobScope
    {
        private readonly FakeSalesHistoryScopes _owner;
        private readonly int _businessId;

        public Scope(FakeSalesHistoryScopes owner, int businessId)
        {
            _owner = owner;
            _businessId = businessId;
        }

        public Task<NayaxSalesHistoryImportJobRunResult> ProcessAsync(
            int jobId, CancellationToken cancellationToken)
        {
            if (_owner.ThrowFor.Contains(jobId))
                throw new InvalidOperationException("the scope could not do the job");

            _owner.Processed.Add((_businessId, jobId));

            return Task.FromResult(new NayaxSalesHistoryImportJobRunResult(
                NayaxSalesHistoryImportJobRunOutcome.Finished,
                new NayaxSalesHistoryImportProgress(500, 500, 0, 0, 0, 0),
                500,
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
internal sealed class FakeSalesHistoryLog : INayaxSalesHistoryImportJobLog
{
    public int Started { get; private set; }

    public int Stopped { get; private set; }

    public List<(int BusinessId, int JobId, NayaxSalesHistoryImportJobRunOutcome Outcome)> Processed { get; } = [];

    public List<(int BusinessId, int JobId, string FailureType)> Failures { get; } = [];

    public List<string> DiscoveryFailures { get; } = [];

    public void WorkerStarted() => Started++;

    public void WorkerStopped() => Stopped++;

    public void JobProcessed(int businessId, int jobId, NayaxSalesHistoryImportJobRunResult result) =>
        Processed.Add((businessId, jobId, result.Outcome));

    public void JobFailed(int businessId, int jobId, string failureType) =>
        Failures.Add((businessId, jobId, failureType));

    public void DiscoveryFailed(string failureType) => DiscoveryFailures.Add(failureType);
}
