using System.Diagnostics;
using Inventory.Application.PlatformDiagnostics;

namespace InventoryApi.Adapters.PlatformDiagnostics;

/// <summary>
/// Writes the platform diagnostics audit trail as one structured <c>ILogger</c> event per query
/// (issue #336).
///
/// <para>It lives at the InventoryApi boundary because of one field: the request/correlation id,
/// which is a transport fact. Everything else on the event comes from the Application layer, so
/// this adapter adds the correlation id and formats; it decides nothing about what is audited.</para>
///
/// <para><strong>What is deliberately absent.</strong> No raw SQL - the query is identified by the
/// SHA-256 of its normalised shape. No result rows, no column values, no column names. No business
/// identifier or business name. No bearer token, authorization header, connection string or
/// credential of any kind. The actor's Entra <c>(tid, oid)</c> pair <em>is</em> recorded, because an
/// audit trail that cannot say who ran a cross-business query is not an audit trail; those are
/// identifiers rather than credentials, and they are the same pair the application already logs
/// nothing else about.</para>
///
/// <para><strong>Retention and destination are platform configuration, and a human must verify
/// them.</strong> This adapter emits an event; where it lands and how long it is kept is decided by
/// the host's logging pipeline - Application Insights when
/// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> is configured, and the App Service log stream
/// otherwise - with a retention period set on that resource, not in this repository. The
/// application cannot assert that its audit trail is durable, so it does not: see
/// README.md § Platform diagnostics API and docs/architecture.md § Platform diagnostics.</para>
///
/// <para>The event is emitted at <c>Information</c> for every outcome, including refusals. A
/// refused query is an access attempt and is exactly what an investigation into misuse would look
/// for, so it must not be the one outcome that is quieter than the others.</para>
/// </summary>
public sealed class LoggingPlatformDiagnosticsAudit : IPlatformDiagnosticsAudit
{
    private readonly ILogger<LoggingPlatformDiagnosticsAudit> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public LoggingPlatformDiagnosticsAudit(
        ILogger<LoggingPlatformDiagnosticsAudit> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public void Record(PlatformDiagnosticsAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _logger.LogInformation(
            "Platform diagnostics query audit. Actor {ActorDirectoryTenantId}/{ActorObjectId}; "
                + "occurred {OccurredAtUtc:O}; correlation {CorrelationId}; "
                + "query shape {QueryFingerprint}; cross-business scope {CrossBusinessScope}; "
                + "outcome {Outcome}; denial {DenialReason}; truncation {TruncationReason}; "
                + "duration {DurationMilliseconds} ms; rows {RowCount}.",
            entry.ActorDirectoryTenantId,
            entry.ActorObjectId,
            entry.OccurredAtUtc,
            CorrelationId(),
            entry.QueryFingerprint,
            entry.CrossBusinessScope,
            entry.Outcome,
            entry.DenialReason,
            entry.TruncationReason,
            entry.DurationMilliseconds,
            entry.RowCount);
    }

    /// <summary>
    /// The distributed trace id when telemetry is running, and the host's own request identifier
    /// otherwise, so the event is always correlatable to the request that produced it even on a
    /// host with no Application Insights connection string.
    /// </summary>
    private string CorrelationId() =>
        Activity.Current?.Id
            ?? _httpContextAccessor.HttpContext?.TraceIdentifier
            ?? "unknown";
}
