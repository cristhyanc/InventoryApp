namespace Inventory.Application.Commissions;

/// <summary>
/// One audit event about one committed end-date change to a site commission agreement
/// (issue #1254).
///
/// <para>It records who ended the agreement, in which business, which agreement and site it was, and
/// the old and new end date - which is the whole of what the issue asks the trail to answer, because
/// commission is calculated on read: bringing an end date into the past changes what every later
/// report says the site is owed from that date on, and "who decided that, and when, and what did it
/// used to be" is the question an investigation starts from.</para>
///
/// <para>It is emitted only after the change is committed. A refused or rolled-back change did not
/// happen, and an audit trail that claimed otherwise would be worse than none.</para>
///
/// <para>What it deliberately does <em>not</em> carry is the site's name, the commission rate or any
/// money figure. The site id names the site within the business the event already names, and a rate
/// is configuration this event did not change - the trail exists to say what was decided, not to
/// become a second copy of the business's financial configuration. The actor's Entra
/// <c>(tid, oid)</c> pair <em>is</em> recorded, because an audit trail that cannot say who made a
/// change is not an audit trail, and those are identifiers rather than credentials.</para>
/// </summary>
/// <param name="ActorDirectoryTenantId">The acting Owner's or Manager's Entra <c>tid</c>.</param>
/// <param name="ActorObjectId">The acting Owner's or Manager's Entra <c>oid</c>.</param>
/// <param name="BusinessId">
/// The business the agreement belongs to, resolved from that actor's membership and never from
/// request input.
/// </param>
/// <param name="AgreementId">The agreement that was ended.</param>
/// <param name="SiteId">The site that agreement covers.</param>
/// <param name="PreviousEndDate">
/// The end date the agreement carried before, or <see langword="null"/> when it was open-ended.
/// </param>
/// <param name="NewEndDate">The end date it carries now.</param>
/// <param name="OccurredAtUtc">
/// When the change was committed, sampled from the application clock with the write lock held, so
/// the trail describes the change rather than the wait that preceded it.
/// </param>
public sealed record SiteCommissionAgreementEndedAuditEntry(
    string ActorDirectoryTenantId,
    string ActorObjectId,
    int BusinessId,
    int AgreementId,
    long SiteId,
    DateTime? PreviousEndDate,
    DateTime NewEndDate,
    DateTime OccurredAtUtc);

/// <summary>
/// The audit sink for commission-agreement end dates, implemented at the InventoryApi boundary over
/// <c>ILogger</c> (issue #1254).
///
/// It is a log port rather than a database table for the reason
/// <see cref="Businesses.IBusinessSettingsAudit"/> and
/// <see cref="Members.IMemberManagementAudit"/> are: an audit row in the same database as the record
/// it is evidence about needs a migration, an owner for a table that is evidence about a tenant's
/// data, and a retention policy this application cannot assert. The structured log event goes to the
/// platform's log pipeline instead, whose destination and retention are platform configuration a
/// human verifies.
/// </summary>
public interface ISiteCommissionAgreementAudit
{
    void Record(SiteCommissionAgreementEndedAuditEntry entry);
}
