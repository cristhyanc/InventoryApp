using System.Diagnostics;
using Inventory.Application.PlatformNayaxSqs;

namespace InventoryApi.Adapters.PlatformAdmin;

/// <summary>
/// Writes the Nayax SQS sender trail as one structured <c>ILogger</c> event per committed decision
/// (issue #1101).
///
/// <para>It is the same shape, and lives at the InventoryApi boundary for the same reasons, as the
/// plan-grant, platform-business and option-visibility audits beside it: everything on the event
/// comes from the Application layer, and the one thing this adapter adds is the
/// request/correlation id, which is a transport fact.</para>
///
/// <para><strong>It is beside the audit row, not instead of it.</strong> The decision's own
/// <c>BusinessNayaxSqsSenderAudits</c> row is written in the same transaction as the change, which
/// is what makes "the mapping and the record of who decided it commit together" true; this event is
/// the operational trail every platform-admin action emits.</para>
///
/// <para><strong>What is deliberately absent.</strong> The AWS sender id on either side - both are
/// stored in the database and never logged - and the business's name, its contact detail and
/// anybody's identity but the acting administrator's own Entra pair.
/// <see cref="PlatformNayaxSqsSenderAuditEntry"/> has no field for a sender id or a name of any
/// kind, so even a sink that printed the whole entry could not emit one. No AWS access key, secret
/// or ARN can appear here because this application stores none.</para>
///
/// <para>Emitted at <c>Information</c>, once, after the decision is committed. A refused decision is
/// not audited, because nothing happened.</para>
/// </summary>
public sealed class LoggingPlatformNayaxSqsSenderAudit : IPlatformNayaxSqsSenderAudit
{
    private readonly ILogger<LoggingPlatformNayaxSqsSenderAudit> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public LoggingPlatformNayaxSqsSenderAudit(
        ILogger<LoggingPlatformNayaxSqsSenderAudit> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public void Record(PlatformNayaxSqsSenderAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _logger.LogInformation(
            "Platform Nayax SQS sender audit. Actor {ActorDirectoryTenantId}/{ActorObjectId}; "
                + "business {BusinessId}; had sender {HadSenderId}; has sender {HasSenderId}; was "
                + "enabled {WasEnabled}; enabled {IsEnabled}; revision {Revision}; occurred "
                + "{OccurredAtUtc:O}; correlation {CorrelationId}.",
            entry.ActorDirectoryTenantId,
            entry.ActorObjectId,
            entry.BusinessId,
            entry.HadSenderId,
            entry.HasSenderId,
            entry.WasEnabled,
            entry.IsEnabled,
            entry.Revision,
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
