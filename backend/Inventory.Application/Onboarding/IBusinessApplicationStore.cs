using Inventory.Domain.Tenancy;

namespace Inventory.Application.Onboarding;

/// <summary>
/// One business application as it stands (issue #541): the business row the application *is*, the
/// details it recorded, where it sits in its lifecycle, and when it was submitted.
///
/// <para>There is deliberately no separate application entity. An application and the business it
/// becomes are one record from the moment it is submitted - a <see cref="BusinessStatus.Pending"/>
/// business - which is what issue #540's lifecycle status made possible and why this slice adds no
/// table and no migration. Approving one does not copy anything anywhere; it changes a status.</para>
///
/// <para>It carries the <see cref="BusinessId"/> because the audit event needs one and because the
/// resubmission comparison has to read the right row. That id never reaches a response: like every
/// other self-describing response here - the invite redemption's role, the account state, the
/// business settings - the applicant is told about their own application and not handed the tenant
/// key (AGENTS.md § Tenant ownership).</para>
/// </summary>
/// <param name="BusinessId">The business this application is, from the row itself.</param>
/// <param name="Settings">What the application recorded, exactly as stored.</param>
/// <param name="Status">The business's lifecycle status, exactly as stored.</param>
/// <param name="AppliedAtUtc">
/// When the application was submitted, and <see langword="null"/> for a business that never went
/// through one - the existing business and anything <c>bootstrap-business</c> adopted (issue #540).
/// It is reported as it is stored rather than derived from the creation instant, because a business
/// record that predates the form did not apply for anything.
/// </param>
public sealed record BusinessApplication(
    BusinessId BusinessId,
    BusinessSettings Settings,
    BusinessStatus Status,
    DateTime? AppliedAtUtc);

/// <summary>
/// The narrow persistence port the self-service business application runs on (issue #541). It
/// touches two tables and nothing else: businesses and memberships.
///
/// <para><strong>Why it is a port of its own.</strong> It is the second path into this application
/// reached by a caller with no business at all, after the invite redemption, so it cannot use
/// <see cref="Businesses.IBusinessSettingsStore"/> or <c>IBusinessMemberStore</c>, whose every
/// method takes a business id tenancy resolution produced. Here there is no business to resolve -
/// the whole point is that one is being created - and for a resubmission the id comes from the
/// caller's own membership row. Keeping that in one small port means the whole of what a
/// business-less caller can reach stays one reviewable surface.</para>
///
/// <para><strong>Every call is made inside issue #522's shared write transaction</strong>, through
/// the request's own <c>AppDbContext</c>, so <c>MembershipWriteGuard</c>'s re-checks see what was
/// written and a refusal rolls the business and the membership back together.</para>
///
/// <para><strong>Nothing tenant-owned is reachable through it.</strong> The request carries a
/// <em>denied</em> <c>BusinessScope</c>, so the query filters and <c>BusinessOwnershipEnforcer</c>
/// stay fully in force; the two tables here are the structurally non-tenant-owned ones
/// (docs/architecture.md § Tenant ownership), which is exactly why they are reachable and why no
/// product, purchase, sale or document is.</para>
/// </summary>
public interface IBusinessApplicationStore
{
    /// <summary>
    /// The application <paramref name="businessId"/> records, or <see langword="null"/> when no such
    /// business row exists.
    ///
    /// <para>The id is only ever one the caller's own membership row supplied, never a value from
    /// the request: it is how a resubmission is compared against the application that is already
    /// waiting.</para>
    /// </summary>
    Task<BusinessApplication?> FindAsync(BusinessId businessId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the <see cref="BusinessStatus.Pending"/> business and the applicant's own active
    /// Owner membership, as one save.
    ///
    /// <para>The business starts empty: no machine, product, purchase, sale, site or Nayax
    /// connection record, so its Nayax status is <c>NotConfigured</c> by virtue of having nothing
    /// stored. No decision instant and no rejection reason are written - a decision has not been
    /// made - and the status is stated explicitly, as issue #584 requires of every path that
    /// creates a business.</para>
    ///
    /// <para>The membership is the applicant's own: the identity comes from their validated token
    /// and the profile is the display-only name and email address the same token reported (issue
    /// #524), either half of which may be absent and is then stored as nothing rather than as a
    /// placeholder. The instant is recorded as the application instant, the business's creation
    /// instant and the membership's creation and state-change instant alike, so no row describes a
    /// state it reached at some other time.</para>
    /// </summary>
    Task<BusinessApplication> CreateAsync(
        BusinessSettings settings,
        ActorIdentity identity,
        MemberProfile profile,
        DateTime appliedAtUtc,
        CancellationToken cancellationToken);
}
