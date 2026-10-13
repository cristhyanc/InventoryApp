using System.Diagnostics;
using Inventory.Application.Onboarding;

namespace InventoryApi.Adapters.Onboarding;

/// <summary>
/// Writes the business-application audit trail as one structured <c>ILogger</c> event per committed
/// application (issue #541).
///
/// <para>It is the same shape, and lives at the InventoryApi boundary for the same reasons, as
/// <see cref="Members.LoggingMemberInviteAudit"/>: everything on the event comes from the
/// Application layer, and the one thing this adapter adds is the request/correlation id, which is a
/// transport fact.</para>
///
/// <para><strong>What is deliberately absent.</strong> The business name, the contact email
/// address, the ABN, the GST registration and the applicant's own display name and email address.
/// A business name and a contact address are values a stranger typed into a public form about a
/// party nobody has approved yet, and the ABN and the GST flag are tax claims rather than facts
/// this trail is evidence of; <see cref="BusinessApplicationAuditEntry"/> has no field for any of
/// them, so even a sink that printed the whole entry could not emit one. The <em>actor's</em> Entra
/// <c>(tid, oid)</c> pair and the new business id are recorded, because an audit trail that cannot
/// say who created a tenant is not an audit trail, and both are identifiers rather than credentials
/// or personal detail.</para>
///
/// <para>Emitted at <c>Information</c>, once, after the application is committed. A refused
/// submission is not audited here, because nothing happened, and neither is a resubmission that
/// returned the application the caller already had: one application is one event. Destination and
/// retention are the host's logging pipeline, which is platform configuration a human must verify
/// rather than something this application can assert.</para>
/// </summary>
public sealed class LoggingBusinessApplicationAudit : IBusinessApplicationAudit
{
    private readonly ILogger<LoggingBusinessApplicationAudit> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public LoggingBusinessApplicationAudit(
        ILogger<LoggingBusinessApplicationAudit> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public void Record(BusinessApplicationAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _logger.LogInformation(
            "Business application audit. Actor {ActorDirectoryTenantId}/{ActorObjectId}; "
                + "business {BusinessId}; occurred {OccurredAtUtc:O}; correlation {CorrelationId}.",
            entry.ActorDirectoryTenantId,
            entry.ActorObjectId,
            entry.BusinessId,
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
