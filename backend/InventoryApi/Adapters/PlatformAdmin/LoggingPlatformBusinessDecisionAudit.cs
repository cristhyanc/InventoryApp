using System.Diagnostics;
using Inventory.Application.PlatformAdmin;

namespace InventoryApi.Adapters.PlatformAdmin;

/// <summary>
/// Writes the platform business-decision audit trail as one structured <c>ILogger</c> event per
/// committed decision (issue #544).
///
/// <para>It is the same shape, and lives at the InventoryApi boundary for the same reasons, as
/// <see cref="PlatformDiagnostics.LoggingPlatformDiagnosticsAudit"/> and
/// <see cref="Onboarding.LoggingBusinessApplicationAudit"/>: everything on the event comes from the
/// Application layer, and the one thing this adapter adds is the request/correlation id, which is a
/// transport fact.</para>
///
/// <para><strong>What is deliberately absent.</strong> The business name, its contact email address
/// and ABN, the applicant's identity, and the rejection reason's <em>text</em>.
/// <see cref="PlatformBusinessDecisionAuditEntry"/> has no field for any of them, so even a sink
/// that printed the whole entry could not emit one; whether a reason was recorded is a flag. The
/// deciding administrator's Entra <c>(tid, oid)</c> pair, the business id and the status transition
/// <em>are</em> recorded, because an audit trail that cannot say who started or refused a tenant is
/// not an audit trail, and all three are identifiers rather than credentials or personal
/// detail.</para>
///
/// <para>Emitted at <c>Information</c>, once, after the decision is committed. A refused decision is
/// not audited here, because nothing happened. Destination and retention are the host's logging
/// pipeline, which is platform configuration a human must verify rather than something this
/// application can assert.</para>
/// </summary>
public sealed class LoggingPlatformBusinessDecisionAudit : IPlatformBusinessDecisionAudit
{
    private readonly ILogger<LoggingPlatformBusinessDecisionAudit> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public LoggingPlatformBusinessDecisionAudit(
        ILogger<LoggingPlatformBusinessDecisionAudit> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public void Record(PlatformBusinessDecisionAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _logger.LogInformation(
            "Platform business decision audit. Actor {ActorDirectoryTenantId}/{ActorObjectId}; "
                + "business {BusinessId}; status {FromStatus} -> {ToStatus}; reason recorded "
                + "{ReasonRecorded}; occurred {OccurredAtUtc:O}; correlation {CorrelationId}.",
            entry.ActorDirectoryTenantId,
            entry.ActorObjectId,
            entry.BusinessId,
            entry.FromStatus,
            entry.ToStatus,
            entry.ReasonRecorded,
            entry.OccurredAtUtc,
            CorrelationId());
    }

    /// <summary>
    /// The distributed trace id when telemetry is running, and the host's own request identifier
    /// otherwise, so the event is always correlatable to the request that produced it.
    /// </summary>
    private string CorrelationId() =>
        Activity.Current?.Id
            ?? _httpContextAccessor.HttpContext?.TraceIdentifier
            ?? "unknown";
}
