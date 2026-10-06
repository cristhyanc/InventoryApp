namespace InventoryApi.DTOs;

/// <summary>
/// The capability signal for <c>GET /api/admin/diagnostics/access</c> (issue #336).
///
/// It exposes no tenant data of any kind: no business, no row, no count, no name and no identifier.
/// Reaching it at all is the signal - the platform-admin policy has already refused everyone else -
/// and the limits are published so the caller in issue #335 can size its input and label its
/// results from the server's numbers rather than restating them.
/// </summary>
/// <param name="Authorized">Always true in a 200 response; false is a 403, not a body.</param>
/// <param name="CrossBusinessScope">
/// Always true. The diagnostics data path reads across every business by design, and saying so in
/// the contract is what keeps that from being a surprise to a reader of the API.
/// </param>
/// <param name="Limits">The server-side hard maxima a query runs under.</param>
public sealed record PlatformDiagnosticsAccessResponse(
    bool Authorized,
    bool CrossBusinessScope,
    PlatformDiagnosticsLimitsResponse Limits);

/// <summary>The server-side hard maxima, which no caller can raise.</summary>
public sealed record PlatformDiagnosticsLimitsResponse(
    int MaxSqlBytes,
    int MaxRows,
    int MaxResponseBytes,
    double MaxDurationSeconds);

/// <summary>One submitted diagnostics statement. There is no other input: no limit, no page,
/// no business, and nothing that could widen what the query may read.</summary>
public sealed record PlatformDiagnosticsQueryRequest(string? Sql);

/// <summary>
/// The bounded result of one diagnostics query.
///
/// <paramref name="Truncated"/> and <paramref name="TruncationReason"/> are the contract's promise
/// that a short answer is never presented as a complete one, and <paramref name="QueryFingerprint"/>
/// is the same value the audit event records, so a result in front of an operator can be matched to
/// its log entry without the statement appearing in either.
/// </summary>
public sealed record PlatformDiagnosticsQueryResponse(
    string Outcome,
    string? DenialReason,
    string? Message,
    bool CrossBusinessScope,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    int RowCount,
    bool Truncated,
    string? TruncationReason,
    long DurationMilliseconds,
    string QueryFingerprint);
