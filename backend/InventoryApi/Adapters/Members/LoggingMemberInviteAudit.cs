using System.Diagnostics;
using Inventory.Application.Members;

namespace InventoryApi.Adapters.Members;

/// <summary>
/// Writes the invite audit trail as one structured <c>ILogger</c> event per committed invite change
/// (issue #531).
///
/// <para>It is the same shape, and lives at the InventoryApi boundary for the same reasons, as
/// <see cref="LoggingMemberManagementAudit"/>: everything on the event comes from the Application
/// layer, and the one thing this adapter adds is the request/correlation id, which is a transport
/// fact.</para>
///
/// <para><strong>What is deliberately absent.</strong> The invite <em>token</em>, in plaintext or
/// hashed - the audit trail is exactly the kind of long-lived, widely-readable store a secret must
/// never reach, and <see cref="MemberInviteAuditEntry"/> has no field for it, so even a sink that
/// printed the whole entry could not emit one. The Owner's note is absent for a related reason: it
/// is free text that may name the person being invited. No display name, no email address, no
/// business name and no role matrix either. The <em>actor's</em> Entra <c>(tid, oid)</c> pair is
/// recorded, because an audit trail that cannot say who opened a way into the business is not an
/// audit trail, and those are identifiers rather than credentials.</para>
///
/// <para>Emitted at <c>Information</c>, once, after the change is committed. A refused or no-op
/// request is not audited here, because nothing happened: the conflict the boundary already logs is
/// the record of the attempt. Destination and retention are the host's logging pipeline, which is
/// platform configuration a human must verify rather than something this application can assert.</para>
/// </summary>
public sealed class LoggingMemberInviteAudit : IMemberInviteAudit
{
    private readonly ILogger<LoggingMemberInviteAudit> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public LoggingMemberInviteAudit(
        ILogger<LoggingMemberInviteAudit> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public void Record(MemberInviteAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _logger.LogInformation(
            "Member invite audit. Actor {ActorDirectoryTenantId}/{ActorObjectId}; "
                + "business {BusinessId}; invite {InviteId}; change {Change}; role {Role}; "
                + "occurred {OccurredAtUtc:O}; correlation {CorrelationId}.",
            entry.ActorDirectoryTenantId,
            entry.ActorObjectId,
            entry.BusinessId,
            entry.InviteId,
            entry.Change,
            entry.Role,
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
