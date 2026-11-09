using System.Diagnostics;
using Inventory.Application.PlatformOptions;

namespace InventoryApi.Adapters.PlatformAdmin;

/// <summary>
/// Writes the menu-visibility audit trail as one structured <c>ILogger</c> event per committed
/// change (issue #1148, replacing issue #873's option-visibility sink).
///
/// <para>It is the same shape, and lives at the InventoryApi boundary for the same reasons, as
/// <see cref="LoggingSupplierPresetAudit"/> and the two platform-business audits beside it:
/// everything on the event comes from the Application layer, and the one thing this adapter adds is
/// the request/correlation id, which is a transport fact.</para>
///
/// <para><strong>What is deliberately absent.</strong> Any business, member or data identifier.
/// <see cref="MenuVisibilityAuditEntry"/> has no field for one, because the setting is about none of
/// them: it records who switched a destination, which destination, and whether it is offered
/// afterwards, which is what an investigation needs and all of what it needs. The key it names is a
/// server-owned catalogue constant, never a value a caller invented.</para>
///
/// <para>Emitted at <c>Information</c>, once, after the write is committed. A refused change, and a
/// request for a key the catalogue does not declare, are not audited, because nothing happened.
/// Destination and retention are the host's logging pipeline, which is platform configuration a
/// human must verify rather than something this application can assert.</para>
/// </summary>
public sealed class LoggingMenuVisibilityAudit : IMenuVisibilityAudit
{
    private readonly ILogger<LoggingMenuVisibilityAudit> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public LoggingMenuVisibilityAudit(
        ILogger<LoggingMenuVisibilityAudit> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public void Record(MenuVisibilityAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _logger.LogInformation(
            "Platform menu visibility audit. Actor {ActorDirectoryTenantId}/{ActorObjectId}; "
                + "destination {Key} visible {Visible}; occurred {OccurredAtUtc:O}; "
                + "correlation {CorrelationId}.",
            entry.ActorDirectoryTenantId,
            entry.ActorObjectId,
            entry.Key,
            entry.Visible,
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
