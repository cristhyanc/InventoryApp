using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.PlatformAdmin;

/// <summary>
/// Approves a business application (issue #544, one slice of #509 under #328): a
/// <see cref="BusinessStatus.Pending"/> business becomes <see cref="BusinessStatus.Active"/>, which
/// is the one status that resolves, so its Owner's very next request stops being refused and starts
/// being served.
///
/// <para>It is the first platform-admin operation that writes. What it may change is one business's
/// lifecycle status and decision instant, and - since issue #849 - that same business's own copies of
/// the enabled platform supplier presets that serve its recorded state (issue #935), written through
/// <see cref="IBusinessSupplierProvisioningStore"/> in the same transaction. No membership is
/// created, no role is granted, no other business is reachable, and nothing is stamped with the
/// business id the <em>request</em> carried: the provisioning write is stamped with the key of the
/// business row the approval's own transaction read.</para>
///
/// <para>The rules, the transaction, the eligibility re-check and the audit event are
/// <see cref="BusinessApplicationDecisionWriter"/>'s, shared with the rejection beside it.</para>
/// </summary>
public sealed class ApproveBusinessApplication
{
    private readonly BusinessApplicationDecisionWriter _writer;

    public ApproveBusinessApplication(BusinessApplicationDecisionWriter writer)
    {
        _writer = writer;
    }

    /// <returns>
    /// The decision as it was recorded, or <see langword="null"/> when no such business exists.
    /// </returns>
    /// <exception cref="DomainConflictException">
    /// The application is no longer waiting for a decision, its applicant's membership in it is no
    /// longer active, or that applicant now holds an active membership in another business. Nothing
    /// is committed in any of those cases.
    /// </exception>
    public Task<PlatformBusinessDecision?> Handle(int businessId, CancellationToken cancellationToken) =>
        _writer.DecideAsync(businessId, approved: true, reason: null, cancellationToken);
}
