namespace Inventory.Application.PlatformOptions;

/// <summary>
/// One audit event about one committed change to the shared option visibility (issue #873): who
/// made it, and the state the setting ended in.
///
/// <para>It is emitted only after the write is committed, exactly as the platform-admin audits
/// beside it are: a refused write did not happen.</para>
///
/// <para><strong>What it deliberately does not carry.</strong> Any business, member or data
/// identifier - because this setting is about none of them. The acting administrator's Entra
/// <c>(tid, oid)</c> pair and the resulting state are what an investigation needs: "who switched
/// this, and is the option offered now". There is no field for anything else, so a sink that printed
/// the whole entry could not emit one.</para>
/// </summary>
/// <param name="ActorDirectoryTenantId">The acting platform administrator's Entra <c>tid</c>.</param>
/// <param name="ActorObjectId">Their Entra <c>oid</c>.</param>
/// <param name="HistoricalGstClassificationVisible">
/// Whether Historical GST Classification is offered after the change.
/// </param>
/// <param name="OccurredAtUtc">When the change was committed, from the application clock.</param>
public sealed record OptionVisibilityAuditEntry(
    string ActorDirectoryTenantId,
    string ActorObjectId,
    bool HistoricalGstClassificationVisible,
    DateTime OccurredAtUtc);

/// <summary>
/// The audit sink for option-visibility changes, implemented at the InventoryApi boundary over
/// <c>ILogger</c> (issue #873) for the same reasons as the platform-admin audits beside it: the
/// event carries the request's correlation id, which is a transport fact, and an audit row in the
/// same database the change was made in would be evidence kept inside the thing it is evidence
/// about.
/// </summary>
public interface IOptionVisibilityAudit
{
    void Record(OptionVisibilityAuditEntry entry);
}
