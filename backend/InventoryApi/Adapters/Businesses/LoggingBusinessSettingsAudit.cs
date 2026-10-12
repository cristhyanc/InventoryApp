using System.Diagnostics;
using Inventory.Application.Businesses;

namespace InventoryApi.Adapters.Businesses;

/// <summary>
/// Writes the business-settings audit trail as one structured <c>ILogger</c> event per change
/// (issue #548).
///
/// <para>It lives at the InventoryApi boundary for the reason
/// <c>LoggingPlatformDiagnosticsAudit</c> does: one field on the event, the request/correlation id,
/// is a transport fact. Everything else comes from the Application layer, so this adapter adds that
/// id and formats; it decides nothing about what is audited.</para>
///
/// <para><strong>What is deliberately absent.</strong> No contact email address, no ABN, no business
/// name, and no before-or-after value of any kind - the event names the <em>fields</em> that
/// changed, from the closed <c>BusinessSettingsField</c> vocabulary, so the audit trail cannot
/// become a second copy of the business's contact and tax detail. The acting Owner's Entra
/// <c>(tid, oid)</c> pair and the business id <em>are</em> recorded, because an audit trail that
/// cannot say who changed what, and where, is not one; neither is a credential.</para>
///
/// <para><strong>Retention and destination are platform configuration, and a human must verify
/// them.</strong> This adapter emits an event; where it lands and how long it is kept is decided by
/// the host's logging pipeline - Application Insights when
/// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> is configured, and the App Service log stream
/// otherwise - with a retention period set on that resource, not in this repository.</para>
///
/// <para>The event is emitted at <c>Information</c>: a settings change is an ordinary, authorised
/// operation, and the trail exists to be read rather than to warn.</para>
/// </summary>
public sealed class LoggingBusinessSettingsAudit : IBusinessSettingsAudit
{
    private readonly ILogger<LoggingBusinessSettingsAudit> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public LoggingBusinessSettingsAudit(
        ILogger<LoggingBusinessSettingsAudit> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public void Record(BusinessSettingsAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _logger.LogInformation(
            "Business settings changed. Actor {ActorDirectoryTenantId}/{ActorObjectId}; "
                + "business {BusinessId}; occurred {OccurredAtUtc:O}; correlation {CorrelationId}; "
                + "fields {ChangedFields}.",
            entry.ActorDirectoryTenantId,
            entry.ActorObjectId,
            entry.BusinessId,
            entry.OccurredAtUtc,
            CorrelationId(),
            string.Join(",", entry.ChangedFields));
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
