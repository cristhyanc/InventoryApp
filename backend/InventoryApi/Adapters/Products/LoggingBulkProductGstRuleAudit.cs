using System.Diagnostics;
using Inventory.Application.Products;

namespace InventoryApi.Adapters.Products;

/// <summary>
/// Writes the bulk product GST rule audit trail as one structured <c>ILogger</c> event per
/// committed change (issue #1076).
///
/// <para>It lives at the InventoryApi boundary for the reason
/// <see cref="Businesses.LoggingBusinessSettingsAudit"/> does: one field on the event, the
/// request/correlation id, is a transport fact. Everything else comes from the Application layer,
/// so this adapter adds that id and formats; it decides nothing about what is audited, and a
/// refused or no-op change reaches it at all.</para>
///
/// <para><strong>What is deliberately absent.</strong> No product id, product name, SKU, category,
/// price, cost or stock figure, and no list of the products that changed - the event names the rule
/// that was configured and how many products took it. The acting member's Entra <c>(tid, oid)</c>
/// pair and the business id <em>are</em> recorded, because an audit trail that cannot say who
/// changed what, and where, is not one; neither is a credential. The entry type has no field for a
/// product of any kind, so this sink could not emit one.</para>
///
/// <para><strong>Retention and destination are platform configuration, and a human must verify
/// them</strong>, exactly as for the audit trails beside it: this adapter emits an event, and where
/// it lands and how long it is kept is decided by the host's logging pipeline.</para>
///
/// <para>The event is emitted at <c>Information</c>: configuring GST rules is an ordinary,
/// authorised operation, and the trail exists to be read rather than to warn.</para>
/// </summary>
public sealed class LoggingBulkProductGstRuleAudit : IBulkProductGstRuleAudit
{
    private readonly ILogger<LoggingBulkProductGstRuleAudit> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public LoggingBulkProductGstRuleAudit(
        ILogger<LoggingBulkProductGstRuleAudit> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public void Record(BulkProductGstRuleAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _logger.LogInformation(
            "Product GST rules changed in bulk. Actor {ActorDirectoryTenantId}/{ActorObjectId}; "
                + "business {BusinessId}; rule {TargetGstRule}; products changed {ChangedCount}; "
                + "occurred {OccurredAtUtc:O}; correlation {CorrelationId}.",
            entry.ActorDirectoryTenantId,
            entry.ActorObjectId,
            entry.BusinessId,
            entry.TargetRule,
            entry.ChangedCount,
            entry.OccurredAtUtc,
            CorrelationId());
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
