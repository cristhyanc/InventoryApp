using System.Diagnostics;
using Inventory.Application.Members;

namespace InventoryApi.Adapters.Members;

/// <summary>
/// Writes the member management audit trail as one structured <c>ILogger</c> event per committed
/// change (issue #533).
///
/// <para>It lives at the InventoryApi boundary for the same reason
/// <see cref="PlatformDiagnostics.LoggingPlatformDiagnosticsAudit"/> does, and is the same shape:
/// the one thing it adds is the request/correlation id, which is a transport fact. Everything else
/// on the event comes from the Application layer, so this adapter formats and decides nothing about
/// what is audited.</para>
///
/// <para><strong>What is deliberately absent.</strong> No display name and no email address - they
/// are display-only personal data and never logged (issue #524), and they identify nobody here. No
/// business name, no role matrix, no capability, and no Entra pair for the member who was changed:
/// the membership id identifies them within the business the event already names. The <em>actor's</em>
/// <c>(tid, oid)</c> pair is recorded, because an audit trail that cannot say who changed somebody's
/// access is not an audit trail, and those are identifiers rather than credentials.</para>
///
/// <para><strong>Retention and destination are platform configuration, and a human must verify
/// them.</strong> This adapter emits an event; where it lands and how long it is kept is decided by
/// the host's logging pipeline - Application Insights when
/// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> is configured, and the App Service log stream
/// otherwise. The application cannot assert that its audit trail is durable, so it does not.</para>
///
/// <para>Emitted at <c>Information</c>, once, after the change is committed. A refused change is
/// not audited here, because it did not happen: the conflict the boundary already logs is the
/// record of the attempt.</para>
/// </summary>
public sealed class LoggingMemberManagementAudit : IMemberManagementAudit
{
    private readonly ILogger<LoggingMemberManagementAudit> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public LoggingMemberManagementAudit(
        ILogger<LoggingMemberManagementAudit> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public void Record(MemberChangeAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _logger.LogInformation(
            "Member management audit. Actor {ActorDirectoryTenantId}/{ActorObjectId}; "
                + "business {BusinessId}; membership {MembershipId}; change {Change}; "
                + "role {PreviousRole} -> {NewRole}; active {PreviouslyActive} -> {NowActive}; "
                + "occurred {OccurredAtUtc:O}; correlation {CorrelationId}.",
            entry.ActorDirectoryTenantId,
            entry.ActorObjectId,
            entry.BusinessId,
            entry.MembershipId,
            entry.Change,
            entry.PreviousRole,
            entry.NewRole,
            entry.PreviouslyActive,
            entry.NowActive,
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
