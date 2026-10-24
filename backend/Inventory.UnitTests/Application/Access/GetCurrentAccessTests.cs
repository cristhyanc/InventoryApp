using Inventory.Application.Access;
using Inventory.Application.Businesses;
using Inventory.Application.PlatformOptions;
using Inventory.Application.SupportSessions;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.PlatformOptions;
using InventoryApi.Tests.Application.SupportSessions;
using Xunit;

namespace InventoryApi.Tests.Application.Access;

/// <summary>
/// The use case behind <c>GET /api/me/access</c> (issue #521, extended by issue #551), without
/// ASP.NET Core or EF Core.
///
/// What it has to show is where each part of the answer comes from: for a member, the role from
/// their resolved membership and that role's capabilities; for a platform administrator in a
/// support session, no role at all and the session's own read-only set; the name and zone from the
/// business the request acts in either way; and nothing at all from a caller-supplied value - the
/// use case takes no argument but a cancellation token.
/// </summary>
public class GetCurrentAccessTests
{
    private const int BusinessId = 7;

    private static GetCurrentAccess Create(
        ICurrentBusinessProvider currentBusiness,
        IBusinessProfileStore? store = null,
        ISupportSessionAccessor? supportSession = null,
        IOptionVisibilityStore? options = null)
    {
        var session = supportSession ?? new FakeSupportSessionAccessor();

        return new(
            currentBusiness,
            new CurrentCapabilities(session, currentBusiness, new FakeRolePreviewAccessor()),
            session,
            new GetCurrentBusiness(currentBusiness, store ?? new FakeBusinessProfileStore()),
            new GetOptionVisibility(options ?? new FakeOptionVisibilityStore()));
    }

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
        Assert.Null(access.SupportSession);
    }

    /// <summary>
    /// The criterion: in a support session the answer is that session's exact capability set, no
    /// role at all, and the session block saying whose data it is and when it runs out.
    /// </summary>
    [Fact]
    public async Task In_a_support_session_reports_the_support_set_no_role_and_the_session()
    {
        var expiresAt = new DateTime(2026, 10, 13, 9, 30, 0, DateTimeKind.Utc);

        var access = await Create(
                FakeCurrentBusinessProvider.SupportSession(BusinessId),
                new FakeBusinessProfileStore(
                    new BusinessProfile("Vending Co", "Australia/Sydney", BusinessStatus.Active)),
                FakeSupportSessionAccessor.Active(BusinessId, expiresAtUtc: expiresAt))
            .Handle(CancellationToken.None);

        Assert.Null(access.Role);
        Assert.Equal(SupportSessionAccessPolicy.Capabilities, access.Capabilities);
        Assert.Equal("Vending Co", access.BusinessName);
        Assert.Equal(new CurrentSupportSession("Vending Co", expiresAt, ReadOnly: true), access.SupportSession);
    }

    /// <summary>
    /// The two exclusions, asserted as absences rather than implied by the set above: the GST
    /// defaults guard only an edit, and the members list carries people's names and addresses.
    /// </summary>
    [Fact]
    public async Task A_support_session_reports_neither_member_management_nor_gst_defaults()
    {
        var access = await Create(
                FakeCurrentBusinessProvider.SupportSession(BusinessId),
                supportSession: FakeSupportSessionAccessor.Active(BusinessId))
            .Handle(CancellationToken.None);

        Assert.DoesNotContain(Capability.MembersManage, access.Capabilities);
        Assert.DoesNotContain(Capability.SuppliersManageGstDefaults, access.Capabilities);
    }

    [Fact]
    public async Task Reports_the_name_and_time_zone_of_the_business_the_membership_resolved_to()
    {
        var store = new FakeBusinessProfileStore(
            new BusinessProfile("Vending Co", "Australia/Sydney", BusinessStatus.Active));

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
    /// The switchable option's state travels with the answer (issue #873), and it changes nothing
    /// about the capabilities: the same Owner is reported with exactly the Owner capability set
    /// whether the option is offered or switched off. That is the whole distinction the issue rests
    /// on - switching it off hides an entry and grants or revokes nothing.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reports_the_shared_option_visibility_without_changing_any_capability(bool visible)
    {
        var access = await Create(
                new FakeCurrentBusinessProvider(BusinessId, BusinessRole.Owner),
                options: FakeOptionVisibilityStore.Holding(visible))
            .Handle(CancellationToken.None);

        Assert.Equal(visible, access.HistoricalGstClassificationVisible);
        Assert.Equal(RoleCapabilities.For(BusinessRole.Owner), access.Capabilities);
        Assert.Contains(Capability.DataRepair, access.Capabilities);
    }

    /// <summary>
    /// An installation that has never switched anything offers the option, which is what every
    /// existing deployment gets on the day this arrives.
    /// </summary>
    [Fact]
    public async Task Reports_the_option_as_offered_when_nothing_has_ever_been_switched()
    {
        var access = await Create(new FakeCurrentBusinessProvider(BusinessId, BusinessRole.Operator))
            .Handle(CancellationToken.None);

        Assert.True(access.HistoricalGstClassificationVisible);
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
            : this(new BusinessProfile("Vending Co", "Australia/Sydney", BusinessStatus.Active))
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
