namespace Inventory.Domain.Tenancy;

/// <summary>
/// The outcome of resolving an authenticated actor to its current business. Either a business and
/// the role the actor holds in it were resolved, or access is denied with a reason; there is no
/// third, "unknown but proceed" state.
/// </summary>
public sealed record BusinessMembershipResolution
{
    private BusinessMembershipResolution(
        BusinessId? businessId,
        BusinessRole? role,
        BusinessAccessDenialReason? denialReason)
    {
        ResolvedBusinessId = businessId;
        ResolvedRole = role;
        DenialReason = denialReason;
    }

    /// <summary>The resolved business, or <c>null</c> when access is denied.</summary>
    public BusinessId? ResolvedBusinessId { get; }

    /// <summary>
    /// The role the actor holds in the resolved business (issue #521), or <c>null</c> when access
    /// is denied. A resolved outcome always carries a declared role: an unrecognised stored role
    /// is a denial, never a resolution with an unknown role attached.
    /// </summary>
    public BusinessRole? ResolvedRole { get; }

    /// <summary>Why access was denied, or <c>null</c> when a business was resolved.</summary>
    public BusinessAccessDenialReason? DenialReason { get; }

    public bool IsResolved => ResolvedBusinessId.HasValue;

    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="role"/> is not a declared <see cref="BusinessRole"/>. Resolving a business
    /// with a role no capability table describes would publish access nobody granted, so it is
    /// refused at construction rather than carried forward.
    /// </exception>
    public static BusinessMembershipResolution Resolved(BusinessId businessId, BusinessRole role) =>
        new(businessId, BusinessRoles.Require(role), null);

    public static BusinessMembershipResolution Denied(BusinessAccessDenialReason reason) =>
        new(null, null, reason);
}
