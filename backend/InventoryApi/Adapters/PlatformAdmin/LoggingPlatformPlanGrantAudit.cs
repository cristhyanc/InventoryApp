using System.Diagnostics;
using Inventory.Application.PlatformPlans;

namespace InventoryApi.Adapters.PlatformAdmin;

/// <summary>
/// Writes the manual plan grant trail as one structured <c>ILogger</c> event per committed decision
/// (issue #982).
///
/// <para>It is the same shape, and lives at the InventoryApi boundary for the same reasons, as the
/// platform-business and option-visibility audits beside it: everything on the event comes from the
/// Application layer, and the one thing this adapter adds is the request/correlation id, which is a
/// transport fact.</para>
///
/// <para><strong>It is beside the audit row, not instead of it.</strong> The decision's own
/// <c>BusinessPlanGrantAudits</c> row is written in the same transaction as the grant, which is what
/// makes "the grant and the record of who gave it commit together" true; this event is the
/// operational trail every platform-admin action emits.</para>
///
/// <para><strong>What is deliberately absent.</strong> The administrator's reason text, which is
/// stored in the database and never logged, and the business's name, its contact detail and
/// anybody's identity but the acting administrator's own Entra pair.
/// <see cref="PlatformPlanGrantAuditEntry"/> has no field for a reason or a name of any kind, so
/// even a sink that printed the whole entry could not emit one.</para>
///
/// <para>Emitted at <c>Information</c>, once, after the decision is committed. A refused decision is
/// not audited, because nothing happened.</para>
/// </summary>
public sealed class LoggingPlatformPlanGrantAudit : IPlatformPlanGrantAudit
{
    private readonly ILogger<LoggingPlatformPlanGrantAudit> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public LoggingPlatformPlanGrantAudit(
        ILogger<LoggingPlatformPlanGrantAudit> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public void Record(PlatformPlanGrantAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _logger.LogInformation(
            "Platform plan grant audit. Actor {ActorDirectoryTenantId}/{ActorObjectId}; business "
                + "{BusinessId}; change {Change}; from {FromPlan}; to {ToPlan}; ends {EndsAtUtc:O}; "
                + "revision {Revision}; occurred {OccurredAtUtc:O}; correlation {CorrelationId}.",
            entry.ActorDirectoryTenantId,
            entry.ActorObjectId,
            entry.BusinessId,
            entry.Change,
            entry.FromPlan,
            entry.ToPlan,
            entry.EndsAtUtc,
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
