using Inventory.Application.Nayax;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Nayax;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Nayax;

/// <summary>
/// Re-testing the stored credentials (issue #537), over fake ports.
///
/// The properties under test are the ones the endpoint exists for: the checks run with the
/// <em>stored</em> token whatever the status is - <c>NeedsAttention</c> included, which ordinary
/// calls refuse - the concluded status is written against the revision the run tested, a run Nayax
/// never answered writes nothing at all, and a token replaced mid-run keeps its own status.
/// </summary>
public class RetestNayaxConnectionTests
{
    // Obviously fake values. No real token or operator account belongs in a test.
    private const string OperatorId = "2002736764";
    private const string Token = "fake-token-not-a-real-credential";
    private const int TestBusinessId = 7;
    private static readonly DateTime Now = new(2026, 10, 12, 2, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Every stored status may be re-tested, including the two the gate refuses for ordinary calls:
    /// a connection in <c>NeedsAttention</c> is exactly the one an operator has to recover.
    /// </summary>
    [Theory]
    [InlineData(NayaxConnectionStatus.Ready)]
    [InlineData(NayaxConnectionStatus.PendingPermissions)]
    [InlineData(NayaxConnectionStatus.NeedsAttention)]
    public async Task Every_check_passing_concludes_ready_from_any_stored_status(NayaxConnectionStatus stored)
    {
        var fixture = new Fixture(stored, FakeNayaxConnectionCheckRunner.AllPassed());

        var outcome = await fixture.Retest.Handle();

        Assert.Equal(
            new NayaxConnectionStatusSummary(NayaxConnectionStatus.Ready, false),
            outcome.Result!.Summary);
        Assert.True(outcome.Result.StatusUpdated);
        Assert.Equal(NayaxConnectionStatus.Ready, fixture.Connections.Connection!.Status);
        Assert.Equal(Now, fixture.Connections.Connection.LastTestedAtUtc);
    }

    [Theory]
    [InlineData(NayaxConnectionStatus.Ready)]
    [InlineData(NayaxConnectionStatus.PendingPermissions)]
    [InlineData(NayaxConnectionStatus.NeedsAttention)]
    public async Task A_permission_failure_concludes_pending_permissions_from_any_stored_status(
        NayaxConnectionStatus stored)
    {
        var fixture = new Fixture(stored, Report(
            NayaxConnectionCheckLine.Passed(NayaxConnectionCheck.SignIn),
            NayaxConnectionCheckLine.Failed(
                NayaxConnectionCheck.MachineProducts,
                NayaxConnectionCheckFailure.PermissionNotGranted)));

        var outcome = await fixture.Retest.Handle();

        Assert.Equal(NayaxConnectionStatus.PendingPermissions, outcome.Result!.Summary.Status);
        Assert.True(outcome.Result.StatusUpdated);
        Assert.Equal(NayaxConnectionStatus.PendingPermissions, fixture.Connections.Connection!.Status);
    }

    /// <summary>
    /// Nayax documents a <c>401</c> as an invalid or expired token, so it is the one answer that
    /// moves a working connection to <c>NeedsAttention</c> - and the one an operator must act on.
    /// </summary>
    [Theory]
    [InlineData(NayaxConnectionStatus.Ready)]
    [InlineData(NayaxConnectionStatus.PendingPermissions)]
    [InlineData(NayaxConnectionStatus.NeedsAttention)]
    public async Task A_401_on_sign_in_concludes_needs_attention_from_any_stored_status(
        NayaxConnectionStatus stored)
    {
        var fixture = new Fixture(stored, Report(
            NayaxConnectionCheckLine.Failed(
                NayaxConnectionCheck.SignIn,
                NayaxConnectionCheckFailure.InvalidOrExpiredToken)));

        var outcome = await fixture.Retest.Handle();

        Assert.Equal(NayaxConnectionStatus.NeedsAttention, outcome.Result!.Summary.Status);
        Assert.True(outcome.Result.StatusUpdated);
        Assert.Equal(NayaxConnectionStatus.NeedsAttention, fixture.Connections.Connection!.Status);
    }

    /// <summary>
    /// Nothing was learned about the credentials, so nothing is concluded: the status, its
    /// last-tested instant and its machine-check state are all left exactly as they were, and the
    /// caller is told the run did not update anything.
    /// </summary>
    [Theory]
    [InlineData(NayaxConnectionStatus.Ready)]
    [InlineData(NayaxConnectionStatus.PendingPermissions)]
    [InlineData(NayaxConnectionStatus.NeedsAttention)]
    public async Task An_unavailable_nayax_leaves_every_stored_status_untouched(NayaxConnectionStatus stored)
    {
        var fixture = new Fixture(stored, Report(
            NayaxConnectionCheckLine.Failed(
                NayaxConnectionCheck.SignIn,
                NayaxConnectionCheckFailure.NayaxUnavailable)));
        var before = fixture.Connections.Connection;

        var outcome = await fixture.Retest.Handle();

        Assert.False(outcome.Result!.StatusUpdated);
        Assert.Equal(stored, outcome.Result.Summary.Status);
        Assert.Equal(before, fixture.Connections.Connection);
        Assert.Empty(fixture.Connections.AppliedResults);
        Assert.Empty(fixture.Connections.DiscardedResults);

        // The lines still report why, so the wizard can say "Nayax is unavailable, try again".
        Assert.Equal(
            NayaxConnectionCheckFailure.NayaxUnavailable,
            outcome.Result.Report.Lines.Single(line => line.Check == NayaxConnectionCheck.SignIn).Failure);
    }

    /// <summary>
    /// The checks are run with the stored credential, at the revision it was read at: that revision
    /// is what the result is written against.
    /// </summary>
    [Fact]
    public async Task The_checks_run_with_the_stored_token_and_its_revision()
    {
        var fixture = new Fixture(
            NayaxConnectionStatus.Ready,
            FakeNayaxConnectionCheckRunner.AllPassed(),
            credentialRevision: 4);

        await fixture.Retest.Handle();

        var candidate = Assert.Single(fixture.Runner.Candidates);
        Assert.Equal(OperatorId, candidate.OperatorId);
        Assert.Equal(Token, candidate.AccessToken);
        Assert.Equal(4, candidate.CredentialRevision);
        Assert.Equal(4, Assert.Single(fixture.Connections.AppliedResults).CredentialRevision);
    }

    /// <summary>
    /// A business with no machines yet records that the machine checks are still pending, and a
    /// later re-test that reaches them clears it.
    /// </summary>
    [Fact]
    public async Task A_re_test_records_and_then_clears_the_pending_machine_checks()
    {
        var withoutMachines = new Fixture(
            NayaxConnectionStatus.PendingPermissions,
            Report(
                NayaxConnectionCheckLine.Passed(NayaxConnectionCheck.SignIn),
                NayaxConnectionCheckLine.Passed(NayaxConnectionCheck.Machines),
                NayaxConnectionCheckLine.NotChecked(NayaxConnectionCheck.Machine),
                NayaxConnectionCheckLine.NotChecked(NayaxConnectionCheck.MachineProducts),
                NayaxConnectionCheckLine.NotChecked(NayaxConnectionCheck.MachineLastSales),
                NayaxConnectionCheckLine.NotChecked(NayaxConnectionCheck.MachineLastAlerts)));

        var pending = await withoutMachines.Retest.Handle();

        Assert.Equal(
            new NayaxConnectionStatusSummary(NayaxConnectionStatus.Ready, true),
            pending.Result!.Summary);
        Assert.True(withoutMachines.Connections.Connection!.MachineChecksPending);

        var withMachines = new Fixture(
            NayaxConnectionStatus.Ready,
            FakeNayaxConnectionCheckRunner.AllPassed(),
            machineChecksPending: true);

        var completed = await withMachines.Retest.Handle();

        Assert.False(completed.Result!.Summary.MachineChecksPending);
        Assert.False(withMachines.Connections.Connection!.MachineChecksPending);
    }

    /// <summary>
    /// The contract correction of #506: an Owner who saves a replacement token while a re-test is in
    /// flight keeps the replacement's own status. The older run's answer describes credentials that
    /// no longer exist, so it is discarded and the caller is told to run the test again.
    /// </summary>
    [Fact]
    public async Task A_result_for_replaced_credentials_is_discarded_and_the_replacement_keeps_its_status()
    {
        var clock = new FakeClock(Now);
        var connections = new FakeNayaxConnectionStore(
            OperatorId, Token, NayaxConnectionStatus.Ready, credentialRevision: 1);

        // The replacement is saved while the checks are running, exactly as a second request would.
        var runner = new InterleavingRunner(
            Report(
                NayaxConnectionCheckLine.Failed(
                    NayaxConnectionCheck.SignIn,
                    NayaxConnectionCheckFailure.InvalidOrExpiredToken)),
            async () =>
            {
                await connections.SaveCredentialAsync(
                    OperatorId, "the-replacement-token", CancellationToken.None);
                await connections.TryApplyStatusResultAsync(
                    new NayaxConnectionStatusResult(2, NayaxConnectionStatus.Ready, Now)
                    {
                        MachineChecksPending = false,
                    },
                    CancellationToken.None);
            });

        var retest = new RetestNayaxConnection(
            new NayaxConnectionRetestCredentialProvider(connections),
            runner,
            connections,
            new NayaxConnectionTestRateLimiter(clock),
            new FakeCurrentBusinessProvider(TestBusinessId),
            clock);

        var exception = await Assert.ThrowsAsync<DomainConflictException>(() => retest.Handle());

        Assert.Equal(RetestNayaxConnection.ConnectionChangedMessage, exception.Message);
        Assert.Equal(1, Assert.Single(connections.DiscardedResults).CredentialRevision);
        Assert.Equal(NayaxConnectionStatus.Ready, connections.Connection!.Status);
        Assert.Equal(2, connections.Connection.CredentialRevision);
    }

    /// <summary>
    /// The same rule for a run that concluded nothing (issue #607): an inconclusive report belongs
    /// to the revision it was produced for just as a concluded one does, so a replacement saved
    /// mid-run refuses it with the same sentence rather than returning the old credentials' report
    /// beside the new credentials' summary.
    ///
    /// The three transport outcomes the requirement names - a timeout, a <c>5xx</c> and an open
    /// circuit - are one cause at this layer, <see cref="NayaxConnectionCheckFailure.NayaxUnavailable"/>;
    /// which Nayax answer or transport failure produces it is
    /// <c>Infrastructure.Nayax.NayaxConnectionCheckRunnerTests</c>'. The other two causes that
    /// conclude nothing are included so the rule is not tied to one of them.
    /// </summary>
    [Theory]
    [InlineData(NayaxConnectionCheckFailure.NayaxUnavailable)]
    [InlineData(NayaxConnectionCheckFailure.UnexpectedResponse)]
    [InlineData(NayaxConnectionCheckFailure.PermissionNotGranted)]
    public async Task An_inconclusive_result_for_replaced_credentials_is_refused_and_writes_nothing(
        NayaxConnectionCheckFailure failure)
    {
        var clock = new FakeClock(Now);
        var connections = new FakeNayaxConnectionStore(
            OperatorId, Token, NayaxConnectionStatus.Ready, credentialRevision: 1);

        var runner = new InterleavingRunner(
            Report(NayaxConnectionCheckLine.Failed(NayaxConnectionCheck.SignIn, failure)),
            async () =>
            {
                await connections.SaveCredentialAsync(
                    OperatorId, "the-replacement-token", CancellationToken.None);
                await connections.TryApplyStatusResultAsync(
                    new NayaxConnectionStatusResult(2, NayaxConnectionStatus.Ready, Now)
                    {
                        MachineChecksPending = false,
                    },
                    CancellationToken.None);
            });

        var retest = Retest(connections, runner, clock);
        var exception = await Assert.ThrowsAsync<DomainConflictException>(() => retest.Handle());

        Assert.Equal(RetestNayaxConnection.ConnectionChangedMessage, exception.Message);

        // The replacement's own write is the only one, and no stale result was even attempted: an
        // inconclusive run has nothing to write, so there is no conditional update to discard.
        Assert.Equal(2, Assert.Single(connections.AppliedResults).CredentialRevision);
        Assert.Empty(connections.DiscardedResults);
        Assert.Equal(NayaxConnectionStatus.Ready, connections.Connection!.Status);
        Assert.Equal(2, connections.Connection.CredentialRevision);
    }

    /// <summary>
    /// A connection that is gone by the time an inconclusive run finishes is the replaced one's twin
    /// (issue #607): neither is the revision this run tested, so both get the same conflict the
    /// concluded path gives - never a <c>NotConfigured</c> summary beside a completed report.
    /// </summary>
    [Fact]
    public async Task An_inconclusive_result_for_a_connection_that_is_gone_is_refused_the_same_way()
    {
        var clock = new FakeClock(Now);
        var connections = new FakeNayaxConnectionStore(
            OperatorId, Token, NayaxConnectionStatus.Ready, credentialRevision: 1);

        var runner = new InterleavingRunner(
            Report(NayaxConnectionCheckLine.Failed(
                NayaxConnectionCheck.SignIn,
                NayaxConnectionCheckFailure.NayaxUnavailable)),
            () =>
            {
                connections.Remove();
                return Task.CompletedTask;
            });

        var exception = await Assert.ThrowsAsync<DomainConflictException>(
            () => Retest(connections, runner, clock).Handle());

        Assert.Equal(RetestNayaxConnection.ConnectionChangedMessage, exception.Message);
        Assert.Empty(connections.AppliedResults);
        Assert.Empty(connections.DiscardedResults);
    }

    /// <summary>
    /// The revision comparison and the summary come from one read of the row (issue #607), so the
    /// report an unchanged revision accepts can never be paired with a state some later read saw.
    /// </summary>
    [Fact]
    public async Task An_unchanged_revision_compares_and_reports_from_one_connection_read()
    {
        var fixture = new Fixture(
            NayaxConnectionStatus.Ready,
            Report(NayaxConnectionCheckLine.Failed(
                NayaxConnectionCheck.SignIn,
                NayaxConnectionCheckFailure.NayaxUnavailable)));

        var outcome = await fixture.Retest.Handle();

        Assert.False(outcome.Result!.StatusUpdated);
        Assert.Equal(NayaxConnectionStatus.Ready, outcome.Result.Summary.Status);
        Assert.Equal(1, fixture.Connections.ConnectionReads);
    }

    /// <summary>
    /// There is nothing to re-test, and the answer is the stable "Nayax is not connected" failure
    /// rather than an invented one. No allowance is spent on it.
    /// </summary>
    [Fact]
    public async Task A_business_with_no_stored_credentials_has_nothing_to_re_test()
    {
        var clock = new FakeClock(Now);
        var connections = new FakeNayaxConnectionStore();
        var runner = new FakeNayaxConnectionCheckRunner();
        var limiter = new NayaxConnectionTestRateLimiter(clock);
        var retest = new RetestNayaxConnection(
            new NayaxConnectionRetestCredentialProvider(connections),
            runner,
            connections,
            limiter,
            new FakeCurrentBusinessProvider(TestBusinessId),
            clock);

        var exception = await Assert.ThrowsAsync<NayaxNotConnectedException>(() => retest.Handle());

        Assert.Equal(NayaxConnectionStatus.NotConfigured, exception.Status);
        Assert.Empty(runner.Candidates);
        Assert.True(limiter.TryClaim(BusinessId.From(TestBusinessId), out _));
    }

    /// <summary>
    /// Test and re-test share one per-business allowance, because a re-test is the same eight calls
    /// against the same upstream. The refused run makes no Nayax call at all.
    /// </summary>
    [Fact]
    public async Task A_re_test_spends_the_same_allowance_a_test_does()
    {
        var clock = new FakeClock(Now);
        var limiter = new NayaxConnectionTestRateLimiter(clock);
        var connections = new FakeNayaxConnectionStore(
            OperatorId, Token, NayaxConnectionStatus.Ready);
        var runner = new FakeNayaxConnectionCheckRunner();
        var retest = new RetestNayaxConnection(
            new NayaxConnectionRetestCredentialProvider(connections),
            runner,
            connections,
            limiter,
            new FakeCurrentBusinessProvider(TestBusinessId),
            clock);
        var test = new TestNayaxConnection(
            runner,
            new FakeNayaxConnectionTestDraftStore(clock),
            limiter,
            new FakeCurrentBusinessProvider(TestBusinessId),
            FakeAuthenticatedActorAccessor.Identified(Actor()),
            clock);

        // Three tests and two re-tests exhaust the window of five.
        for (var run = 0; run < 3; run++)
        {
            Assert.NotNull((await test.Handle(OperatorId, Token)).Result);
        }

        Assert.NotNull((await retest.Handle()).Result);
        Assert.NotNull((await retest.Handle()).Result);

        var runsSoFar = runner.Candidates.Count;
        var refusedRetest = await retest.Handle();
        var refusedTest = await test.Handle(OperatorId, Token);

        Assert.Null(refusedRetest.Result);
        Assert.True(refusedRetest.RetryAfter > TimeSpan.Zero);
        Assert.Null(refusedTest.Result);
        Assert.Equal(runsSoFar, runner.Candidates.Count);
    }

    [Fact]
    public async Task A_caller_with_no_resolvable_business_re_tests_nothing()
    {
        var clock = new FakeClock(Now);
        var connections = new FakeNayaxConnectionStore(
            OperatorId, Token, NayaxConnectionStatus.Ready);
        var runner = new FakeNayaxConnectionCheckRunner();
        var retest = new RetestNayaxConnection(
            new NayaxConnectionRetestCredentialProvider(connections),
            runner,
            connections,
            new NayaxConnectionTestRateLimiter(clock),
            FakeCurrentBusinessProvider.Denied(),
            clock);

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(() => retest.Handle());

        Assert.Empty(runner.Candidates);
        Assert.Empty(connections.AppliedResults);
    }

    private static NayaxConnectionCheckReport Report(params NayaxConnectionCheckLine[] lines) =>
        new(lines, MachineCount: null);

    /// <summary>The use case over one store and one runner, for the interleaved-replacement tests.</summary>
    private static RetestNayaxConnection Retest(
        FakeNayaxConnectionStore connections,
        INayaxConnectionCheckRunner runner,
        FakeClock clock) =>
        new(
            new NayaxConnectionRetestCredentialProvider(connections),
            runner,
            connections,
            new NayaxConnectionTestRateLimiter(clock),
            new FakeCurrentBusinessProvider(TestBusinessId),
            clock);

    private static ActorIdentity Actor()
    {
        Assert.True(ActorIdentity.TryCreate(
            "99999999-9999-9999-9999-999999999999",
            "11111111-1111-1111-1111-111111111111",
            out var actor));
        return actor!;
    }

    /// <summary>
    /// A runner that performs one action in the middle of its run, so "the credentials were replaced
    /// while the checks were running" can be tested deterministically rather than with timing.
    /// </summary>
    private sealed class InterleavingRunner : INayaxConnectionCheckRunner
    {
        private readonly NayaxConnectionCheckReport _report;
        private readonly Func<Task> _duringRun;

        public InterleavingRunner(NayaxConnectionCheckReport report, Func<Task> duringRun)
        {
            _report = report;
            _duringRun = duringRun;
        }

        public async Task<NayaxConnectionCheckReport> RunAsync(
            NayaxConnectionCredential candidate,
            CancellationToken cancellationToken)
        {
            await _duringRun();
            return _report;
        }
    }

    /// <summary>The use case over fake ports, with one stored connection to re-test.</summary>
    private sealed class Fixture
    {
        public Fixture(
            NayaxConnectionStatus stored,
            NayaxConnectionCheckReport report,
            int credentialRevision = 1,
            bool machineChecksPending = false)
        {
            Clock = new FakeClock(Now);
            Connections = new FakeNayaxConnectionStore(
                OperatorId, Token, stored, credentialRevision, machineChecksPending);
            Runner = new FakeNayaxConnectionCheckRunner(report);
            Retest = new RetestNayaxConnection(
                new NayaxConnectionRetestCredentialProvider(Connections),
                Runner,
                Connections,
                new NayaxConnectionTestRateLimiter(Clock),
                new FakeCurrentBusinessProvider(TestBusinessId),
                Clock);
        }

        public FakeClock Clock { get; }

        public FakeNayaxConnectionStore Connections { get; }

        public FakeNayaxConnectionCheckRunner Runner { get; }

        public RetestNayaxConnection Retest { get; }
    }
}
