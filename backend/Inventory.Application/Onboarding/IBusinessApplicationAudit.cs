namespace Inventory.Application.Onboarding;

/// <summary>
/// One audit event about one committed business application (issue #541): who applied, and which
/// business the application created.
///
/// <para>It is emitted only after the application is committed, for the reason every audit entry
/// here is: a refused application did not happen, and an audit trail that claimed otherwise would
/// be worse than none. A resubmission that returned the application the caller already had is not
/// audited either - nothing was created, and recording a second event for one application would
/// make the trail answer "how many businesses were created?" wrongly.</para>
///
/// <para><strong>What it deliberately does not carry.</strong> The business <em>name</em>, the
/// contact email address and the applicant's own display name and email address - the issue's "no
/// email or name logged", and the standing rule of issue #524. The business name is a value a
/// stranger typed into a public form, the contact address is personal data about a party that has
/// not been approved yet, and neither identifies anything an investigation needs: the actor's Entra
/// <c>(tid, oid)</c> pair and the new business id do, and those are identifiers rather than
/// credentials or personal detail. The ABN and the GST registration are absent for the same reason
/// - they are tax facts about the applicant, and the trail records that a business was created, not
/// what was claimed about it.</para>
/// </summary>
/// <param name="ActorDirectoryTenantId">The applicant's Entra <c>tid</c>.</param>
/// <param name="ActorObjectId">The applicant's Entra <c>oid</c>.</param>
/// <param name="BusinessId">The business the application created.</param>
/// <param name="OccurredAtUtc">When the application was committed, from the application clock.</param>
public sealed record BusinessApplicationAuditEntry(
    string ActorDirectoryTenantId,
    string ActorObjectId,
    int BusinessId,
    DateTime OccurredAtUtc);

/// <summary>
/// The audit sink for business applications, implemented at the InventoryApi boundary over
/// <c>ILogger</c> (issue #541), for the same reasons as <c>IMemberInviteAudit</c>: the event carries
/// the request's correlation id, which is a transport fact, and an audit row in the same database
/// the change was made in would be evidence kept inside the thing it is evidence about - here, in a
/// tenant that has not been approved yet.
/// </summary>
public interface IBusinessApplicationAudit
{
    void Record(BusinessApplicationAuditEntry entry);
}
