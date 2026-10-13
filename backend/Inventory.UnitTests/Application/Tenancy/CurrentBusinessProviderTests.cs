using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Application.Tenancy;

/// <summary>
/// End-to-end resolution of an authenticated Entra actor to an application-owned business
/// (issue #64), across the two ports and the Domain rule, without ASP.NET Core or EF Core.
///
/// The denial tests are the security cases: every one of them must produce no business id at
/// all, and <see cref="ICurrentBusinessProvider.RequireBusinessIdAsync"/> must throw rather than
/// return anything a caller could accidentally treat as scoped.
/// </summary>
public class CurrentBusinessProviderTests
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "22222222-2222-2222-2222-222222222222";

    private static ActorIdentity Actor()
    {
        Assert.True(ActorIdentity.TryCreate(Tid, Oid, out var actor));
        return actor!;
    }

    /// <summary>
    /// The provider under test. The business scope defaults to a fresh, unresolved one, which is
    /// what every membership case below runs with: a denied scope is what a request carries before
    /// this provider's own answer is published into it.
    /// </summary>
    private static CurrentBusinessProvider Provider(
        IAuthenticatedActorAccessor actorAccessor,
        IBusinessMembershipStore membershipStore,
        IBusinessScope? businessScope = null) =>
        new(actorAccessor, membershipStore, businessScope ?? new BusinessScope());

    private static ActorBusinessMembership Membership(
        int businessId,
        bool isActive = true,
        BusinessStatus businessStatus = BusinessStatus.Active,
        BusinessRole role = BusinessRole.Owner) =>
        new(
            BusinessId.From(businessId),
            role,
            isActive,
            businessStatus,
            StatusChangedAtUtc: new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc),
            BusinessRejectionReason: null);

    [Fact]
    public async Task Identified_actor_with_one_active_membership_resolves_to_that_business()
    {
        var store = new FakeBusinessMembershipStore(Membership(7));
        var provider = Provider(FakeAuthenticatedActorAccessor.Identified(Actor()), store);

        var resolution = await provider.ResolveAsync(CancellationToken.None);

        Assert.True(resolution.IsResolved);
        Assert.Equal(BusinessId.From(7), resolution.ResolvedBusinessId);
        Assert.Equal(Actor(), store.LastQueriedActor);
    }

    [Fact]
    public async Task RequireBusinessIdAsync_returns_the_resolved_business()
    {
        var provider = Provider(
            FakeAuthenticatedActorAccessor.Identified(Actor()),
            new FakeBusinessMembershipStore(Membership(7)));

        Assert.Equal(BusinessId.From(7), await provider.RequireBusinessIdAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Unauthenticated_caller_is_denied_without_querying_memberships()
    {
        var store = new FakeBusinessMembershipStore(Membership(7));
        var provider = Provider(FakeAuthenticatedActorAccessor.NotAuthenticated(), store);

        var resolution = await provider.ResolveAsync(CancellationToken.None);

        Assert.False(resolution.IsResolved);
        Assert.Equal(BusinessAccessDenialReason.NotAuthenticated, resolution.DenialReason);
        Assert.Equal(0, store.QueryCount);
    }

    /// <summary>
    /// A validated token without a usable <c>(tid, oid)</c> pair. There is no fallback identity,
    /// so the caller is denied before any membership lookup.
    /// </summary>
    [Fact]
    public async Task Authenticated_but_unidentifiable_actor_is_denied_without_querying_memberships()
    {
        var store = new FakeBusinessMembershipStore(Membership(7));
        var provider = Provider(FakeAuthenticatedActorAccessor.Unidentifiable(), store);

        var resolution = await provider.ResolveAsync(CancellationToken.None);

        Assert.False(resolution.IsResolved);
        Assert.Equal(BusinessAccessDenialReason.UnidentifiableActor, resolution.DenialReason);
        Assert.Equal(0, store.QueryCount);
    }

    [Fact]
    public async Task Actor_with_no_membership_is_denied()
    {
        var provider = Provider(
            FakeAuthenticatedActorAccessor.Identified(Actor()),
            new FakeBusinessMembershipStore());

        var resolution = await provider.ResolveAsync(CancellationToken.None);

        Assert.False(resolution.IsResolved);
        Assert.Equal(BusinessAccessDenialReason.MembershipMissing, resolution.DenialReason);
    }

    [Fact]
    public async Task Actor_with_memberships_in_two_businesses_is_denied_as_ambiguous()
    {
        var provider = Provider(
            FakeAuthenticatedActorAccessor.Identified(Actor()),
            new FakeBusinessMembershipStore(Membership(7), Membership(8)));

        var resolution = await provider.ResolveAsync(CancellationToken.None);

        Assert.False(resolution.IsResolved);
        Assert.Equal(BusinessAccessDenialReason.MembershipAmbiguous, resolution.DenialReason);
    }

    /// <summary>
    /// The role reaches the Application layer from the membership the Domain rule resolved
    /// (issue #521), with nothing in between able to choose or default it.
    /// </summary>
    [Theory]
    [InlineData(BusinessRole.Operator)]
    [InlineData(BusinessRole.Manager)]
    [InlineData(BusinessRole.Owner)]
    public async Task RequireRoleAsync_returns_the_role_the_resolved_membership_carries(BusinessRole role)
    {
        var provider = Provider(
            FakeAuthenticatedActorAccessor.Identified(Actor()),
            new FakeBusinessMembershipStore(Membership(7, role: role)));

        Assert.Equal(role, await provider.RequireRoleAsync(CancellationToken.None));
        Assert.Equal(role, (await provider.ResolveAsync(CancellationToken.None)).ResolvedRole);
    }

    [Theory]
    [InlineData(BusinessAccessDenialReason.NotAuthenticated)]
    [InlineData(BusinessAccessDenialReason.UnidentifiableActor)]
    [InlineData(BusinessAccessDenialReason.MembershipMissing)]
    [InlineData(BusinessAccessDenialReason.MembershipInactive)]
    [InlineData(BusinessAccessDenialReason.MembershipAmbiguous)]
    [InlineData(BusinessAccessDenialReason.BusinessInactive)]
    [InlineData(BusinessAccessDenialReason.RoleUnrecognised)]
    public async Task RequireBusinessIdAsync_throws_for_every_denial_reason(BusinessAccessDenialReason reason)
    {
        var provider = CreateProviderDeniedWith(reason);

        var exception = await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => provider.RequireBusinessIdAsync(CancellationToken.None));

        Assert.Equal(reason, exception.Reason);
    }

    /// <summary>
    /// Asking for the role must fail closed in exactly the cases asking for the business does -
    /// including the unrecognised stored role, which must never come back as some default role a
    /// capability check would then honour.
    /// </summary>
    [Theory]
    [InlineData(BusinessAccessDenialReason.NotAuthenticated)]
    [InlineData(BusinessAccessDenialReason.UnidentifiableActor)]
    [InlineData(BusinessAccessDenialReason.MembershipMissing)]
    [InlineData(BusinessAccessDenialReason.MembershipInactive)]
    [InlineData(BusinessAccessDenialReason.MembershipAmbiguous)]
    [InlineData(BusinessAccessDenialReason.BusinessInactive)]
    [InlineData(BusinessAccessDenialReason.RoleUnrecognised)]
    public async Task RequireRoleAsync_throws_for_every_denial_reason(BusinessAccessDenialReason reason)
    {
        var provider = CreateProviderDeniedWith(reason);

        var exception = await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => provider.RequireRoleAsync(CancellationToken.None));

        Assert.Equal(reason, exception.Reason);
    }

    /// <summary>
    /// A denial message may reach a log, so it must carry the reason only - never the actor
    /// directory tenant or object id.
    /// </summary>
    [Fact]
    public async Task Denial_message_does_not_leak_the_actor_identity()
    {
        var provider = Provider(
            FakeAuthenticatedActorAccessor.Identified(Actor()),
            new FakeBusinessMembershipStore());

        var exception = await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => provider.RequireBusinessIdAsync(CancellationToken.None));

        Assert.DoesNotContain(Tid, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Oid, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The provider is scoped per request: several use cases in one request must agree on the
    /// current business and must not each re-query the membership table.
    /// </summary>
    [Fact]
    public async Task Resolution_is_memoised_for_the_lifetime_of_the_scope()
    {
        var accessor = FakeAuthenticatedActorAccessor.Identified(Actor());
        var store = new FakeBusinessMembershipStore(Membership(7));
        var provider = Provider(accessor, store);

        var first = await provider.ResolveAsync(CancellationToken.None);
        var second = await provider.ResolveAsync(CancellationToken.None);
        _ = await provider.RequireBusinessIdAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, store.QueryCount);
        Assert.Equal(1, accessor.CallCount);
    }

    /// <summary>
    /// The support-session branch (issue #551): a request whose scope was published for a support
    /// session acts in that business, so a use case that has to pass a business id to a port passes
    /// the one the tenant query filters are already scoped to. No membership is read at all - the
    /// one caller who can be in this state is a member of nothing.
    /// </summary>
    [Fact]
    public async Task RequireBusinessIdAsync_answers_the_support_sessions_business_without_a_membership()
    {
        var scope = new BusinessScope();
        scope.ResolveForSupportSession(BusinessId.From(9));
        var store = new FakeBusinessMembershipStore();
        var provider = Provider(FakeAuthenticatedActorAccessor.Identified(Actor()), store, scope);

        Assert.Equal(BusinessId.From(9), await provider.RequireBusinessIdAsync(CancellationToken.None));
        Assert.Equal(0, store.QueryCount);
    }

    /// <summary>
    /// A support session is not a membership and must not be reported as one: the resolution stays
    /// denied, and asking for a role fails closed rather than inventing one. What a support request
    /// may do comes from <c>Access.CurrentCapabilities</c>' fixed set instead.
    /// </summary>
    [Fact]
    public async Task A_support_session_resolves_no_membership_and_no_role()
    {
        var scope = new BusinessScope();
        scope.ResolveForSupportSession(BusinessId.From(9));
        var provider = Provider(
            FakeAuthenticatedActorAccessor.Identified(Actor()),
            new FakeBusinessMembershipStore(),
            scope);

        var resolution = await provider.ResolveAsync(CancellationToken.None);

        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.ResolvedRole);
        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => provider.RequireRoleAsync(CancellationToken.None));
    }

    /// <summary>
    /// An ordinary member's own resolution is untouched by the branch above: a resolved scope is not
    /// a support scope, and the business still comes from their membership.
    /// </summary>
    [Fact]
    public async Task A_resolved_members_business_still_comes_from_their_membership()
    {
        var scope = new BusinessScope();
        scope.Resolve(BusinessId.From(7));
        var provider = Provider(
            FakeAuthenticatedActorAccessor.Identified(Actor()),
            new FakeBusinessMembershipStore(Membership(7)),
            scope);

        Assert.Equal(BusinessId.From(7), await provider.RequireBusinessIdAsync(CancellationToken.None));
        Assert.Equal(BusinessRole.Owner, await provider.RequireRoleAsync(CancellationToken.None));
    }

    private static CurrentBusinessProvider CreateProviderDeniedWith(BusinessAccessDenialReason reason) => reason switch
    {
        BusinessAccessDenialReason.NotAuthenticated => Provider(
            FakeAuthenticatedActorAccessor.NotAuthenticated(), new FakeBusinessMembershipStore()),
        BusinessAccessDenialReason.UnidentifiableActor => Provider(
            FakeAuthenticatedActorAccessor.Unidentifiable(), new FakeBusinessMembershipStore()),
        BusinessAccessDenialReason.MembershipMissing => Provider(
            FakeAuthenticatedActorAccessor.Identified(Actor()), new FakeBusinessMembershipStore()),
        BusinessAccessDenialReason.MembershipInactive => Provider(
            FakeAuthenticatedActorAccessor.Identified(Actor()),
            new FakeBusinessMembershipStore(Membership(7, isActive: false))),
        BusinessAccessDenialReason.MembershipAmbiguous => Provider(
            FakeAuthenticatedActorAccessor.Identified(Actor()),
            new FakeBusinessMembershipStore(Membership(7), Membership(8))),
        BusinessAccessDenialReason.BusinessInactive => Provider(
            FakeAuthenticatedActorAccessor.Identified(Actor()),
            new FakeBusinessMembershipStore(Membership(7, businessStatus: BusinessStatus.Deactivated))),
        BusinessAccessDenialReason.RoleUnrecognised => Provider(
            FakeAuthenticatedActorAccessor.Identified(Actor()),
            new FakeBusinessMembershipStore(Membership(7, role: (BusinessRole)0))),
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unhandled denial reason."),
    };
}
