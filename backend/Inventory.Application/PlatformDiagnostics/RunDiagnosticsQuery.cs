using System.Diagnostics;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;

namespace Inventory.Application.PlatformDiagnostics;

/// <summary>
/// Runs one bounded, read-only, cross-business diagnostics query on behalf of the configured
/// platform administrator, and audits it (issue #336).
///
/// The use case owns the order the acceptance criteria require: an oversized or multi-statement
/// submission is refused <em>before</em> the executor is asked to prepare anything, and every
/// outcome - success, truncation, refusal, timeout, cancellation, failure - is audited exactly
/// once. The audit is in a <c>finally</c>-shaped path rather than on the success branch, because an
/// unaudited diagnostics read is the failure mode this endpoint exists to avoid.
///
/// It does not authorise. Authorisation is the platform-admin policy at the API boundary, checked
/// before the endpoint runs and re-checked by the business-scope middleware before membership is
/// bypassed; a use case that re-decided it would be a second answer to a question that must have
/// one.
/// </summary>
public sealed class RunDiagnosticsQuery
{
    private readonly IDiagnosticsQueryExecutor _executor;
    private readonly IPlatformDiagnosticsAudit _audit;
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly IClock _clock;

    public RunDiagnosticsQuery(
        IDiagnosticsQueryExecutor executor,
        IPlatformDiagnosticsAudit audit,
        IAuthenticatedActorAccessor actorAccessor,
        IClock clock)
    {
        _executor = executor;
        _audit = audit;
        _actorAccessor = actorAccessor;
        _clock = clock;
    }

    public async Task<DiagnosticsQueryResult> Handle(string? sql, CancellationToken cancellationToken)
    {
        var fingerprint = DiagnosticsSqlShape.Fingerprint(sql);
        var stopwatch = Stopwatch.StartNew();

        if (!DiagnosticsSqlShape.IsPermittedShape(sql, out var reason, out var message))
        {
            stopwatch.Stop();
            return Audited(DiagnosticsQueryExecution.Rejected(reason, message), stopwatch, fingerprint);
        }

        DiagnosticsQueryExecution execution;
        try
        {
            execution = await _executor.ExecuteAsync(sql!, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The adapter reports cancellation as a result, so reaching here means the token
            // tripped outside its own handling. It is still a diagnostics read that happened and
            // must still be audited before the cancellation propagates.
            stopwatch.Stop();
            Audited(
                Unreturned(DiagnosticsQueryOutcome.Cancelled, "The request was cancelled."),
                stopwatch,
                fingerprint);
            throw;
        }
        catch (Exception)
        {
            // Anything else the adapter throws instead of reporting - a provider error, a native
            // SQLite handle that could not be acquired, a failure while reading - is still a
            // diagnostics read that was attempted on a cross-business connection. The event is
            // recorded as a failure before the exception continues to the API's error handling,
            // because "one structured audit event per query" has to hold for the queries that go
            // wrong unexpectedly, not only for the ones the adapter had an answer for.
            stopwatch.Stop();
            Audited(
                Unreturned(DiagnosticsQueryOutcome.Failed, "The diagnostics query failed unexpectedly."),
                stopwatch,
                fingerprint);
            throw;
        }

        stopwatch.Stop();
        return Audited(execution, stopwatch, fingerprint);
    }

    /// <summary>
    /// The execution the adapter never got to report, built so the audit path is the same one every
    /// returned outcome takes. It carries no columns and no rows because nothing was read.
    /// </summary>
    private static DiagnosticsQueryExecution Unreturned(DiagnosticsQueryOutcome outcome, string message) =>
        new(outcome, DiagnosticsQueryDenialReason.None, message, [], [], null);

    private DiagnosticsQueryResult Audited(
        DiagnosticsQueryExecution execution,
        Stopwatch stopwatch,
        string fingerprint)
    {
        var duration = (long)stopwatch.Elapsed.TotalMilliseconds;
        var result = new DiagnosticsQueryResult(
            execution.Outcome,
            execution.DenialReason,
            execution.Message,
            execution.Columns,
            execution.Rows,
            execution.TruncationReason,
            duration,
            fingerprint);

        // A token whose (tid, oid) pair cannot be read identifies nobody, and the policy would
        // already have refused it; recording the blanks rather than skipping the event keeps
        // "a diagnostics request happened" true in the log even then.
        var actor = _actorAccessor.GetCurrentActor().Actor;

        _audit.Record(new PlatformDiagnosticsAuditEntry(
            actor?.DirectoryTenantId ?? string.Empty,
            actor?.ObjectId ?? string.Empty,
            _clock.UtcNow,
            fingerprint,
            result.Outcome,
            result.DenialReason,
            result.TruncationReason,
            duration,
            result.RowCount));

        return result;
    }
}
