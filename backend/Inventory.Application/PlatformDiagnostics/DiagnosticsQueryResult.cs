namespace Inventory.Application.PlatformDiagnostics;

/// <summary>How a diagnostics query ended. Every value is reported, never smoothed over.</summary>
public enum DiagnosticsQueryOutcome
{
    /// <summary>The complete result was read within every limit.</summary>
    Succeeded,

    /// <summary>
    /// A valid result was read but a limit stopped it short. The rows returned are a prefix, not
    /// the answer, and <see cref="DiagnosticsQueryResult.TruncationReason"/> says which cap hit.
    /// </summary>
    Truncated,

    /// <summary>
    /// The query was refused before or during preparation: oversized, not a single read-only
    /// statement, or reaching outside the permitted data surface. Nothing was read and nothing
    /// could have been written.
    /// </summary>
    Rejected,

    /// <summary>The query exceeded the duration ceiling and SQLite was interrupted.</summary>
    TimedOut,

    /// <summary>The caller's request was cancelled and SQLite was interrupted.</summary>
    Cancelled,

    /// <summary>SQLite reported some other error; the message is the provider's, not the data.</summary>
    Failed,
}

/// <summary>Why a query was refused. <c>None</c> accompanies every non-rejected outcome.</summary>
public enum DiagnosticsQueryDenialReason
{
    None,

    /// <summary>No SQL was submitted.</summary>
    SqlMissing,

    /// <summary>The submitted SQL exceeded <see cref="PlatformDiagnosticsQueryLimits.MaxSqlBytes"/>.</summary>
    SqlTooLarge,

    /// <summary>More than one statement was submitted.</summary>
    MultipleStatements,

    /// <summary>The statement is not a bare <c>SELECT</c>/<c>WITH … SELECT</c> read.</summary>
    NotAReadOnlyStatement,

    /// <summary>
    /// The statement reached a table or column outside <see cref="PlatformDiagnosticsDataSurface"/>,
    /// which SQLite itself refused while preparing it.
    /// </summary>
    ForbiddenSchemaAccess,

    /// <summary>
    /// The statement attempted an operation the diagnostics connection does not permit at all - a
    /// write, DDL, a <c>PRAGMA</c>, an <c>ATTACH</c>, or a function outside the permitted set.
    /// </summary>
    ForbiddenOperation,
}

/// <summary>Which cap stopped a truncated read.</summary>
public enum DiagnosticsTruncationReason
{
    /// <summary><see cref="PlatformDiagnosticsQueryLimits.MaxRows"/> rows had been read.</summary>
    RowLimit,

    /// <summary>The next row would have taken the response past its byte budget.</summary>
    ResponseByteLimit,
}

/// <summary>
/// What <c>IDiagnosticsQueryExecutor</c> managed to read. It carries no duration and no
/// fingerprint: those belong to the use case that timed the call and audited it.
/// </summary>
public sealed record DiagnosticsQueryExecution(
    DiagnosticsQueryOutcome Outcome,
    DiagnosticsQueryDenialReason DenialReason,
    string? Message,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    DiagnosticsTruncationReason? TruncationReason)
{
    public static DiagnosticsQueryExecution Rejected(DiagnosticsQueryDenialReason reason, string message) =>
        new(DiagnosticsQueryOutcome.Rejected, reason, message, [], [], null);
}

/// <summary>
/// The complete, bounded answer to one diagnostics query (issue #336).
///
/// Values are carried as strings because the surface is a diagnostic read of identity and
/// foreign-key columns, not a financial API: nothing downstream calculates with them, and keeping
/// them as text means a result can never be mistaken for a money or quantity contract.
///
/// <see cref="CrossBusinessScope"/> is always true and is part of the contract rather than a
/// parameter. This endpoint reads across every business by design, so the response says so
/// explicitly instead of leaving a reader to infer it - and the audit event records the same fact.
/// </summary>
public sealed record DiagnosticsQueryResult(
    DiagnosticsQueryOutcome Outcome,
    DiagnosticsQueryDenialReason DenialReason,
    string? Message,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    DiagnosticsTruncationReason? TruncationReason,
    long DurationMilliseconds,
    string QueryFingerprint)
{
    public int RowCount => Rows.Count;

    public bool Truncated => Outcome == DiagnosticsQueryOutcome.Truncated;

    public static bool CrossBusinessScope => true;
}
