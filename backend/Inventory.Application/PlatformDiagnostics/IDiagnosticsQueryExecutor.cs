namespace Inventory.Application.PlatformDiagnostics;

/// <summary>
/// The narrow port behind which a bounded, read-only diagnostics read happens (issue #336).
///
/// It takes SQL and a cancellation token and nothing else: no business identifier, no connection,
/// no limits and no "unrestricted" flag. The limits are the adapter's, injected once at
/// composition; the cross-business scope is the endpoint's documented contract rather than a
/// parameter; and there is no overload that could ask for a wider surface.
///
/// An implementation never throws for a refused or interrupted query. Refusal, timeout and
/// cancellation are results, because the use case has to audit all three the same way it audits a
/// success - an exception that escaped here would be an unaudited diagnostics read.
/// </summary>
public interface IDiagnosticsQueryExecutor
{
    Task<DiagnosticsQueryExecution> ExecuteAsync(string sql, CancellationToken cancellationToken);
}
