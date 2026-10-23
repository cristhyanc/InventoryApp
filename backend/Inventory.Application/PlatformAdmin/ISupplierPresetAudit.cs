namespace Inventory.Application.PlatformAdmin;

/// <summary>Which change one supplier-preset audit event is about (issue #848).</summary>
public enum SupplierPresetChange
{
    /// <summary>A preset was created.</summary>
    Created = 1,

    /// <summary>
    /// A preset was edited. Enabling and disabling are edits too, which is why there is no separate
    /// value for them: the endpoint is one <c>PUT</c> of the whole resource, so "the name changed"
    /// and "it was disabled" arrive as the same request and
    /// <see cref="SupplierPresetAuditEntry.IsEnabled"/> records which state it ended in.
    /// </summary>
    Updated = 2,
}

/// <summary>
/// One audit event about one committed write to the platform supplier presets (issue #848): who did
/// it, which preset, which change, and whether the preset is enabled afterwards.
///
/// <para>It is emitted only after the write is committed, exactly as
/// <see cref="PlatformBusinessDecisionAuditEntry"/> and
/// <see cref="PlatformBusinessLifecycleAuditEntry"/> are: a refused write did not happen.</para>
///
/// <para><strong>What it deliberately does not carry.</strong> The preset's name and its URL. The
/// existing platform-admin audits carry no business name, no contact address and no rejection text,
/// and issue #848 asks for the same restraint here; the preset's key and the acting administrator's
/// Entra <c>(tid, oid)</c> pair are identifiers rather than content, and they are what an
/// investigation needs - "who changed which preset, and is it suggested now". There is no field for
/// a name or an address, so a sink that printed the whole entry could not emit one.</para>
/// </summary>
/// <param name="ActorDirectoryTenantId">The acting platform administrator's Entra <c>tid</c>.</param>
/// <param name="ActorObjectId">Their Entra <c>oid</c>.</param>
/// <param name="PresetId">The preset the change was about.</param>
/// <param name="Change">Which change it was.</param>
/// <param name="IsEnabled">Whether the preset is suggested after the change.</param>
/// <param name="OccurredAtUtc">When the change was committed, from the application clock.</param>
public sealed record SupplierPresetAuditEntry(
    string ActorDirectoryTenantId,
    string ActorObjectId,
    int PresetId,
    SupplierPresetChange Change,
    bool IsEnabled,
    DateTime OccurredAtUtc);

/// <summary>
/// The audit sink for platform supplier-preset writes, implemented at the InventoryApi boundary over
/// <c>ILogger</c> (issue #848) for the same reasons as the two platform-business audits beside it:
/// the event carries the request's correlation id, which is a transport fact, and an audit row in the
/// same database the change was made in would be evidence kept inside the thing it is evidence about.
/// </summary>
public interface ISupplierPresetAudit
{
    void Record(SupplierPresetAuditEntry entry);
}
