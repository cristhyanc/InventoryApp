using Inventory.Application.Access;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Application.Access;

/// <summary>
/// The use case behind <c>GET /api/me/account-state</c> (issue #523), without ASP.NET Core or EF
/// Core.
///
/// Two things matter here and nowhere else. The question is asked about the caller's own identity
/// and nothing else - there is no parameter that could name somebody else - and the answer carries
/// the state alone: no business name, no business id, and no detail about another business, even
/// when the caller is a member of one.
/// </summary>
public class GetAccountStateTests
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "22222222-2222-2222-2222-222222222222";

    private static ActorIdentity Actor()
    {
        Assert.True(ActorIdentity.TryCreate(Tid, Oid, out var actor));
        return actor!;
    }

    private static ActorBusinessMembership Membership(
        int businessId,
        bool isActive = true,
        bool businessIsActive = true) =>
        new(BusinessId.From(businessId), BusinessRole.Owner, isActive, businessIsActive);

    private static GetAccountState UseCase(
        FakeBusinessMembershipStore store,
        FakeAuthenticatedActorAccessor? accessor = null) =>
        new(accessor ?? FakeAuthenticatedActorAccessor.Identified(Actor()), store);

    [Fact]
    public async Task Reports_the_state_the_precedence_gives_for_the_callers_own_memberships()
    {
        var store = new FakeBusinessMembershipStore(Membership(7));

        var result = await UseCase(store).Handle(CancellationToken.None);

        Assert.Equal(AccountState.Member, result.State);
        Assert.Equal(Actor(), store.LastQueriedActor);
    }

    [Theory]
    [InlineData(false, true, AccountState.MembershipRevoked)]
    [InlineData(true, false, AccountState.BusinessDeactivated)]
    public async Task Reports_each_state_a_single_membership_can_be_in(
        bool isActive,
        bool businessIsActive,
        AccountState expected)
    {
        var store = new FakeBusinessMembershipStore(
            Membership(7, isActive: isActive, businessIsActive: businessIsActive));

        Assert.Equal(expected, (await UseCase(store).Handle(CancellationToken.None)).State);
    }

    [Fact]
    public async Task An_identity_with_no_membership_rows_is_reported_as_NoMembership()
    {
        var result = await UseCase(new FakeBusinessMembershipStore()).Handle(CancellationToken.None);

        Assert.Equal(AccountState.NoMembership, result.State);
    }

    /// <summary>
    /// Two active memberships, which only legacy data predating issue #522's index can hold. The
    /// relational database now refuses that state, so this is the level at which it stays covered.
    /// </summary>
    [Fact]
    public async Task Two_active_memberships_are_reported_as_MembershipAmbiguous()
    {
        var store = new FakeBusinessMembershipStore(Membership(7), Membership(8));

        Assert.Equal(
            AccountState.MembershipAmbiguous,
            (await UseCase(store).Handle(CancellationToken.None)).State);
    }

    /// <summary>
    /// <c>onboardingEnabled</c> is part of the agreed response and is <c>false</c> until issue
    /// #507 adds the self-service onboarding flag. It is answered here, in the Application layer,
    /// so #507 reads its configuration in one place rather than in the controller.
    /// </summary>
    [Fact]
    public async Task Onboarding_is_reported_as_disabled_and_no_rejection_reason_is_given()
    {
        var result = await UseCase(new FakeBusinessMembershipStore(Membership(7)))
            .Handle(CancellationToken.None);

        Assert.False(result.OnboardingEnabled);
        Assert.Null(result.RejectionReason);
    }

    /// <summary>
    /// A rejection reason exists only for <see cref="AccountState.ApplicationRejected"/>, which
    /// needs issue #507's business status. No state this slice can report carries one.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task No_state_this_slice_reports_carries_a_rejection_reason(
        bool isActive,
        bool businessIsActive)
    {
        var store = new FakeBusinessMembershipStore(
            Membership(7, isActive: isActive, businessIsActive: businessIsActive));

        var result = await UseCase(store).Handle(CancellationToken.None);

        Assert.Null(result.RejectionReason);
        Assert.NotEqual(AccountState.ApplicationRejected, result.State);
    }

    /// <summary>
    /// An authenticated token with no usable <c>(tid, oid)</c> pair identifies nobody, so there
    /// are no membership records to look up and none are looked for. It is reported as
    /// <see cref="AccountState.NoMembership"/>, which is what that caller's situation is: this
    /// endpoint exists to tell a caller without a usable membership which screen to show, and it
    /// grants nothing by answering - the request's business scope stays denied either way.
    /// </summary>
    [Fact]
    public async Task A_caller_whose_token_identifies_nobody_is_reported_as_NoMembership()
    {
        var store = new FakeBusinessMembershipStore(Membership(7));

        var result = await UseCase(store, FakeAuthenticatedActorAccessor.Unidentifiable())
            .Handle(CancellationToken.None);

        Assert.Equal(AccountState.NoMembership, result.State);
        Assert.Null(store.LastQueriedActor);
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_reported_as_NoMembership_without_a_lookup()
    {
        var store = new FakeBusinessMembershipStore(Membership(7));

        var result = await UseCase(store, FakeAuthenticatedActorAccessor.NotAuthenticated())
            .Handle(CancellationToken.None);

        Assert.Equal(AccountState.NoMembership, result.State);
        Assert.Equal(0, store.QueryCount);
    }
}
