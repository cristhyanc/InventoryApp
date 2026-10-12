using Inventory.Application.Access;
using Inventory.Application.Onboarding;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Onboarding;
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
        BusinessStatus businessStatus = BusinessStatus.Active,
        string? rejectionReason = null) =>
        new(
            BusinessId.From(businessId),
            BusinessRole.Owner,
            isActive,
            businessStatus,
            StatusChangedAtUtc: new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc),
            rejectionReason);

    private static GetAccountState UseCase(
        FakeBusinessMembershipStore store,
        FakeAuthenticatedActorAccessor? accessor = null,
        ISelfServiceOnboardingFlag? onboarding = null) =>
        new(
            accessor ?? FakeAuthenticatedActorAccessor.Identified(Actor()),
            store,
            onboarding ?? FakeSelfServiceOnboardingFlag.Disabled);

    [Fact]
    public async Task Reports_the_state_the_precedence_gives_for_the_callers_own_memberships()
    {
        var store = new FakeBusinessMembershipStore(Membership(7));

        var result = await UseCase(store).Handle(CancellationToken.None);

        Assert.Equal(AccountState.Member, result.State);
        Assert.Equal(Actor(), store.LastQueriedActor);
    }

    [Theory]
    [InlineData(false, BusinessStatus.Active, AccountState.MembershipRevoked)]
    [InlineData(true, BusinessStatus.Deactivated, AccountState.BusinessDeactivated)]
    [InlineData(true, BusinessStatus.Pending, AccountState.ApplicationPending)]
    [InlineData(false, BusinessStatus.Rejected, AccountState.ApplicationRejected)]
    public async Task Reports_each_state_a_single_membership_can_be_in(
        bool isActive,
        BusinessStatus businessStatus,
        AccountState expected)
    {
        var store = new FakeBusinessMembershipStore(
            Membership(7, isActive: isActive, businessStatus: businessStatus));

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
    /// <c>onboardingEnabled</c> is the deployment's own self-service onboarding flag (issue #541),
    /// read through the same port the apply endpoint reads, so a screen that offers "Start a new
    /// business" and the endpoint behind it cannot disagree. It is answered here, in the
    /// Application layer, rather than in the controller, which is what makes that one read.
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
    /// And it reports the flag being on, for every state alike: the field describes the deployment,
    /// not the caller. Whether <em>this</em> account may apply is
    /// <c>ApplyForBusiness</c>'s decision from their own membership records, so a member and a
    /// pending applicant see <c>true</c> here too while the endpoint still refuses them - the
    /// alternative, answering this field from the caller's eligibility, would be a second copy of
    /// that rule in the one place it must not be duplicated.
    /// </summary>
    [Theory]
    [InlineData(true, BusinessStatus.Active)]
    [InlineData(true, BusinessStatus.Pending)]
    [InlineData(false, BusinessStatus.Active)]
    [InlineData(false, BusinessStatus.Rejected)]
    public async Task Onboarding_is_reported_as_enabled_for_every_state_when_the_deployment_allows_it(
        bool isActive,
        BusinessStatus businessStatus)
    {
        var store = new FakeBusinessMembershipStore(
            Membership(7, isActive: isActive, businessStatus: businessStatus));

        var result = await UseCase(store, onboarding: FakeSelfServiceOnboardingFlag.Enabled)
            .Handle(CancellationToken.None);

        Assert.True(result.OnboardingEnabled);
    }

    /// <summary>
    /// Including for a caller whose token identifies nobody, who is reported as
    /// <see cref="AccountState.NoMembership"/>: the flag is not a fact about them, so it is not
    /// withheld from them either.
    /// </summary>
    [Fact]
    public async Task Onboarding_is_reported_for_a_caller_whose_token_identifies_nobody()
    {
        var result = await UseCase(
                new FakeBusinessMembershipStore(),
                FakeAuthenticatedActorAccessor.Unidentifiable(),
                FakeSelfServiceOnboardingFlag.Enabled)
            .Handle(CancellationToken.None);

        Assert.Equal(AccountState.NoMembership, result.State);
        Assert.True(result.OnboardingEnabled);
    }

    /// <summary>
    /// A rejection reason exists only for <see cref="AccountState.ApplicationRejected"/> (issue
    /// #540): no other state reports one, whatever the records happen to carry.
    /// </summary>
    [Theory]
    [InlineData(true, BusinessStatus.Active)]
    [InlineData(true, BusinessStatus.Pending)]
    [InlineData(true, BusinessStatus.Deactivated)]
    [InlineData(false, BusinessStatus.Active)]
    public async Task No_state_other_than_ApplicationRejected_carries_a_rejection_reason(
        bool isActive,
        BusinessStatus businessStatus)
    {
        var store = new FakeBusinessMembershipStore(
            Membership(7, isActive: isActive, businessStatus: businessStatus, rejectionReason: "Refused."));

        var result = await UseCase(store).Handle(CancellationToken.None);

        Assert.Null(result.RejectionReason);
        Assert.NotEqual(AccountState.ApplicationRejected, result.State);
    }

    /// <summary>
    /// The one state that does carry it: the rejection reason the operator recorded on the
    /// business reaches the applicant's own account state (issue #540), and it is the only thing
    /// about that business the response says - still no name, no id, and no confirmation that it
    /// exists.
    /// </summary>
    [Fact]
    public async Task A_rejected_application_reports_the_recorded_reason()
    {
        var store = new FakeBusinessMembershipStore(
            Membership(
                7,
                isActive: false,
                businessStatus: BusinessStatus.Rejected,
                rejectionReason: "The ABN provided belongs to another entity."));

        var result = await UseCase(store).Handle(CancellationToken.None);

        Assert.Equal(AccountState.ApplicationRejected, result.State);
        Assert.Equal("The ABN provided belongs to another entity.", result.RejectionReason);
        Assert.False(result.OnboardingEnabled);
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
