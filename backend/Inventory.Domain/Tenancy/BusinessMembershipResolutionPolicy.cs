namespace Inventory.Domain.Tenancy;

/// <summary>
/// The one authoritative rule mapping an authenticated actor's membership records to its current
/// business (issue #64).
///
/// The rule fails closed in every direction: no membership, only revoked memberships, more than
/// one active membership (duplicate rows or two businesses), and an inactive owning business all
/// deny access. Nothing here picks a "first" or "default" business, because an unattended wrong
/// guess would silently expose another business's financial data.
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

        return single.BusinessIsActive
            ? BusinessMembershipResolution.Resolved(single.BusinessId)
            : BusinessMembershipResolution.Denied(BusinessAccessDenialReason.BusinessInactive);
    }
}
