namespace Inventory.Application.PlatformOptions;

/// <summary>
/// One audit event about one committed change to the shared menu visibility (issue #1148, the
/// successor to issue #873's <c>OptionVisibilityAuditEntry</c>): who made it, which destination it
/// was about, and the state that destination ended in.
///
/// <para>It is emitted only after the write is committed, exactly as the platform-admin audits
/// beside it are: a refused write did not happen, and neither did a write for a key the catalogue
/// does not declare.</para>
///
/// <para><strong>What it deliberately does not carry.</strong> Any business, member or data
/// identifier - because this setting is about none of them. The acting administrator's Entra
/// <c>(tid, oid)</c> pair, the key and the resulting state are what an investigation needs: "who
/// switched this, which destination, and is it offered now". There is no field for anything else, so
/// a sink that printed the whole entry could not emit one.</para>
/// </summary>
/// <param name="ActorDirectoryTenantId">The acting platform administrator's Entra <c>tid</c>.</param>
/// <param name="ActorObjectId">Their Entra <c>oid</c>.</param>
/// <param name="Key">
/// The catalogue key of the destination that was switched; see <see cref="MenuVisibilityKeys"/>. It
/// is a server-owned constant rather than anything a caller invented - an unknown key stores nothing
/// and is never audited.
/// </param>
/// <param name="Visible">Whether that destination is offered after the change.</param>
/// <param name="OccurredAtUtc">When the change was committed, from the application clock.</param>
public sealed record MenuVisibilityAuditEntry(
    string ActorDirectoryTenantId,
    string ActorObjectId,
    string Key,
    bool Visible,
    DateTime OccurredAtUtc);

/// <summary>
/// The audit sink for menu-visibility changes, implemented at the InventoryApi boundary over
/// <c>ILogger</c> (issue #1148, inheriting issue #873's pattern) for the same reasons as the
/// platform-admin audits beside it: the event carries the request's correlation id, which is a
/// transport fact, and an audit row in the same database the change was made in would be evidence
/// kept inside the thing it is evidence about.
/// </summary>
public interface IMenuVisibilityAudit
{
    void Record(MenuVisibilityAuditEntry entry);
}
