using Inventory.Application.Access;
using Inventory.Application.Businesses;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Gst;
using Xunit;

namespace InventoryApi.Tests.Application.Access;

/// <summary>
/// The use case behind <c>GET /api/me/access</c> (issue #521), without ASP.NET Core or EF Core.
///
/// What it has to show is where each half of the answer comes from: the role from the caller's
/// resolved membership, the name and zone from the business that membership resolved to, and
/// nothing at all from a caller-supplied value - the use case takes no argument but a cancellation
/// token.
/// </summary>
public class GetCurrentAccessTests
{
    private const int BusinessId = 7;

    private static GetCurrentAccess Create(
        ICurrentBusinessProvider currentBusiness,
        IBusinessProfileStore? store = null) =>
        new(currentBusiness, new GetCurrentBusiness(currentBusiness, store ?? new FakeBusinessProfileStore()));

    [Theory]
    [InlineData(BusinessRole.Operator)]
    [InlineData(BusinessRole.Manager)]
    [InlineData(BusinessRole.Owner)]
    public async Task Reports_the_callers_role_with_exactly_that_roles_capabilities(BusinessRole role)
    {
        var access = await Create(new FakeCurrentBusinessProvider(BusinessId, role))
            .Handle(CancellationToken.None);

        Assert.Equal(role, access.Role);
        Assert.Equal(RoleCapabilities.For(role), access.Capabilities);
    }

    [Fact]
    public async Task Reports_the_name_and_time_zone_of_the_business_the_membership_resolved_to()
    {
        var store = new FakeBusinessProfileStore(new BusinessProfile("Vending Co", "Australia/Sydney"));

        var access = await Create(new FakeCurrentBusinessProvider(BusinessId), store).Handle(CancellationToken.None);

        Assert.Equal("Vending Co", access.BusinessName);
        Assert.Equal("Australia/Sydney", access.TimeZoneId);
        Assert.Equal(Inventory.Domain.Tenancy.BusinessId.From(BusinessId), store.LastRequestedBusinessId);
    }

    /// <summary>
    /// A caller with no usable membership has no access to describe, and must be told nothing -
    /// not an empty capability list, which a client could read as "signed in with no permissions"
    /// rather than "not a member of anything".
    /// </summary>
    [Theory]
    [InlineData(BusinessAccessDenialReason.MembershipMissing)]
    [InlineData(BusinessAccessDenialReason.MembershipAmbiguous)]
    [InlineData(BusinessAccessDenialReason.BusinessInactive)]
    [InlineData(BusinessAccessDenialReason.RoleUnrecognised)]
    public async Task A_caller_with_no_usable_membership_is_denied(BusinessAccessDenialReason reason)
    {
        var store = new FakeBusinessProfileStore();

        var exception = await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => Create(FakeCurrentBusinessProvider.Denied(reason), store).Handle(CancellationToken.None));

        Assert.Equal(reason, exception.Reason);
        Assert.Null(store.LastRequestedBusinessId);
    }

    /// <summary>
    /// A resolved membership pointing at a business row that is gone is a broken tenancy state,
    /// and it fails closed: no role, no capability list and no substituted business.
    /// </summary>
    [Fact]
    public async Task A_resolved_business_that_no_longer_exists_fails_closed()
    {
        await Assert.ThrowsAsync<DomainConflictException>(
            () => Create(new FakeCurrentBusinessProvider(BusinessId), new FakeBusinessProfileStore(profile: null))
                .Handle(CancellationToken.None));
    }

    private sealed class FakeBusinessProfileStore : IBusinessProfileStore
    {
        private readonly BusinessProfile? _profile;

        public FakeBusinessProfileStore(BusinessProfile? profile) => _profile = profile;

        public FakeBusinessProfileStore()
            : this(new BusinessProfile("Vending Co", "Australia/Sydney"))
        {
        }

        public BusinessId? LastRequestedBusinessId { get; private set; }

        public Task<BusinessProfile?> FindAsync(BusinessId businessId, CancellationToken cancellationToken)
        {
            LastRequestedBusinessId = businessId;
            return Task.FromResult(_profile);
        }
    }
}
