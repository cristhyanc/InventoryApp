using System.Diagnostics;
using Inventory.Application.PlatformOptions;

namespace InventoryApi.Adapters.PlatformAdmin;

/// <summary>
/// Writes the option-visibility audit trail as one structured <c>ILogger</c> event per committed
/// change (issue #873).
///
/// <para>It is the same shape, and lives at the InventoryApi boundary for the same reasons, as
/// <see cref="LoggingSupplierPresetAudit"/> and the two platform-business audits beside it:
/// everything on the event comes from the Application layer, and the one thing this adapter adds is
/// the request/correlation id, which is a transport fact.</para>
///
/// <para><strong>What is deliberately absent.</strong> Any business, member or data identifier.
/// <see cref="OptionVisibilityAuditEntry"/> has no field for one, because the setting is about none
/// of them: it records who switched the option and whether it is offered afterwards, which is what
/// an investigation needs and all of what it needs.</para>
///
/// <para>Emitted at <c>Information</c>, once, after the write is committed. A refused change is not
/// audited, because nothing happened. Destination and retention are the host's logging pipeline,
/// which is platform configuration a human must verify rather than something this application can
/// assert.</para>
/// </summary>
public sealed class LoggingOptionVisibilityAudit : IOptionVisibilityAudit
{
    private readonly ILogger<LoggingOptionVisibilityAudit> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public LoggingOptionVisibilityAudit(
        ILogger<LoggingOptionVisibilityAudit> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public void Record(OptionVisibilityAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _logger.LogInformation(
            "Platform option visibility audit. Actor {ActorDirectoryTenantId}/{ActorObjectId}; "
                + "historical GST classification visible {HistoricalGstClassificationVisible}; "
                + "occurred {OccurredAtUtc:O}; correlation {CorrelationId}.",
            entry.ActorDirectoryTenantId,
            entry.ActorObjectId,
            entry.HistoricalGstClassificationVisible,
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
