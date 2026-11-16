using System.Diagnostics;
using System.Globalization;
using Inventory.Application.Commissions;

namespace InventoryApi.Adapters.Commissions;

/// <summary>
/// Writes the commission-agreement audit trail as one structured <c>ILogger</c> event per committed
/// end date (issue #1254).
///
/// <para>It lives at the InventoryApi boundary for the reason
/// <see cref="Businesses.LoggingBusinessSettingsAudit"/> does: one field on the event, the
/// request/correlation id, is a transport fact. Everything else comes from the Application layer, so
/// this adapter adds that id and formats; it decides nothing about what is audited.</para>
///
/// <para><strong>What is deliberately absent.</strong> No site name, no commission rate and no money
/// figure - the site id names the site within the business the event already names, and the trail
/// exists to record the decision rather than to become a second copy of the business's financial
/// configuration. The acting member's Entra <c>(tid, oid)</c> pair, the business, the agreement and
/// both end dates <em>are</em> recorded, because commission is calculated on read: an end date
/// brought into the past changes what every later report says the site is owed from that date on,
/// and "who decided that, when, and what was it before" is what an investigation needs.</para>
///
/// <para><strong>Retention and destination are platform configuration, and a human must verify
/// them.</strong> This adapter emits an event; where it lands and how long it is kept is decided by
/// the host's logging pipeline - Application Insights when
/// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> is configured, and the App Service log stream
/// otherwise.</para>
///
/// <para>Emitted at <c>Information</c>, once, after the change is committed. A refused change is not
/// audited here, because it did not happen.</para>
/// </summary>
public sealed class LoggingSiteCommissionAgreementAudit : ISiteCommissionAgreementAudit
{
    private readonly ILogger<LoggingSiteCommissionAgreementAudit> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public LoggingSiteCommissionAgreementAudit(
        ILogger<LoggingSiteCommissionAgreementAudit> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public void Record(SiteCommissionAgreementEndedAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _logger.LogInformation(
            "Site commission agreement ended. Actor {ActorDirectoryTenantId}/{ActorObjectId}; "
                + "business {BusinessId}; agreement {AgreementId}; site {SiteId}; "
                + "end date {PreviousEndDate} -> {NewEndDate}; occurred {OccurredAtUtc:O}; "
                + "correlation {CorrelationId}.",
            entry.ActorDirectoryTenantId,
            entry.ActorObjectId,
            entry.BusinessId,
            entry.AgreementId,
            entry.SiteId,
            entry.PreviousEndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "none",
            entry.NewEndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
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
