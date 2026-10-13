#nullable enable

using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Tests.Application.Gst;

/// <summary>
/// A current-business provider that resolves to a fixed business, or denies. It exists so a use-case
/// test can show that the business a preview is bound to comes from the authenticated actor's
/// membership and never from a request, and that an unresolved caller reads and writes nothing.
///
/// <para><see cref="SupportSession"/> is the third state the real provider has since issue #551: no
/// membership resolves at all - the platform administrator is a member of nothing, so there is no
/// role either - but the business the session was granted for is answered, which is what lets a
/// read-only support request act in that business.</para>
/// </summary>
public sealed class FakeCurrentBusinessProvider : ICurrentBusinessProvider
{
    private readonly BusinessMembershipResolution _resolution;
    private readonly BusinessId? _supportSessionBusinessId;

    public FakeCurrentBusinessProvider(int businessId, BusinessRole role = BusinessRole.Owner) =>
        _resolution = BusinessMembershipResolution.Resolved(BusinessId.From(businessId), role);

    private FakeCurrentBusinessProvider(
        BusinessAccessDenialReason reason,
        BusinessId? supportSessionBusinessId = null)
    {
        _resolution = BusinessMembershipResolution.Denied(reason);
        _supportSessionBusinessId = supportSessionBusinessId;
    }

    public static FakeCurrentBusinessProvider Denied(
        BusinessAccessDenialReason reason = BusinessAccessDenialReason.MembershipMissing) => new(reason);

    /// <summary>A request reading <paramref name="businessId"/> through a support session.</summary>
    public static FakeCurrentBusinessProvider SupportSession(int businessId) =>
        new(BusinessAccessDenialReason.MembershipMissing, BusinessId.From(businessId));

    public Task<BusinessMembershipResolution> ResolveAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_resolution);

    public Task<BusinessId> RequireBusinessIdAsync(CancellationToken cancellationToken) =>
        (_supportSessionBusinessId ?? _resolution.ResolvedBusinessId) is { } businessId
            ? Task.FromResult(businessId)
            : throw new BusinessAccessDeniedException(
                _resolution.DenialReason ?? BusinessAccessDenialReason.UnidentifiableActor);

    public Task<BusinessRole> RequireRoleAsync(CancellationToken cancellationToken) =>
        _resolution.ResolvedRole is { } role
            ? Task.FromResult(role)
            : throw new BusinessAccessDeniedException(
                _resolution.DenialReason ?? BusinessAccessDenialReason.UnidentifiableActor);
}
