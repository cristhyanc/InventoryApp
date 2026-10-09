#nullable enable

using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Tests.Application.Gst;

/// <summary>
/// A current-business provider that resolves to a fixed business, or denies. It exists so a use-case
/// test can show that the business a preview is bound to comes from the authenticated actor's
/// membership and never from a request, and that an unresolved caller reads and writes nothing.
/// </summary>
public sealed class FakeCurrentBusinessProvider : ICurrentBusinessProvider
{
    private readonly BusinessMembershipResolution _resolution;

    public FakeCurrentBusinessProvider(int businessId) =>
        _resolution = BusinessMembershipResolution.Resolved(BusinessId.From(businessId));

    private FakeCurrentBusinessProvider(BusinessAccessDenialReason reason) =>
        _resolution = BusinessMembershipResolution.Denied(reason);

    public static FakeCurrentBusinessProvider Denied(
        BusinessAccessDenialReason reason = BusinessAccessDenialReason.MembershipMissing) => new(reason);

    public Task<BusinessMembershipResolution> ResolveAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_resolution);

    public Task<BusinessId> RequireBusinessIdAsync(CancellationToken cancellationToken) =>
        _resolution.ResolvedBusinessId is { } businessId
            ? Task.FromResult(businessId)
            : throw new BusinessAccessDeniedException(
                _resolution.DenialReason ?? BusinessAccessDenialReason.UnidentifiableActor);
}
