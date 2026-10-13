using System.Diagnostics;
using Inventory.Application.PlatformAdmin;

namespace InventoryApi.Adapters.PlatformAdmin;

/// <summary>
/// Writes the platform business-lifecycle audit trail as one structured <c>ILogger</c> event per
/// committed action (issue #545).
///
/// <para>It is the same shape, and lives at the InventoryApi boundary for the same reasons, as
/// <see cref="LoggingPlatformBusinessDecisionAudit"/> beside it: everything on the event comes from
/// the Application layer, and the one thing this adapter adds is the request/correlation id, which is
/// a transport fact.</para>
///
/// <para><strong>What is deliberately absent.</strong> Every name: the business's, the released
/// member's display name and email address, and the member's Entra identity.
/// <see cref="PlatformBusinessLifecycleAuditEntry"/> has no field for any of them, so even a sink
/// that printed the whole entry could not emit one. The acting administrator's Entra
/// <c>(tid, oid)</c> pair, the business id, the membership id and the status transition <em>are</em>
/// recorded, because an audit trail that cannot say who switched a business off, or whose membership
/// they released, is not an audit trail - and all four are identifiers rather than personal
/// detail.</para>
///
/// <para>Emitted at <c>Information</c>, once, after the action is committed. A refused action and an
/// action that changed nothing are not audited, because nothing happened. Destination and retention
/// are the host's logging pipeline, which is platform configuration a human must verify rather than
/// something this application can assert.</para>
/// </summary>
public sealed class LoggingPlatformBusinessLifecycleAudit : IPlatformBusinessLifecycleAudit
{
    private readonly ILogger<LoggingPlatformBusinessLifecycleAudit> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public LoggingPlatformBusinessLifecycleAudit(
        ILogger<LoggingPlatformBusinessLifecycleAudit> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public void Record(PlatformBusinessLifecycleAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _logger.LogInformation(
            "Platform business lifecycle audit. Actor {ActorDirectoryTenantId}/{ActorObjectId}; "
                + "business {BusinessId}; action {Change}; status {FromStatus} -> {ToStatus}; "
                + "membership {MembershipId}; invites cancelled {InvitesCancelled}; "
                + "occurred {OccurredAtUtc:O}; correlation {CorrelationId}.",
            entry.ActorDirectoryTenantId,
            entry.ActorObjectId,
            entry.BusinessId,
            entry.Change,
            entry.FromStatus,
            entry.ToStatus,
            // "none" rather than an empty rendering, so an action that is about no membership and
            // one whose membership id failed to be recorded cannot read the same in the trail.
            entry.MembershipId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none",
            entry.InvitesCancelled?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none",
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
