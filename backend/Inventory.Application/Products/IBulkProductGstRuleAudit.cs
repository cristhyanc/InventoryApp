using Inventory.Domain.Gst;

namespace Inventory.Application.Products;

/// <summary>
/// One audit event about one committed bulk GST rule change (issue #1076).
///
/// <para>It follows the convention <c>Inventory.Application.Businesses.BusinessSettingsAuditEntry</c>
/// established for a business's own configuration changes: the acting member's Entra
/// <c>(tid, oid)</c> pair, the business the change was made in, the instant, and a closed
/// description of <em>what</em> changed. Those identifiers are recorded because an audit trail that
/// cannot say who changed what, and where, is not one; neither of them is a credential.</para>
///
/// <para><strong>What it deliberately does not carry.</strong> No product id, product name, SKU,
/// category, price, cost or stock figure, and no list of the products that changed - the event
/// records the rule that was configured and how many products took it, which is what an
/// investigation into "who set every product to Taxable" needs. There is no field for a product of
/// any kind, so a sink that printed the whole entry could not emit one.</para>
/// </summary>
/// <param name="ActorDirectoryTenantId">The acting member's Entra <c>tid</c>.</param>
/// <param name="ActorObjectId">Their Entra <c>oid</c>.</param>
/// <param name="BusinessId">
/// The business whose products were configured, resolved from that actor's membership and never
/// from request input.
/// </param>
/// <param name="TargetRule">
/// The rule that was set - one of <see cref="GstClassification.Taxable"/> and
/// <see cref="GstClassification.GstFree"/>, the only two
/// <see cref="BulkProductGstRulePolicy.RequireTarget"/> accepts.
/// </param>
/// <param name="ChangedCount">
/// How many products were configured, as the committed write reported it. Never zero: a change of
/// nothing is not audited, because the event is evidence about a change rather than about an
/// attempt.
/// </param>
/// <param name="OccurredAtUtc">When the change was stored, from the application clock.</param>
public sealed record BulkProductGstRuleAuditEntry(
    string ActorDirectoryTenantId,
    string ActorObjectId,
    int BusinessId,
    GstClassification TargetRule,
    int ChangedCount,
    DateTime OccurredAtUtc);

/// <summary>
/// The audit sink for bulk product GST rule changes, implemented at the InventoryApi boundary over
/// <c>ILogger</c> (issue #1076).
///
/// It is a log port rather than a database table for the reason
/// <c>Inventory.Application.Businesses.IBusinessSettingsAudit</c> is: an audit row in the same
/// database as the records it is evidence about needs a migration, an owner for a non-tenant-owned
/// table and a retention policy this application cannot assert, and this issue authorises no schema
/// change at all. The structured log event goes to the platform's log pipeline instead, whose
/// destination and retention are platform configuration a human verifies.
/// </summary>
public interface IBulkProductGstRuleAudit
{
    void Record(BulkProductGstRuleAuditEntry entry);
}
