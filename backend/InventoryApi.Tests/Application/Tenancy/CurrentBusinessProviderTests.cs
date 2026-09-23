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

    private static ActorBusinessMembership Membership(int businessId, bool isActive = true, bool businessIsActive = true) =>
        new(BusinessId.From(businessId), isActive, businessIsActive);

    [Fact]
    public async Task Identified_actor_with_one_active_membership_resolves_to_that_business()
    {
        var store = new FakeBusinessMembershipStore(Membership(7));
        var provider = new CurrentBusinessProvider(FakeAuthenticatedActorAccessor.Identified(Actor()), store);

        var resolution = await provider.ResolveAsync(CancellationToken.None);

        Assert.True(resolution.IsResolved);
        Assert.Equal(BusinessId.From(7), resolution.ResolvedBusinessId);
        Assert.Equal(Actor(), store.LastQueriedActor);
    }

    [Fact]
    public async Task RequireBusinessIdAsync_returns_the_resolved_business()
    {
        var provider = new CurrentBusinessProvider(
            FakeAuthenticatedActorAccessor.Identified(Actor()),
            new FakeBusinessMembershipStore(Membership(7)));

        Assert.Equal(BusinessId.From(7), await provider.RequireBusinessIdAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Unauthenticated_caller_is_denied_without_querying_memberships()
    {
        var store = new FakeBusinessMembershipStore(Membership(7));
        var provider = new CurrentBusinessProvider(FakeAuthenticatedActorAccessor.NotAuthenticated(), store);

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
        var provider = new CurrentBusinessProvider(FakeAuthenticatedActorAccessor.Unidentifiable(), store);

        var resolution = await provider.ResolveAsync(CancellationToken.None);

        Assert.False(resolution.IsResolved);
        Assert.Equal(BusinessAccessDenialReason.UnidentifiableActor, resolution.DenialReason);
        Assert.Equal(0, store.QueryCount);
    }

    [Fact]
    public async Task Actor_with_no_membership_is_denied()
    {
        var provider = new CurrentBusinessProvider(
            FakeAuthenticatedActorAccessor.Identified(Actor()),
            new FakeBusinessMembershipStore());

        var resolution = await provider.ResolveAsync(CancellationToken.None);

        Assert.False(resolution.IsResolved);
        Assert.Equal(BusinessAccessDenialReason.MembershipMissing, resolution.DenialReason);
    }

    [Fact]
    public async Task Actor_with_memberships_in_two_businesses_is_denied_as_ambiguous()
    {
        var provider = new CurrentBusinessProvider(
            FakeAuthenticatedActorAccessor.Identified(Actor()),
            new FakeBusinessMembershipStore(Membership(7), Membership(8)));

        var resolution = await provider.ResolveAsync(CancellationToken.None);

        Assert.False(resolution.IsResolved);
        Assert.Equal(BusinessAccessDenialReason.MembershipAmbiguous, resolution.DenialReason);
    }

    [Theory]
    [InlineData(BusinessAccessDenialReason.NotAuthenticated)]
    [InlineData(BusinessAccessDenialReason.UnidentifiableActor)]
    [InlineData(BusinessAccessDenialReason.MembershipMissing)]
    [InlineData(BusinessAccessDenialReason.MembershipInactive)]
    [InlineData(BusinessAccessDenialReason.MembershipAmbiguous)]
    [InlineData(BusinessAccessDenialReason.BusinessInactive)]
    public async Task RequireBusinessIdAsync_throws_for_every_denial_reason(BusinessAccessDenialReason reason)
    {
        var provider = CreateProviderDeniedWith(reason);

        var exception = await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => provider.RequireBusinessIdAsync(CancellationToken.None));

        Assert.Equal(reason, exception.Reason);
    }

    /// <summary>
    /// A denial message may reach a log, so it must carry the reason only - never the actor
    /// directory tenant or object id.
    /// </summary>
    [Fact]
    public async Task Denial_message_does_not_leak_the_actor_identity()
    {
        var provider = new CurrentBusinessProvider(
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
        var provider = new CurrentBusinessProvider(accessor, store);

        var first = await provider.ResolveAsync(CancellationToken.None);
        var second = await provider.ResolveAsync(CancellationToken.None);
        _ = await provider.RequireBusinessIdAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, store.QueryCount);
        Assert.Equal(1, accessor.CallCount);
    }

    private static CurrentBusinessProvider CreateProviderDeniedWith(BusinessAccessDenialReason reason) => reason switch
    {
        BusinessAccessDenialReason.NotAuthenticated => new CurrentBusinessProvider(
            FakeAuthenticatedActorAccessor.NotAuthenticated(), new FakeBusinessMembershipStore()),
        BusinessAccessDenialReason.UnidentifiableActor => new CurrentBusinessProvider(
            FakeAuthenticatedActorAccessor.Unidentifiable(), new FakeBusinessMembershipStore()),
        BusinessAccessDenialReason.MembershipMissing => new CurrentBusinessProvider(
            FakeAuthenticatedActorAccessor.Identified(Actor()), new FakeBusinessMembershipStore()),
        BusinessAccessDenialReason.MembershipInactive => new CurrentBusinessProvider(
            FakeAuthenticatedActorAccessor.Identified(Actor()),
            new FakeBusinessMembershipStore(Membership(7, isActive: false))),
        BusinessAccessDenialReason.MembershipAmbiguous => new CurrentBusinessProvider(
            FakeAuthenticatedActorAccessor.Identified(Actor()),
            new FakeBusinessMembershipStore(Membership(7), Membership(8))),
        BusinessAccessDenialReason.BusinessInactive => new CurrentBusinessProvider(
            FakeAuthenticatedActorAccessor.Identified(Actor()),
            new FakeBusinessMembershipStore(Membership(7, businessIsActive: false))),
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unhandled denial reason."),
    };
}
