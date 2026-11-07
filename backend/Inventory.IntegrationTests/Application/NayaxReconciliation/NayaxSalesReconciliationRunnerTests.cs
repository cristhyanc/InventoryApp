using Inventory.Application.NayaxReconciliation;
using Xunit;

namespace InventoryApi.Tests.Application.NayaxReconciliation;

/// <summary>
/// The hourly schedule of issue #1104: when it sweeps, in what order, one business at a time, and
/// what one business's trouble costs the others.
///
/// <para><strong>The waiting is a port, so none of these cases waits.</strong> The runner asks its
/// schedule to delay and a fake records the duration and returns, which is what makes "five minutes,
/// then every sixty" assertable at all - a test that actually waited an hour would simply not be a
/// test.</para>
/// </summary>
public class NayaxSalesReconciliationRunnerTests
{
    [Fact]
    public async Task The_first_sweep_is_five_minutes_after_startup_and_then_every_hour()
    {
        using var stopping = new CancellationTokenSource();
        var schedule = new FakeNayaxSalesReconciliationSchedule(stopping, delaysBeforeStopping: 4);
        var scopes = new FakeNayaxSalesReconciliationScopes([7], _ => Task.FromResult(Succeeded()));
        var log = new FakeNayaxSalesReconciliationLog();

        await new NayaxSalesReconciliationRunner(scopes, schedule, log).RunAsync(stopping.Token);

        Assert.Equal(
            [
                NayaxSalesReconciliationRunner.FirstSweepDelay,
                NayaxSalesReconciliationRunner.SweepInterval,
                NayaxSalesReconciliationRunner.SweepInterval,
                NayaxSalesReconciliationRunner.SweepInterval,
            ],
            schedule.Delays);

        Assert.Equal(TimeSpan.FromMinutes(5), NayaxSalesReconciliationRunner.FirstSweepDelay);
        Assert.Equal(TimeSpan.FromMinutes(60), NayaxSalesReconciliationRunner.SweepInterval);

        // Three sweeps ran: one after the first delay and one after each of the first two intervals.
        Assert.Equal(3, log.SweepSizes.Count);
        Assert.Equal(1, log.Started);
        Assert.Equal(1, log.Stopped);
    }

    /// <summary>
    /// Nothing happens before the first delay, so a deployment restart is not also a burst of Nayax
    /// reads while the API is serving the requests that were waiting for it.
    /// </summary>
    [Fact]
    public async Task No_sweep_runs_before_the_first_delay_has_passed()
    {
        using var stopping = new CancellationTokenSource();
        var schedule = new FakeNayaxSalesReconciliationSchedule(stopping, delaysBeforeStopping: 1);
        var scopes = new FakeNayaxSalesReconciliationScopes([7], _ => Task.FromResult(Succeeded()));
        var log = new FakeNayaxSalesReconciliationLog();

        await new NayaxSalesReconciliationRunner(scopes, schedule, log).RunAsync(stopping.Token);

        Assert.Equal([NayaxSalesReconciliationRunner.FirstSweepDelay], schedule.Delays);
        Assert.Empty(log.SweepSizes);
        Assert.Empty(scopes.ScopesOpened);
    }

    /// <summary>
    /// One business at a time, in the order the eligibility read answered, each in its own scope -
    /// and every scope closed afterwards, so no context outlives the business it was opened for.
    /// </summary>
    [Fact]
    public async Task Every_eligible_business_is_run_once_in_order_and_one_at_a_time()
    {
        var scopes = new FakeNayaxSalesReconciliationScopes(
            [3, 1, 2], _ => Task.FromResult(Succeeded()));

        var sweep = await Runner(scopes).SweepOnceAsync(CancellationToken.None);

        Assert.Equal([3, 1, 2], scopes.ScopesOpened);
        Assert.Equal(1, scopes.MaxConcurrentScopes);
        Assert.Equal(3, scopes.ScopesDisposed);
        Assert.Equal(0, scopes.OpenScopes);
        Assert.Equal(new NayaxSalesReconciliationSweep(3, 3, 0, 0, null), sweep);
    }

    /// <summary>
    /// <strong>One business's failure never stops the others.</strong> The use case reports a failure
    /// as an outcome, so the sweep counts it and carries on.
    /// </summary>
    [Fact]
    public async Task A_business_whose_run_failed_does_not_stop_the_others()
    {
        var scopes = new FakeNayaxSalesReconciliationScopes(
            [1, 2, 3],
            businessId => Task.FromResult(
                businessId == 2
                    ? new NayaxSalesReconciliationResult(
                        NayaxSalesReconciliationOutcome.Failed, 0, 0, 0, 0, 0, 0, 0, "HttpRequestException")
                    : Succeeded()));

        var log = new FakeNayaxSalesReconciliationLog();
        var sweep = await Runner(scopes, log).SweepOnceAsync(CancellationToken.None);

        Assert.Equal([1, 2, 3], scopes.ScopesOpened);
        Assert.Equal(new NayaxSalesReconciliationSweep(3, 2, 0, 1, null), sweep);

        // Every business got its own line, including the one that failed.
        Assert.Equal([1, 2, 3], log.Reconciled.Select(entry => entry.BusinessId).ToArray());
    }

    /// <summary>
    /// And neither does one whose run threw something the use case did not turn into an outcome -
    /// which it should not, and which is caught here anyway because the alternative is one business
    /// ending the whole sweep.
    /// </summary>
    [Fact]
    public async Task A_business_whose_run_threw_does_not_stop_the_others()
    {
        var scopes = new FakeNayaxSalesReconciliationScopes(
            [1, 2, 3],
            businessId => businessId == 2
                ? throw new InvalidOperationException("boom")
                : Task.FromResult(Succeeded()));

        var log = new FakeNayaxSalesReconciliationLog();
        var sweep = await Runner(scopes, log).SweepOnceAsync(CancellationToken.None);

        Assert.Equal([1, 2, 3], scopes.ScopesOpened);
        Assert.Equal(new NayaxSalesReconciliationSweep(3, 2, 0, 1, null), sweep);

        var failed = Assert.Single(log.Failed);
        Assert.Equal(2, failed.BusinessId);
        Assert.Equal(nameof(InvalidOperationException), failed.FailureType);

        // The scope is still closed, so a failing business leaks no context.
        Assert.Equal(0, scopes.OpenScopes);
        Assert.Equal(3, scopes.ScopesDisposed);
    }

    /// <summary>
    /// A skip - the lock was held, the allowance was spent, a gate refused it - is counted as a skip
    /// and not as a failure, because nothing went wrong.
    /// </summary>
    [Theory]
    [InlineData(NayaxSalesReconciliationOutcome.SkippedSyncRunning)]
    [InlineData(NayaxSalesReconciliationOutcome.SkippedRateLimited)]
    [InlineData(NayaxSalesReconciliationOutcome.SkippedGateRefused)]
    public async Task A_skipped_business_is_counted_as_skipped(NayaxSalesReconciliationOutcome outcome)
    {
        var scopes = new FakeNayaxSalesReconciliationScopes(
            [1], _ => Task.FromResult(NayaxSalesReconciliationResult.Skipped(outcome)));

        var sweep = await Runner(scopes).SweepOnceAsync(CancellationToken.None);

        Assert.Equal(new NayaxSalesReconciliationSweep(1, 0, 1, 0, null), sweep);
    }

    /// <summary>
    /// A sweep that could not find out who was eligible ran for nobody and changed nothing. The next
    /// hour is the retry; it does not guess a list.
    /// </summary>
    [Fact]
    public async Task A_failed_eligibility_read_runs_for_nobody()
    {
        var scopes = new FakeNayaxSalesReconciliationScopes(
            () => throw new InvalidOperationException("no database"),
            _ => Task.FromResult(Succeeded()));

        var log = new FakeNayaxSalesReconciliationLog();
        var sweep = await Runner(scopes, log).SweepOnceAsync(CancellationToken.None);

        Assert.Equal(
            new NayaxSalesReconciliationSweep(0, 0, 0, 0, nameof(InvalidOperationException)), sweep);
        Assert.Empty(scopes.ScopesOpened);
        Assert.Empty(log.SweepSizes);
        Assert.Equal([nameof(InvalidOperationException)], log.EligibilityFailures);
    }

    /// <summary>No eligible business is a sweep that makes no Nayax call and opens no scope.</summary>
    [Fact]
    public async Task No_eligible_business_sweeps_nobody()
    {
        var scopes = new FakeNayaxSalesReconciliationScopes([], _ => Task.FromResult(Succeeded()));
        var log = new FakeNayaxSalesReconciliationLog();

        var sweep = await Runner(scopes, log).SweepOnceAsync(CancellationToken.None);

        Assert.Equal(new NayaxSalesReconciliationSweep(0, 0, 0, 0, null), sweep);
        Assert.Empty(scopes.ScopesOpened);
        Assert.Equal([0], log.SweepSizes);
    }

    /// <summary>
    /// Shutdown stops the sweep rather than rushing it: the businesses not reached keep their last
    /// success instant, and the loop ends.
    /// </summary>
    [Fact]
    public async Task Cancellation_stops_the_sweep_rather_than_rushing_it()
    {
        using var stopping = new CancellationTokenSource();
        var scopes = new FakeNayaxSalesReconciliationScopes(
            [1, 2, 3],
            async businessId =>
            {
                if (businessId == 1)
                {
                    await stopping.CancelAsync();
                }

                return Succeeded();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Runner(scopes).SweepOnceAsync(stopping.Token));

        Assert.Equal([1], scopes.ScopesOpened);
        Assert.Equal(0, scopes.OpenScopes);
    }

    /// <summary>
    /// And the loop itself treats that cancellation as shutdown rather than as a failure: it logs its
    /// stop and returns.
    /// </summary>
    [Fact]
    public async Task Cancellation_ends_the_loop_without_raising()
    {
        using var stopping = new CancellationTokenSource();
        var log = new FakeNayaxSalesReconciliationLog();
        var scopes = new FakeNayaxSalesReconciliationScopes(
            [1],
            async _ =>
            {
                await stopping.CancelAsync();
                return Succeeded();
            });

        await new NayaxSalesReconciliationRunner(
                scopes, new FakeNayaxSalesReconciliationSchedule(), log)
            .RunAsync(stopping.Token);

        Assert.Equal(1, log.Started);
        Assert.Equal(1, log.Stopped);
    }

    /// <summary>
    /// The one log line per business the issue asks for carries the counts the result holds - and the
    /// result type carries counts and an outcome only, so a transaction id, an amount or a machine
    /// name cannot reach a log through it.
    /// </summary>
    [Fact]
    public async Task Each_businesss_line_carries_the_runs_counts_and_whether_it_succeeded()
    {
        var result = new NayaxSalesReconciliationResult(
            NayaxSalesReconciliationOutcome.Succeeded,
            ItemsRead: 42,
            SalesAdded: 3,
            Verified: 30,
            Conflicts: 1,
            NeedsInvestigation: 2,
            AbsenceRows: 2,
            EvidenceSkipped: 7,
            Failure: null);

        var scopes = new FakeNayaxSalesReconciliationScopes([9], _ => Task.FromResult(result));
        var log = new FakeNayaxSalesReconciliationLog();

        await Runner(scopes, log).SweepOnceAsync(CancellationToken.None);

        var line = Assert.Single(log.Reconciled);
        Assert.Equal(9, line.BusinessId);
        Assert.Equal(result, line.Result);
        Assert.True(line.Result.Succeeded);

        // Counts and an outcome only. A property that was not one of these would be something a log
        // line could publish about a business's sales.
        Assert.Equal(
            new[]
            {
                "AbsenceRows",
                "Conflicts",
                "EvidenceSkipped",
                "Failure",
                "ItemsRead",
                "NeedsInvestigation",
                "Outcome",
                "SalesAdded",
                "Succeeded",
                "Verified",
            },
            typeof(NayaxSalesReconciliationResult)
                .GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
    }

    private static NayaxSalesReconciliationRunner Runner(
        FakeNayaxSalesReconciliationScopes scopes, FakeNayaxSalesReconciliationLog? log = null) =>
        new(scopes, new FakeNayaxSalesReconciliationSchedule(), log ?? new FakeNayaxSalesReconciliationLog());

    private static NayaxSalesReconciliationResult Succeeded() =>
        new(NayaxSalesReconciliationOutcome.Succeeded, 1, 0, 1, 0, 0, 0, 0, null);
}
