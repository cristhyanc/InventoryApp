using Inventory.Application.PlatformDiagnostics;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.PlatformDiagnostics;

/// <summary>
/// The use case's two jobs (issue #336): refuse a badly shaped submission before anything prepares
/// it, and audit every outcome exactly once.
///
/// The audit assertions are the point. An unaudited cross-business read is the failure this
/// endpoint exists to prevent, so "a refusal is audited too" and "the event carries the actor and
/// the fingerprint and no statement" are behavioural requirements, not logging details.
/// </summary>
public class RunDiagnosticsQueryTests
{
    private static readonly ActorIdentity PlatformAdmin = Actor(
        "aaaaaaaa-0000-0000-0000-000000000001",
        "bbbbbbbb-0000-0000-0000-000000000002");

    private static readonly DateTime QueriedAt = new(2026, 10, 6, 1, 2, 3, DateTimeKind.Utc);

    [Fact]
    public async Task A_successful_read_is_returned_and_audited_with_the_actor_and_the_fingerprint()
    {
        var executor = FakeExecutor.Returning(new DiagnosticsQueryExecution(
            DiagnosticsQueryOutcome.Succeeded,
            DiagnosticsQueryDenialReason.None,
            null,
            ["Id"],
            [["1"], ["2"]],
            null));
        var audit = new RecordingAudit();
        var useCase = Build(executor, audit);

        var result = await useCase.Handle("SELECT Id FROM Products WHERE BusinessId = 3", CancellationToken.None);

        Assert.Equal(DiagnosticsQueryOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, result.RowCount);
        Assert.False(result.Truncated);
        Assert.True(DiagnosticsQueryResult.CrossBusinessScope);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(PlatformAdmin.DirectoryTenantId, entry.ActorDirectoryTenantId);
        Assert.Equal(PlatformAdmin.ObjectId, entry.ActorObjectId);
        Assert.Equal(QueriedAt, entry.OccurredAtUtc);
        Assert.Equal(result.QueryFingerprint, entry.QueryFingerprint);
        Assert.Equal(DiagnosticsQueryOutcome.Succeeded, entry.Outcome);
        Assert.Equal(2, entry.RowCount);
        Assert.True(entry.CrossBusinessScope);
    }

    /// <summary>
    /// The audit event must be able to say what was asked without saying it. The fingerprint is
    /// the only description of the query on the event, and it is a hash.
    /// </summary>
    [Fact]
    public async Task The_audit_event_carries_a_fingerprint_and_no_part_of_the_statement()
    {
        const string sql = "SELECT Id FROM Products WHERE BusinessId = 7 AND Id = 42";
        var audit = new RecordingAudit();

        await Build(FakeExecutor.Succeeding(), audit).Handle(sql, CancellationToken.None);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(DiagnosticsSqlShape.Fingerprint(sql), entry.QueryFingerprint);
        Assert.DoesNotContain("Products", entry.QueryFingerprint, StringComparison.OrdinalIgnoreCase);

        // The shape the fingerprint is taken over is what must carry no value the caller typed; a
        // hex digest of it would contain "42" by coincidence, so assert on the shape itself.
        Assert.DoesNotContain("42", DiagnosticsSqlShape.NormalizeForFingerprint(sql), StringComparison.Ordinal);
        Assert.DoesNotContain("7", DiagnosticsSqlShape.NormalizeForFingerprint(sql), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_oversized_statement_is_refused_before_the_executor_is_asked_to_prepare_it()
    {
        var executor = FakeExecutor.Succeeding();
        var audit = new RecordingAudit();
        var oversized = "SELECT Id FROM Products WHERE Id = 1 AND '"
            + new string('x', PlatformDiagnosticsQueryLimits.MaxSqlBytes)
            + "' = ''";

        var result = await Build(executor, audit).Handle(oversized, CancellationToken.None);

        Assert.Equal(DiagnosticsQueryOutcome.Rejected, result.Outcome);
        Assert.Equal(DiagnosticsQueryDenialReason.SqlTooLarge, result.DenialReason);
        Assert.Equal(0, executor.CallCount);
        Assert.Empty(result.Rows);
        Assert.Single(audit.Entries);
    }

    [Theory]
    [InlineData("DELETE FROM Products", DiagnosticsQueryDenialReason.NotAReadOnlyStatement)]
    [InlineData("SELECT Id FROM Products; DROP TABLE Products", DiagnosticsQueryDenialReason.MultipleStatements)]
    [InlineData("", DiagnosticsQueryDenialReason.SqlMissing)]
    public async Task A_refused_submission_never_reaches_the_executor_and_is_still_audited(
        string sql,
        DiagnosticsQueryDenialReason expected)
    {
        var executor = FakeExecutor.Succeeding();
        var audit = new RecordingAudit();

        var result = await Build(executor, audit).Handle(sql, CancellationToken.None);

        Assert.Equal(DiagnosticsQueryOutcome.Rejected, result.Outcome);
        Assert.Equal(expected, result.DenialReason);
        Assert.Equal(0, executor.CallCount);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(DiagnosticsQueryOutcome.Rejected, entry.Outcome);
        Assert.Equal(expected, entry.DenialReason);
        Assert.Equal(0, entry.RowCount);
    }

    [Fact]
    public async Task A_truncated_read_reports_truncation_rather_than_presenting_a_prefix_as_complete()
    {
        var executor = FakeExecutor.Returning(new DiagnosticsQueryExecution(
            DiagnosticsQueryOutcome.Truncated,
            DiagnosticsQueryDenialReason.None,
            "cut short",
            ["Id"],
            [["1"]],
            DiagnosticsTruncationReason.RowLimit));
        var audit = new RecordingAudit();

        var result = await Build(executor, audit).Handle("SELECT Id FROM Products", CancellationToken.None);

        Assert.Equal(DiagnosticsQueryOutcome.Truncated, result.Outcome);
        Assert.True(result.Truncated);
        Assert.Equal(DiagnosticsTruncationReason.RowLimit, result.TruncationReason);
        Assert.Equal(DiagnosticsTruncationReason.RowLimit, Assert.Single(audit.Entries).TruncationReason);
    }

    [Theory]
    [InlineData(DiagnosticsQueryOutcome.TimedOut)]
    [InlineData(DiagnosticsQueryOutcome.Failed)]
    [InlineData(DiagnosticsQueryOutcome.Cancelled)]
    public async Task Every_unsuccessful_outcome_the_adapter_reports_is_audited(DiagnosticsQueryOutcome outcome)
    {
        var executor = FakeExecutor.Returning(new DiagnosticsQueryExecution(
            outcome,
            DiagnosticsQueryDenialReason.None,
            "stopped",
            [],
            [],
            null));
        var audit = new RecordingAudit();

        var result = await Build(executor, audit).Handle("SELECT Id FROM Products", CancellationToken.None);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(outcome, Assert.Single(audit.Entries).Outcome);
    }

    /// <summary>
    /// A cancellation that escapes the adapter still happened, so it is audited on the way out
    /// rather than disappearing with the exception.
    /// </summary>
    [Fact]
    public async Task A_cancellation_that_escapes_the_adapter_is_audited_before_it_propagates()
    {
        var audit = new RecordingAudit();
        var executor = FakeExecutor.Throwing(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Build(executor, audit).Handle("SELECT Id FROM Products", CancellationToken.None));

        Assert.Equal(DiagnosticsQueryOutcome.Cancelled, Assert.Single(audit.Entries).Outcome);
    }

    /// <summary>
    /// The use case does not authorise, so it must not be the thing that notices an unidentifiable
    /// caller - the policy already refused one. What it must not do is skip the audit event.
    /// </summary>
    [Fact]
    public async Task An_unidentifiable_actor_still_produces_an_audit_event()
    {
        var audit = new RecordingAudit();
        var useCase = new RunDiagnosticsQuery(
            FakeExecutor.Succeeding(),
            audit,
            FakeAuthenticatedActorAccessor.Unidentifiable(),
            new FakeClock(QueriedAt));

        await useCase.Handle("SELECT Id FROM Products", CancellationToken.None);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(string.Empty, entry.ActorDirectoryTenantId);
        Assert.Equal(string.Empty, entry.ActorObjectId);
    }

    private static RunDiagnosticsQuery Build(FakeExecutor executor, RecordingAudit audit) =>
        new(
            executor,
            audit,
            FakeAuthenticatedActorAccessor.Identified(PlatformAdmin),
            new FakeClock(QueriedAt));

    private static ActorIdentity Actor(string directoryTenantId, string objectId)
    {
        Assert.True(ActorIdentity.TryCreate(directoryTenantId, objectId, out var actor));
        return actor!;
    }

    private sealed class FakeExecutor : IDiagnosticsQueryExecutor
    {
        private readonly DiagnosticsQueryExecution? _execution;
        private readonly Exception? _exception;

        private FakeExecutor(DiagnosticsQueryExecution? execution, Exception? exception)
        {
            _execution = execution;
            _exception = exception;
        }

        public int CallCount { get; private set; }

        public static FakeExecutor Returning(DiagnosticsQueryExecution execution) => new(execution, null);

        public static FakeExecutor Throwing(Exception exception) => new(null, exception);

        public static FakeExecutor Succeeding() => Returning(new DiagnosticsQueryExecution(
            DiagnosticsQueryOutcome.Succeeded,
            DiagnosticsQueryDenialReason.None,
            null,
            ["Id"],
            [["1"]],
            null));

        public Task<DiagnosticsQueryExecution> ExecuteAsync(string sql, CancellationToken cancellationToken)
        {
            CallCount++;

            if (_exception is not null)
            {
                throw _exception;
            }

            return Task.FromResult(_execution!);
        }
    }

    private sealed class RecordingAudit : IPlatformDiagnosticsAudit
    {
        private readonly List<PlatformDiagnosticsAuditEntry> _entries = [];

        public IReadOnlyList<PlatformDiagnosticsAuditEntry> Entries => _entries;

        public void Record(PlatformDiagnosticsAuditEntry entry) => _entries.Add(entry);
    }
}
