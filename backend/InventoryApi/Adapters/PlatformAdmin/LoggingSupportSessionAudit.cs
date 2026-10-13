using System.Diagnostics;
using System.Globalization;
using Inventory.Application.SupportSessions;

namespace InventoryApi.Adapters.PlatformAdmin;

/// <summary>
/// Writes the support-session audit trail as one structured <c>ILogger</c> event per session started
/// and per session ended (issue #550).
///
/// <para>It is the same shape, and lives at the InventoryApi boundary for the same reasons, as
/// <see cref="LoggingPlatformBusinessDecisionAudit"/> and
/// <see cref="LoggingPlatformBusinessLifecycleAudit"/> beside it: everything on the event comes from
/// the Application layer, and the one thing this adapter adds is the request/correlation id, which is
/// a transport fact.</para>
///
/// <para><strong>What it carries that the others do not, and why.</strong> The reason the
/// administrator typed. Every other platform trail here excludes free text; this one is the record
/// that somebody outside a business was allowed to read its data, so "why" is the fact that makes the
/// access reviewable at all. It is the administrator's own justification - not the business's data,
/// not a member's personal detail.</para>
///
/// <para><strong>What is deliberately absent.</strong> The session id in any form, plaintext or hash:
/// a trail is read by more people than a response is, and a credential in a log is a credential. The
/// business's name, its members' names and addresses, and any Entra identity but the acting
/// administrator's. <see cref="SupportSessionAuditEntry"/> has no field for any of them, so even a
/// sink that printed the whole entry could not emit one.</para>
///
/// <para>Emitted at <c>Information</c>, once per event. A refused start is not audited, because no
/// session existed; an end that closed nothing is not audited either. Destination and retention are
/// the host's logging pipeline, which is platform configuration a human must verify rather than
/// something this application can assert.</para>
/// </summary>
public sealed class LoggingSupportSessionAudit : ISupportSessionAudit
{
    private readonly ILogger<LoggingSupportSessionAudit> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public LoggingSupportSessionAudit(
        ILogger<LoggingSupportSessionAudit> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public void Record(SupportSessionAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _logger.LogInformation(
            "Support session audit. Actor {ActorDirectoryTenantId}/{ActorObjectId}; "
                + "business {BusinessId}; session {SessionKey}; event {Event}; reason {Reason}; "
                + "started {StartedAtUtc:O}; expires {ExpiresAtUtc:O}; ended {EndedAtUtc}; "
                + "end reason {EndReason}; correlation {CorrelationId}.",
            entry.ActorDirectoryTenantId,
            entry.ActorObjectId,
            entry.BusinessId,
            entry.SessionKey,
            entry.Event,
            entry.Reason,
            entry.StartedAtUtc,
            entry.ExpiresAtUtc,
            // "none" rather than an empty rendering, so a start event and an end event whose instant
            // failed to be recorded cannot read the same in the trail.
            entry.EndedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "none",
            entry.EndReason?.ToString() ?? "none",
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
