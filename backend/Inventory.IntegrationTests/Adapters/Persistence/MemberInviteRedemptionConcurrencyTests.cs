using Inventory.Application.Members;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Members;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The invite redemption of issue #532 under real concurrency, on two independent connections to one
/// real SQLite file through <see cref="MembershipWriteRaceHarness"/>.
///
/// <para>These are the tests the design exists for, and the two races the issue names. Both are
/// check-then-write, and a check-then-write rule that is not serialised is not a rule: without
/// issue #522's <c>BEGIN IMMEDIATE</c> transaction and the conditional <c>UPDATE</c> that closes the
/// invite, two people would both read "this invite is pending" and both join on one link, or one
/// person would end up with active memberships in two businesses - which denies them both.</para>
///
/// <para>The whole use case is exercised, not just the adapter: the real
/// <see cref="MembershipWriteGuard"/>, the real <see cref="EfMemberInviteRedemptionStore"/> and the
/// real <see cref="EfBusinessMembershipWriteStore"/> over the migrated schema, with only the
/// authenticated actor, the clock and the audit sink faked - because those are the three things a
/// test has to decide rather than observe.</para>
/// </summary>
public class MemberInviteRedemptionConcurrencyTests : IAsyncDisposable
{
    private const int FirstBusinessId = 1;
    private const int SecondBusinessId = 2;

    private const string JoinerOid = "22222222-2222-2222-2222-222222222222";
    private const string SecondJoinerOid = "33333333-3333-3333-3333-333333333333";

    private static readonly DateTime Now = MembershipWriteRaceHarness.SeededAt.AddHours(1);

    private readonly MembershipWriteRaceHarness _harness =
        MembershipWriteRaceHarness.Create(FirstBusinessId, SecondBusinessId);

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    /// <summary>
    /// Two people presenting the same link at the same instant. Exactly one joins; the other is
    /// refused with the one generic invalid-invite sentence, and the invite records the membership
    /// that won and only that one.
    ///
    /// Without the conditional close both would be let in on one single-use link, and the row would
    /// name just one of the two memberships it had created.
    /// </summary>
    [Fact]
    public async Task The_same_invite_redeemed_twice_at_once_grants_exactly_one_membership()
    {
        var inviteId = _harness.SeedInvite(FirstBusinessId, "the-link", BusinessRole.Manager);

        var failures = await MembershipWriteRaceHarness.RunAsync(
            token => RedeemAsync("the-link", JoinerOid, token),
            token => RedeemAsync("the-link", SecondJoinerOid, token));

        var refused = MembershipWriteRaceHarness.SingleFailure(failures);
        var invalid = Assert.IsType<DomainValidationException>(refused);
        Assert.Equal(MemberInviteRedemptionPolicy.InvalidInviteMessage, invalid.Message);

        // One membership, in the inviting business, with the invite's role - and the loser's row is
        // gone with its transaction rather than left behind revoked.
        var membership = Assert.Single(_harness.Memberships());
        Assert.Equal(FirstBusinessId, membership.BusinessId);
        Assert.True(membership.IsActive);
        Assert.Equal(BusinessRole.Manager, membership.Role);

        var invite = Assert.Single(_harness.Invites());
        Assert.Equal(inviteId, invite.Id);
        Assert.Equal(MemberInviteStatus.Used, invite.Status);
        Assert.Equal(Now, invite.UsedAtUtc);
        Assert.Equal(membership.Id, invite.RedeemedByMembershipId);
    }

    /// <summary>
    /// One person holding two links, from two different businesses, redeemed at the same instant.
    /// Exactly one succeeds; the other is refused as a conflict with the standing membership
    /// sentence, and - the half that matters most - its invite is left <em>unused</em>, so the
    /// business that issued it has not silently spent its link on a redemption that never happened.
    /// </summary>
    [Fact]
    public async Task Two_invites_from_two_businesses_for_one_identity_leave_one_membership_and_one_unused_invite()
    {
        var first = _harness.SeedInvite(FirstBusinessId, "first-link", BusinessRole.Manager);
        var second = _harness.SeedInvite(SecondBusinessId, "second-link", BusinessRole.Owner);

        var failures = await MembershipWriteRaceHarness.RunAsync(
            token => RedeemAsync("first-link", JoinerOid, token),
            token => RedeemAsync("second-link", JoinerOid, token));

        var refused = MembershipWriteRaceHarness.SingleFailure(failures);
        var conflict = Assert.IsType<DomainConflictException>(refused);
        Assert.Equal(MembershipEligibility.AlreadyAMemberMessage, conflict.Message);

        var membership = Assert.Single(_harness.Memberships());
        Assert.True(membership.IsActive);

        var invites = _harness.Invites();
        Assert.Equal(2, invites.Count);

        // The invite of the business the person actually joined is used and names their membership;
        // the other is exactly as it was seeded.
        var used = Assert.Single(invites.Where(invite => invite.Status == MemberInviteStatus.Used));
        var unused = Assert.Single(invites.Where(invite => invite.Status == MemberInviteStatus.Pending));

        Assert.Equal(membership.BusinessId, used.BusinessId);
        Assert.Equal(membership.Id, used.RedeemedByMembershipId);
        Assert.NotEqual(membership.BusinessId, unused.BusinessId);
        Assert.Null(unused.UsedAtUtc);
        Assert.Null(unused.RedeemedByMembershipId);
        Assert.Contains(used.Id, new[] { first, second });
        Assert.Contains(unused.Id, new[] { first, second });
    }

    /// <summary>
    /// The same two-business race for somebody who was revoked in both: the reactivation path is
    /// serialised exactly as the creation path is, so one row comes back and the other stays
    /// revoked, rather than both reactivating into the state the filtered index forbids.
    /// </summary>
    [Fact]
    public async Task Two_invites_for_one_revoked_identity_reactivate_exactly_one_row()
    {
        _harness.SeedInvite(FirstBusinessId, "first-link", BusinessRole.Manager);
        _harness.SeedInvite(SecondBusinessId, "second-link", BusinessRole.Owner);
        _harness.SeedMembership(FirstBusinessId, JoinerOid, BusinessRole.Operator, isActive: false);
        _harness.SeedMembership(SecondBusinessId, JoinerOid, BusinessRole.Operator, isActive: false);

        var failures = await MembershipWriteRaceHarness.RunAsync(
            token => RedeemAsync("first-link", JoinerOid, token),
            token => RedeemAsync("second-link", JoinerOid, token));

        var conflict = Assert.IsType<DomainConflictException>(MembershipWriteRaceHarness.SingleFailure(failures));
        Assert.Equal(MembershipEligibility.AlreadyAMemberMessage, conflict.Message);

        var memberships = _harness.Memberships();
        Assert.Equal(2, memberships.Count);

        var active = Assert.Single(memberships.Where(membership => membership.IsActive));
        var stillRevoked = Assert.Single(memberships.Where(membership => !membership.IsActive));

        // The reactivated row took the invite's role; the refused one kept everything it had,
        // including the instant its state last changed.
        Assert.NotEqual(BusinessRole.Operator, active.Role);
        Assert.Equal(Now, active.StatusChangedAtUtc);
        Assert.Equal(BusinessRole.Operator, stillRevoked.Role);
        Assert.Equal(MembershipWriteRaceHarness.SeededAt, stillRevoked.StatusChangedAtUtc);

        Assert.Single(_harness.Invites().Where(invite => invite.Status == MemberInviteStatus.Used));
        Assert.Single(_harness.Invites().Where(invite => invite.Status == MemberInviteStatus.Pending));
    }

    /// <summary>
    /// The sequential half of the first race, and the one a person meets in practice: redeeming a
    /// link somebody already used is refused with the same generic sentence, and the second attempt
    /// changes nothing at all - not the first membership, not the invite's recorded instant.
    /// </summary>
    [Fact]
    public async Task Redeeming_an_already_used_invite_changes_nothing()
    {
        _harness.SeedInvite(FirstBusinessId, "the-link", BusinessRole.Manager);

        await RedeemAsync("the-link", JoinerOid, CancellationToken.None);

        var invalid = await Assert.ThrowsAsync<DomainValidationException>(() =>
            RedeemAsync("the-link", SecondJoinerOid, CancellationToken.None));
        Assert.Equal(MemberInviteRedemptionPolicy.InvalidInviteMessage, invalid.Message);

        var membership = Assert.Single(_harness.Memberships());
        var invite = Assert.Single(_harness.Invites());
        Assert.Equal(MemberInviteStatus.Used, invite.Status);
        Assert.Equal(membership.Id, invite.RedeemedByMembershipId);
        Assert.Equal(Now, invite.UsedAtUtc);
    }

    /// <summary>
    /// Runs one real redemption on its own connection, as a request would: a denied business scope,
    /// the real guard and the real stores, and the caller's identity supplied the way the API
    /// boundary supplies it.
    /// </summary>
    private async Task RedeemAsync(string token, string objectId, CancellationToken cancellationToken)
    {
        var identity = MembershipWriteRaceHarness.Actor(objectId);

        // Denied, because somebody joining a business is not yet a member of any - which is exactly
        // the scope the membership-not-required endpoint gives this request.
        await using var db = TestAppDbContext.Denied(_harness.Options);

        var redeem = new RedeemMemberInvite(
            FakeActorProfileAccessor.For(identity, MemberProfile.None),
            new EfBusinessMembershipStore(db),
            new EfMemberInviteRedemptionStore(db),
            new MembershipWriteGuard(new EfBusinessMembershipWriteStore(db)),
            new FakeMemberInviteAudit(),
            new FakeClock(Now));

        await redeem.Handle(token, cancellationToken);
    }
}
