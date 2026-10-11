namespace Inventory.Domain.Tenancy;

/// <summary>
/// The one authoritative rule mapping an authenticated actor's membership records to its current
/// business (issue #64).
///
/// The rule fails closed in every direction: no membership, only revoked memberships, more than
/// one active membership (duplicate rows or two businesses), an inactive owning business, and a
/// stored role this code does not declare (issue #521) all deny access. Nothing here picks a
/// "first" or "default" business, and nothing picks a default role, because an unattended wrong
/// guess would silently expose another business's financial data or grant access nobody recorded.
/// </summary>
public static class BusinessMembershipResolutionPolicy
{
    public static BusinessMembershipResolution Resolve(IReadOnlyCollection<ActorBusinessMembership>? memberships)
    {
        if (memberships is null || memberships.Count == 0)
        {
            return BusinessMembershipResolution.Denied(BusinessAccessDenialReason.MembershipMissing);
        }

        // Revoked memberships are removed before counting, so a single active membership still
        // resolves after an old one is deactivated instead of being reported as ambiguous.
        var active = memberships.Where(membership => membership.IsActive).ToList();

        if (active.Count == 0)
        {
            return BusinessMembershipResolution.Denied(BusinessAccessDenialReason.MembershipInactive);
        }

        if (active.Count > 1)
        {
            return BusinessMembershipResolution.Denied(BusinessAccessDenialReason.MembershipAmbiguous);
        }

        var single = active[0];

        if (!single.BusinessIsActive)
        {
            return BusinessMembershipResolution.Denied(BusinessAccessDenialReason.BusinessInactive);
        }

        // The role is checked last, and only for the membership that would otherwise have
        // resolved: an actor who is already denied for a membership reason must keep being denied
        // for that reason, so a stored role never changes which problem an operator is shown.
        return BusinessRoles.IsSupported(single.Role)
            ? BusinessMembershipResolution.Resolved(single.BusinessId, single.Role)
            : BusinessMembershipResolution.Denied(BusinessAccessDenialReason.RoleUnrecognised);
    }
}
