namespace Inventory.Domain.Tenancy;

/// <summary>
/// The outcome of resolving an authenticated actor to its current business. Either a business
/// was resolved or access is denied with a reason; there is no third, "unknown but proceed"
/// state.
/// </summary>
public sealed record BusinessMembershipResolution
{
    private BusinessMembershipResolution(BusinessId? businessId, BusinessAccessDenialReason? denialReason)
    {
        ResolvedBusinessId = businessId;
        DenialReason = denialReason;
    }

    /// <summary>The resolved business, or <c>null</c> when access is denied.</summary>
    public BusinessId? ResolvedBusinessId { get; }

    /// <summary>Why access was denied, or <c>null</c> when a business was resolved.</summary>
    public BusinessAccessDenialReason? DenialReason { get; }

    public bool IsResolved => ResolvedBusinessId.HasValue;

    public static BusinessMembershipResolution Resolved(BusinessId businessId) => new(businessId, null);

    public static BusinessMembershipResolution Denied(BusinessAccessDenialReason reason) => new(null, reason);
}
