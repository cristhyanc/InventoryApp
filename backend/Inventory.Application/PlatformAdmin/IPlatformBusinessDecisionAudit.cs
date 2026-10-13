using Inventory.Domain.Tenancy;

namespace Inventory.Application.PlatformAdmin;

/// <summary>
/// One audit event about one committed platform decision on one business (issue #544): who decided,
/// which business, which status it moved from and to, and whether a reason was recorded.
///
/// <para>It is emitted only after the decision is committed, for the reason every audit entry here
/// is: a refused decision did not happen, and an audit trail that claimed otherwise would be worse
/// than none. This is the first platform-admin event about a <em>write</em>, which is exactly why it
/// is not optional - the diagnostics API audits every read of every business, and a path that can
/// start or refuse a tenant must be at least as traceable.</para>
///
/// <para><strong>What it deliberately does not carry.</strong> The business <em>name</em>, the
/// contact email address, the ABN, the applicant's identity, and the rejection <em>text</em> - the
/// issue's "no email or names logged". The name and the contact address are detail about a party
/// whose application may just have been refused, the rejection text is an operator's note written
/// for one applicant to read, and none of them identifies anything an investigation needs: the
/// actor's Entra <c>(tid, oid)</c> pair, the business id and the status transition do, and those are
/// identifiers rather than personal detail. <see cref="ReasonRecorded"/> is a flag for the same
/// reason - "was a reason given?" is an answerable question about the decision, while the sentence
/// itself is not the trail's business.</para>
/// </summary>
/// <param name="ActorDirectoryTenantId">The deciding platform administrator's Entra <c>tid</c>.</param>
/// <param name="ActorObjectId">Their Entra <c>oid</c>.</param>
/// <param name="BusinessId">The business that was decided.</param>
/// <param name="FromStatus">
/// The status the business held before the decision, exactly as it was stored. Always
/// <see cref="BusinessStatus.Pending"/> today, because nothing else is decidable; it is recorded
/// rather than assumed so the trail stays truthful if that ever changes.
/// </param>
/// <param name="ToStatus">The status it holds now - <c>Active</c> or <c>Rejected</c>.</param>
/// <param name="ReasonRecorded">Whether a rejection reason was stored. Never the reason itself.</param>
/// <param name="OccurredAtUtc">When the decision was committed, from the application clock.</param>
public sealed record PlatformBusinessDecisionAuditEntry(
    string ActorDirectoryTenantId,
    string ActorObjectId,
    int BusinessId,
    BusinessStatus FromStatus,
    BusinessStatus ToStatus,
    bool ReasonRecorded,
    DateTime OccurredAtUtc);

/// <summary>
/// The audit sink for platform decisions on businesses, implemented at the InventoryApi boundary
/// over <c>ILogger</c> (issue #544), for the same reasons as <c>IPlatformDiagnosticsAudit</c> and
/// <c>IBusinessApplicationAudit</c>: the event carries the request's correlation id, which is a
/// transport fact, and an audit row in the same database the change was made in would be evidence
/// kept inside the thing it is evidence about.
/// </summary>
public interface IPlatformBusinessDecisionAudit
{
    void Record(PlatformBusinessDecisionAuditEntry entry);
}
