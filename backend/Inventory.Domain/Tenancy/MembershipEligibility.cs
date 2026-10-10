namespace Inventory.Domain.Tenancy;

/// <summary>
/// One membership row of one identity, as <see cref="MembershipEligibility"/> sees it (issue
/// #522).
///
/// It deliberately carries no business id and no role. The rule is about the identity, not about
/// which business it is a member of, and an answer that named the blocking business would hand the
/// caller the one piece of information the tenant boundary exists to withhold: that this person
/// belongs to some other business, and which one.
/// </summary>
/// <param name="MembershipId">The membership's own persisted key, which is what an exclusion names.</param>
/// <param name="IsActive">The membership's own state, exactly as persistence read it.</param>
public readonly record struct IdentityMembership(int MembershipId, bool IsActive);

/// <summary>
/// The one rule deciding whether an identity may gain or regain an active business membership
/// (issue #522, one slice of #501 under #328): only while it holds no other active membership, in
/// any business.
///
/// A person belongs to one business at a time. That is not a convenience: the whole tenant
/// boundary resolves a request to exactly one business from exactly one active membership
/// (<see cref="BusinessMembershipResolutionPolicy"/>), so a second active membership denies that
/// person every business rather than offering a choice. Enforcing it when the membership is
/// granted, instead of when it is used, is the difference between refusing one write and locking
/// somebody out of two businesses at their next sign-in.
///
/// "Any business" means any: an active one, a Pending one and a Deactivated one alike. This rule
/// therefore never reads the owning business's state - a membership in a business nobody can
/// currently reach still occupies the identity's one active membership, and has to be revoked
/// deliberately before that person joins somewhere else. A future business status cannot quietly
/// create an exception to the rule, because there is nothing here for it to be an exception to.
///
/// The rule is only as complete as the memberships it is given: the caller must pass every
/// membership recorded for the identity, across every business, active or not, read inside the
/// same write transaction as the change it is about to make. The filtered unique index on the
/// active rows is what makes that a guarantee rather than a hope.
/// </summary>
public static class MembershipEligibility
{
    /// <summary>
    /// The one message a refusal is reported with, written for the caller and answering a
    /// question about one person's own membership. It names no business, no role and no other
    /// member, because the person being told this may be entitled to know none of them.
    /// </summary>
    public const string AlreadyAMemberMessage = "This person is already a member of a business.";

    /// <summary>
    /// Whether the identity those <paramref name="memberships"/> belong to may hold an active
    /// membership.
    /// </summary>
    /// <param name="memberships">
    /// Every membership recorded for the one identity, in every business, active or not. An empty
    /// or <c>null</c> collection is an identity with no membership at all, which is eligible.
    /// </param>
    /// <param name="excludedMembershipId">
    /// The membership the operation is itself about, left out of the count. An operation on an
    /// already-active membership - approving an applicant's own membership (#509), changing a
    /// member's role (#504) - must not be refused because that same row is active: it would be
    /// counting the membership against itself. It is an explicit argument rather than something
    /// inferred, so excluding a row is always a decision the caller made.
    /// </param>
    public static bool AllowsActiveMembership(
        IReadOnlyCollection<IdentityMembership>? memberships,
        int? excludedMembershipId = null) =>
        memberships is null
        || !memberships.Any(membership =>
            membership.IsActive && membership.MembershipId != excludedMembershipId);
}
