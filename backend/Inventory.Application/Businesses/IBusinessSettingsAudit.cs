using Inventory.Domain.Tenancy;

namespace Inventory.Application.Businesses;

/// <summary>
/// One audit event about one change to a business's own settings (issue #548).
///
/// <para>What it deliberately does <em>not</em> carry is as much of the contract as what it does:
/// no contact email address, no ABN, no business name, and no before-or-after value of any kind.
/// The event records <em>that</em> a setting changed, who changed it and in which business - which
/// is what an investigation into "who changed our ABN" needs - without turning the audit trail into
/// a second, unprotected copy of the business's tax and contact detail. The fields are a closed
/// vocabulary (<see cref="BusinessSettingsField"/>) rather than assembled strings, so the event
/// cannot pick up a value by accident.</para>
///
/// <para>The actor's Entra <c>(tid, oid)</c> pair <em>is</em> recorded, because an audit trail that
/// cannot say who made a change is not an audit trail; those are identifiers rather than
/// credentials, and they are the same pair the platform diagnostics audit records for the same
/// reason (see <c>Inventory.Application.PlatformDiagnostics.PlatformDiagnosticsAuditEntry</c>).</para>
/// </summary>
/// <param name="ActorDirectoryTenantId">The acting Owner's Entra <c>tid</c>.</param>
/// <param name="ActorObjectId">The acting Owner's Entra <c>oid</c>.</param>
/// <param name="BusinessId">
/// The business whose settings changed, resolved from that actor's membership and never from
/// request input.
/// </param>
/// <param name="OccurredAtUtc">When the change was stored, from the application clock.</param>
/// <param name="ChangedFields">
/// Which settings changed, in declaration order. Never empty: a save that changed nothing is not
/// audited.
/// </param>
public sealed record BusinessSettingsAuditEntry(
    string ActorDirectoryTenantId,
    string ActorObjectId,
    int BusinessId,
    DateTime OccurredAtUtc,
    IReadOnlyList<BusinessSettingsField> ChangedFields);

/// <summary>
/// The audit sink for business-settings changes, implemented at the InventoryApi boundary over
/// <c>ILogger</c> (issue #548).
///
/// It is a log port rather than a database table for the reason the platform diagnostics audit is
/// (<c>Inventory.Application.PlatformDiagnostics.IPlatformDiagnosticsAudit</c>): an audit row in the
/// same database as the record it is evidence about needs a migration, an owner for a
/// non-tenant-owned table, and a retention policy this application cannot assert. The structured
/// log event goes to the platform's log pipeline instead, whose destination and retention are
/// platform configuration a human verifies.
/// </summary>
public interface IBusinessSettingsAudit
{
    void Record(BusinessSettingsAuditEntry entry);
}
