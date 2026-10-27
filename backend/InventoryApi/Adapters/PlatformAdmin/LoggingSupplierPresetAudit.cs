using System.Diagnostics;
using Inventory.Application.PlatformAdmin;

namespace InventoryApi.Adapters.PlatformAdmin;

/// <summary>
/// Writes the platform supplier-preset audit trail as one structured <c>ILogger</c> event per
/// committed write (issue #848).
///
/// <para>It is the same shape, and lives at the InventoryApi boundary for the same reasons, as
/// <see cref="LoggingPlatformBusinessDecisionAudit"/> and
/// <see cref="LoggingPlatformBusinessLifecycleAudit"/> beside it: everything on the event comes from
/// the Application layer, and the one thing this adapter adds is the request/correlation id, which
/// is a transport fact.</para>
///
/// <para><strong>What is deliberately absent.</strong> The preset's name and its URL.
/// <see cref="SupplierPresetAuditEntry"/> has no field for either, so even a sink that printed the
/// whole entry could not emit one - the same restraint the two audits beside it show about a
/// business's name, contact address and rejection text. The acting administrator's Entra
/// <c>(tid, oid)</c> pair, the preset's key, which change it was and whether the preset is suggested
/// afterwards <em>are</em> recorded, because an audit trail that cannot say who changed which preset
/// is not an audit trail, and all four are identifiers or states rather than content.</para>
///
/// <para><strong>And so are the supplier-master fields of issue #931</strong> - the market, whether
/// the published prices include GST, the collection route and its key - because they are the
/// configuration a Super Admin came here to change, and "who switched this supplier to ex-GST
/// prices" is exactly the question this trail exists to answer. Each of them is a state or a closed
/// vocabulary's value, and the connector key names which retailer a shared route is for; none is
/// anybody's name, address or business data.</para>
///
/// <para>Emitted at <c>Information</c>, once, after the write is committed. A refused create or edit
/// is not audited, because nothing happened. Destination and retention are the host's logging
/// pipeline, which is platform configuration a human must verify rather than something this
/// application can assert.</para>
/// </summary>
public sealed class LoggingSupplierPresetAudit : ISupplierPresetAudit
{
    private readonly ILogger<LoggingSupplierPresetAudit> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public LoggingSupplierPresetAudit(
        ILogger<LoggingSupplierPresetAudit> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public void Record(SupplierPresetAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _logger.LogInformation(
            "Platform supplier preset audit. Actor {ActorDirectoryTenantId}/{ActorObjectId}; "
                + "preset {PresetId}; change {Change}; enabled {IsEnabled}; nationwide "
                + "{ServesNationwide}; states {ServedStates}; prices include GST "
                + "{PricesIncludeGst}; connector {PriceConnector}; connector key "
                + "{PriceConnectorKey}; occurred {OccurredAtUtc:O}; correlation {CorrelationId}.",
            entry.ActorDirectoryTenantId,
            entry.ActorObjectId,
            entry.PresetId,
            entry.Change,
            entry.IsEnabled,
            entry.ServesNationwide,
            entry.ServedStates,
            entry.PricesIncludeGst,
            entry.PriceConnector,
            entry.PriceConnectorKey,
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
