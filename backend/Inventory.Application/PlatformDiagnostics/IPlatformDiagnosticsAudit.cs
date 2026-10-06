namespace Inventory.Application.PlatformDiagnostics;

/// <summary>
/// One audit event about one cross-business diagnostics query (issue #336).
///
/// What it deliberately does <em>not</em> carry is as much of the contract as what it does: no raw
/// SQL, no result row, no column value, no credential, and no business name. The query is
/// identified by its normalised shape fingerprint alone, so the event records what was asked
/// without recording what was found or which identifiers were typed in.
/// </summary>
/// <param name="ActorDirectoryTenantId">The platform admin's Entra <c>tid</c>.</param>
/// <param name="ActorObjectId">The platform admin's Entra <c>oid</c>.</param>
/// <param name="OccurredAtUtc">When the query ran, from the application clock.</param>
/// <param name="QueryFingerprint">SHA-256 of the normalised query shape, never the statement.</param>
/// <param name="Outcome">Success, truncation, refusal, timeout, cancellation or failure.</param>
/// <param name="DenialReason">Why a refused query was refused; <c>None</c> otherwise.</param>
/// <param name="TruncationReason">Which cap stopped a truncated read; <c>null</c> otherwise.</param>
/// <param name="DurationMilliseconds">Preparation, execution and result reading together.</param>
/// <param name="RowCount">Rows actually returned, which for a truncated read is the capped count.</param>
/// <param name="CrossBusinessScope">Always true: this path reads across every business by design.</param>
public sealed record PlatformDiagnosticsAuditEntry(
    string ActorDirectoryTenantId,
    string ActorObjectId,
    DateTime OccurredAtUtc,
    string QueryFingerprint,
    DiagnosticsQueryOutcome Outcome,
    DiagnosticsQueryDenialReason DenialReason,
    DiagnosticsTruncationReason? TruncationReason,
    long DurationMilliseconds,
    int RowCount,
    bool CrossBusinessScope = true);

/// <summary>
/// The audit sink for the diagnostics path, implemented at the InventoryApi boundary over
/// <c>ILogger</c> (issue #336).
///
/// It is deliberately a log port and not a database table. An audit row written through the same
/// SQLite database the query reads would be evidence kept inside the thing it is evidence about,
/// would need a migration and an owner for a non-tenant-owned table, and would make a read-only
/// diagnostics request a write. The structured log event goes to the platform's log pipeline
/// instead - whose destination and retention are platform configuration a human must verify, not
/// something this application can assert.
/// </summary>
public interface IPlatformDiagnosticsAudit
{
    void Record(PlatformDiagnosticsAuditEntry entry);
}
