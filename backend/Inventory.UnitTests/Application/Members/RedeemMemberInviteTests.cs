using Inventory.Application.Members;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Members;

/// <summary>
/// The invite redemption of issue #532 against fakes: what it refuses, in which order it reads and
/// writes, and what reaches the audit trail.
///
/// The relational and concurrency behaviour is proved elsewhere -
/// <c>EfMemberInviteRedemptionStoreTests</c>, <c>MemberInviteRedemptionConcurrencyTests</c> and
/// <c>InviteRedemptionApiTests</c>. What these tests are for is the decisions, and above all the
/// ones that are invisible in a passing happy path: that the store never sees a plaintext token,
/// that no business id comes from anywhere but the invite row, that every unusable invite is refused
/// with the one sentence that tells them apart from nothing, that an invite closed between the
/// authoritative read and the write takes the membership with it, and that nothing is audited for a
/// redemption that did not happen.
/// </summary>
public class RedeemMemberInviteTests
{
    private const int InvitingBusinessId = 4;
    private const int OtherBusinessId = 9;
    private const int InviteId = 31;

    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string JoinerOid = "22222222-2222-2222-2222-222222222222";

    private const string Token = "a-token-only-the-link-carries";

    private static readonly DateTime Now = new(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// An expiry deliberately just ahead of <see cref="Now"/>, so the timing tests of issue #608 can
    /// cross it inside one request without a wall-clock sleep.
    /// </summary>
    private static readonly DateTime ExpiresAt = Now.AddMinutes(5);

    #region Joining

    /// <summary>
    /// The whole redemption for somebody new: the membership is created in the invite's business
    /// with the invite's role, the invite is closed against that membership, the caller is told the
    /// role, and one audit event records it.
    /// </summary>
    [Fact]
    public async Task A_person_with_no_membership_joins_the_inviting_business_with_the_invites_role()
    {
        var fixture = new Fixture();
        fixture.SeedInvite(BusinessRole.Manager);

        var redeemed = await fixture.Redeem().Handle(Token, CancellationToken.None);

        Assert.Equal(BusinessRole.Manager, redeemed.Role);

        var membership = fixture.Store.MembershipFor(InvitingBusinessId, fixture.Joiner);
        Assert.True(membership.IsActive);
        Assert.Equal(BusinessRole.Manager, membership.Role);

        var invite = fixture.Store.InviteFor(MemberInviteToken.Hash(Token));
        Assert.Equal(MemberInviteStatus.Used, invite.StoredStatus);
        Assert.Equal(membership.MembershipId, invite.UsedByMembershipId);
    }

    /// <summary>
    /// The order is the design. The authoritative read of the invite happens inside the write
    /// transaction - after the lock is taken - and the invite is closed after the membership exists,
    /// so the membership the row names is a membership that was really created.
    /// </summary>
    [Fact]
    public async Task The_invite_is_read_and_closed_inside_the_write_transaction()
    {
        var fixture = new Fixture();
        fixture.SeedInvite(BusinessRole.Operator);

        await fixture.Redeem().Handle(Token, CancellationToken.None);

        Assert.Equal(
            [
                FakeBusinessMembershipWriteStore.Begin,
                FakeBusinessMembershipWriteStore.IdentityRead,
                FakeMemberInviteRedemptionStore.Lookup,
                FakeMemberInviteRedemptionStore.FindMembership,
                FakeMemberInviteRedemptionStore.CreateMembership,
                FakeMemberInviteRedemptionStore.MarkUsed,
                FakeBusinessMembershipWriteStore.Commit,
                FakeBusinessMembershipWriteStore.Dispose,
            ],
            fixture.Operations);
    }

    /// <summary>
    /// Every statement the redemption writes names the business the invite row reported, and the
    /// caller supplies nothing that could choose it: the use case takes no business id at all.
    /// </summary>
    [Fact]
    public async Task Every_write_names_the_business_the_invite_row_reported()
    {
        var fixture = new Fixture();
        fixture.SeedInvite(BusinessRole.Manager);

        await fixture.Redeem().Handle(Token, CancellationToken.None);

        Assert.NotEmpty(fixture.Store.WrittenBusinessIds);
        Assert.All(fixture.Store.WrittenBusinessIds, written => Assert.Equal(InvitingBusinessId, written));
        Assert.False(fixture.Store.HasMembership(OtherBusinessId, fixture.Joiner));
    }

    /// <summary>
    /// The store receives the token's hash and never the plaintext, so no adapter, row or log can
    /// hold it - the same guarantee the creation side gives, in the other direction.
    /// </summary>
    [Fact]
    public async Task The_store_is_looked_up_by_the_tokens_hash_and_never_the_token()
    {
        var fixture = new Fixture();
        // Seeded under the plaintext rather than the hash: a store looked up by the token itself
        // would find this and pass, which is exactly the mistake worth failing on.
        fixture.Store.AddInvite(
            Token,
            InviteId,
            InvitingBusinessId,
            BusinessRole.Manager,
            MemberInviteStatus.Pending,
            Now.AddDays(1));

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() =>
            fixture.Redeem().Handle(Token, CancellationToken.None));

        Assert.Equal(MemberInviteRedemptionPolicy.InvalidInviteMessage, refusal.Message);
    }

    /// <summary>
    /// The display name and email address the caller's own token reported are stored on the new
    /// membership (issue #524's values, which decide nothing).
    /// </summary>
    [Fact]
    public async Task The_new_membership_carries_the_display_values_the_callers_token_reported()
    {
        var fixture = new Fixture(profile: MemberProfile.Create("Dana Doe", "dana@example.test"));
        fixture.SeedInvite(BusinessRole.Operator);

        await fixture.Redeem().Handle(Token, CancellationToken.None);

        var membership = fixture.Store.MembershipFor(InvitingBusinessId, fixture.Joiner);
        Assert.Equal("Dana Doe", membership.DisplayName);
        Assert.Equal("dana@example.test", membership.Email);
    }

    /// <summary>
    /// A token carrying neither claim stores neither value, rather than a placeholder: a membership
    /// created before anybody reported a name simply has none.
    /// </summary>
    [Fact]
    public async Task A_token_that_reports_no_display_values_stores_none()
    {
        var fixture = new Fixture();
        fixture.SeedInvite(BusinessRole.Operator);

        await fixture.Redeem().Handle(Token, CancellationToken.None);

        var membership = fixture.Store.MembershipFor(InvitingBusinessId, fixture.Joiner);
        Assert.Null(membership.DisplayName);
        Assert.Null(membership.Email);
    }

    #endregion

    #region Rejoining

    /// <summary>
    /// A person this business revoked is reactivated rather than duplicated, and with the invite's
    /// role rather than the one the revoked row happened to hold: the invite is the new decision
    /// about what they may do.
    /// </summary>
    [Fact]
    public async Task A_revoked_member_is_reactivated_with_the_invites_role()
    {
        var fixture = new Fixture();
        fixture.SeedInvite(BusinessRole.Owner);
        fixture.Store.AddMembership(InvitingBusinessId, fixture.Joiner, membershipId: 77, isActive: false);

        var redeemed = await fixture.Redeem().Handle(Token, CancellationToken.None);

        Assert.Equal(BusinessRole.Owner, redeemed.Role);

        var membership = fixture.Store.MembershipFor(InvitingBusinessId, fixture.Joiner);
        Assert.Equal(77, membership.MembershipId);
        Assert.True(membership.IsActive);
        Assert.Equal(BusinessRole.Owner, membership.Role);

        Assert.Equal(
            [
                FakeBusinessMembershipWriteStore.Begin,
                FakeBusinessMembershipWriteStore.IdentityRead,
                FakeMemberInviteRedemptionStore.Lookup,
                FakeMemberInviteRedemptionStore.FindMembership,
                FakeMemberInviteRedemptionStore.ReactivateMembership,
                FakeMemberInviteRedemptionStore.MarkUsed,
                FakeBusinessMembershipWriteStore.Commit,
                FakeBusinessMembershipWriteStore.Dispose,
            ],
            fixture.Operations);

        // And the invite records the row that was brought back, not a new one.
        Assert.Equal(77, fixture.Store.InviteFor(MemberInviteToken.Hash(Token)).UsedByMembershipId);
    }

    /// <summary>
    /// A revoked row reactivated by somebody else before this redemption's in-transaction membership
    /// read is refused with the standing "already a member" sentence, and rightly so: that read saw
    /// an <em>active</em> row, so the caller really is a member of this business. This is the one
    /// race refusal where the cause is established rather than inferred, and it keeps its wording.
    /// </summary>
    [Fact]
    public async Task A_row_reactivated_between_the_decision_and_the_write_refuses_the_redemption()
    {
        var fixture = new Fixture();
        fixture.SeedInvite(BusinessRole.Manager);
        fixture.Store.AddMembership(InvitingBusinessId, fixture.Joiner, membershipId: 77, isActive: false);
        fixture.Store.OnceAfterLookup = () =>
            fixture.Store.ActivateBehindTheCallersBack(InvitingBusinessId, fixture.Joiner);

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() =>
            fixture.Redeem().Handle(Token, CancellationToken.None));

        Assert.Equal(MembershipEligibility.AlreadyAMemberMessage, refusal.Message);
        Assert.Equal(MemberInviteStatus.Pending, fixture.Store.InviteFor(MemberInviteToken.Hash(Token)).StoredStatus);
        Assert.Empty(fixture.Audit.Entries);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.Commit, fixture.Operations);
    }

    /// <summary>
    /// Issue #681's regression, and the case the test above is deliberately <em>not</em>: a row that
    /// stops being the revoked one after the in-transaction read, so the conditional reactivation
    /// matches nothing and the server never learned why.
    ///
    /// <para>Here the row is gone, and this caller is a member of nothing - so refusing them with
    /// "this person is already a member of a business", which is what this path did before, states
    /// something that was never established and leaves them nothing to do about it. It gets the
    /// late-change sentence instead: nothing was recorded, check whether you can already sign in, and
    /// use the link again if you cannot. It still names no business, role or member.</para>
    /// </summary>
    [Fact]
    public async Task A_row_that_is_gone_between_the_in_transaction_read_and_the_write_is_not_refused_as_already_a_member()
    {
        var fixture = new Fixture();
        fixture.SeedInvite(BusinessRole.Manager);
        fixture.Store.AddMembership(InvitingBusinessId, fixture.Joiner, membershipId: 77, isActive: false);
        // After the membership read, not after the invite lookup: a change made earlier is still seen
        // by that read, and the whole point of this case is that the read saw a revoked row.
        fixture.Store.OnceAfterMembershipLookup = () =>
            fixture.Store.RemoveBehindTheCallersBack(InvitingBusinessId, fixture.Joiner);

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() =>
            fixture.Redeem().Handle(Token, CancellationToken.None));

        Assert.Equal(MemberInviteRedemptionPolicy.MembershipChangedMessage, refusal.Message);
        Assert.NotEqual(MembershipEligibility.AlreadyAMemberMessage, refusal.Message);
        Assert.Contains(FakeMemberInviteRedemptionStore.ReactivateMembership, fixture.Operations);
        Assert.Equal(MemberInviteStatus.Pending, fixture.Store.InviteFor(MemberInviteToken.Hash(Token)).StoredStatus);
        Assert.Empty(fixture.Audit.Entries);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.Commit, fixture.Operations);
    }

    /// <summary>
    /// The same late refusal for the other cause, so the sentence is the one answer for all of them:
    /// a row reactivated <em>after</em> the in-transaction read is equally invisible to this code,
    /// and is refused the same way rather than being guessed at correctly by accident.
    /// </summary>
    [Fact]
    public async Task A_row_reactivated_after_the_in_transaction_read_gets_the_same_late_change_refusal()
    {
        var fixture = new Fixture();
        fixture.SeedInvite(BusinessRole.Manager);
        fixture.Store.AddMembership(InvitingBusinessId, fixture.Joiner, membershipId: 77, isActive: false);
        fixture.Store.OnceAfterMembershipLookup = () =>
            fixture.Store.ActivateBehindTheCallersBack(InvitingBusinessId, fixture.Joiner);

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() =>
            fixture.Redeem().Handle(Token, CancellationToken.None));

        Assert.Equal(MemberInviteRedemptionPolicy.MembershipChangedMessage, refusal.Message);
        Assert.Equal(MemberInviteStatus.Pending, fixture.Store.InviteFor(MemberInviteToken.Hash(Token)).StoredStatus);
        Assert.Empty(fixture.Audit.Entries);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.Commit, fixture.Operations);
    }

    #endregion

    #region Unusable invites

    /// <summary>
    /// The four token cases the issue names, and the two the policy adds, all answered with one
    /// sentence and with nothing written: an unknown token cannot be told from a used one, which is
    /// what stops the endpoint confirming which links exist.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a-token-no-invite-answers-to")]
    public async Task An_unknown_or_blank_token_is_refused_with_the_one_generic_sentence(string? submitted)
    {
        var fixture = new Fixture();
        fixture.SeedInvite(BusinessRole.Manager);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() =>
            fixture.Redeem().Handle(submitted, CancellationToken.None));

        Assert.Equal(MemberInviteRedemptionPolicy.InvalidInviteMessage, refusal.Message);
        Assert.False(fixture.Store.HasMembership(InvitingBusinessId, fixture.Joiner));
        Assert.Equal(MemberInviteStatus.Pending, fixture.Store.InviteFor(MemberInviteToken.Hash(Token)).StoredStatus);
        Assert.Empty(fixture.Audit.Entries);
    }

    /// <summary>
    /// A blank token is refused before anything at all is read: there is no invite it could
    /// identify, and hashing it would produce a valid-looking lookup key for the empty string.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_blank_token_is_refused_before_anything_is_read(string? submitted)
    {
        var fixture = new Fixture();

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            fixture.Redeem().Handle(submitted, CancellationToken.None));

        Assert.Empty(fixture.Operations);
    }

    [Theory]
    [InlineData(MemberInviteStatus.Used)]
    [InlineData(MemberInviteStatus.Cancelled)]
    [InlineData((MemberInviteStatus)99)]
    public async Task A_closed_invite_is_refused_and_nothing_is_written(MemberInviteStatus status)
    {
        var fixture = new Fixture();
        fixture.SeedInvite(BusinessRole.Manager, status: status);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() =>
            fixture.Redeem().Handle(Token, CancellationToken.None));

        Assert.Equal(MemberInviteRedemptionPolicy.InvalidInviteMessage, refusal.Message);
        Assert.False(fixture.Store.HasMembership(InvitingBusinessId, fixture.Joiner));
        Assert.Empty(fixture.Audit.Entries);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.Commit, fixture.Operations);
    }

    [Fact]
    public async Task An_expired_invite_is_refused_and_nothing_is_written()
    {
        var fixture = new Fixture();
        fixture.SeedInvite(BusinessRole.Manager, expiresAtUtc: Now);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() =>
            fixture.Redeem().Handle(Token, CancellationToken.None));

        Assert.Equal(MemberInviteRedemptionPolicy.InvalidInviteMessage, refusal.Message);
        Assert.False(fixture.Store.HasMembership(InvitingBusinessId, fixture.Joiner));
    }

    /// <summary>
    /// An invite of a business that is not active is the same refusal, and deliberately not a
    /// different one: "that business is deactivated" would tell somebody holding a link that the
    /// business exists and what became of it.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Pending)]
    [InlineData(BusinessStatus.Rejected)]
    [InlineData(BusinessStatus.Deactivated)]
    public async Task An_invite_of_a_business_that_is_not_active_is_refused(BusinessStatus businessStatus)
    {
        var fixture = new Fixture();
        fixture.SeedInvite(BusinessRole.Manager, businessStatus: businessStatus);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() =>
            fixture.Redeem().Handle(Token, CancellationToken.None));

        Assert.Equal(MemberInviteRedemptionPolicy.InvalidInviteMessage, refusal.Message);
        Assert.False(fixture.Store.HasMembership(InvitingBusinessId, fixture.Joiner));
        Assert.Empty(fixture.Audit.Entries);
    }

    /// <summary>
    /// An invite whose stored role this deployment does not declare is refused rather than honoured.
    /// Creating the membership would let the person in and lock them out in the same operation,
    /// because an undeclared role denies access everywhere.
    /// </summary>
    [Fact]
    public async Task An_invite_granting_an_undeclared_role_is_refused()
    {
        var fixture = new Fixture();
        fixture.SeedInvite((BusinessRole)99);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() =>
            fixture.Redeem().Handle(Token, CancellationToken.None));

        Assert.Equal(MemberInviteRedemptionPolicy.InvalidInviteMessage, refusal.Message);
        Assert.False(fixture.Store.HasMembership(InvitingBusinessId, fixture.Joiner));
    }

    /// <summary>
    /// The race the conditional close exists for: the invite is still live when this redemption
    /// reads it and is used by the time it writes. The membership this attempt created goes back
    /// with the transaction, so one invite grants one membership rather than two.
    /// </summary>
    [Fact]
    public async Task An_invite_used_between_the_read_and_the_write_grants_nothing()
    {
        var fixture = new Fixture();
        fixture.SeedInvite(BusinessRole.Manager);
        fixture.Store.OnceAfterLookup = () => fixture.Store.ChangeBehindTheCallersBack(
            MemberInviteToken.Hash(Token),
            invite => invite with { StoredStatus = MemberInviteStatus.Used, UsedByMembershipId = 500 });

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() =>
            fixture.Redeem().Handle(Token, CancellationToken.None));

        Assert.Equal(MemberInviteRedemptionPolicy.InvalidInviteMessage, refusal.Message);

        // The invite still records the redemption that won, and this attempt is audited as nothing.
        Assert.Equal(500, fixture.Store.InviteFor(MemberInviteToken.Hash(Token)).UsedByMembershipId);
        Assert.Empty(fixture.Audit.Entries);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.Commit, fixture.Operations);
    }

    #endregion

    #region The expiry boundary in time (issue #608)

    /// <summary>
    /// The time-of-check defect issue #608 fixes. The request arrives while the link is still live
    /// and is then held at <c>BEGIN IMMEDIATE</c> while another writer holds the lock; by the time it
    /// is admitted the link has expired. The authoritative decision is the one made inside the
    /// transaction, so the expired link grants nothing - rather than being judged at the instant the
    /// request happened to enter the process.
    /// </summary>
    /// <param name="ticksPastExpiry">
    /// <c>0</c> is the inclusive boundary <see cref="MemberInvitePolicy"/> promises: an invite is
    /// expired *at* its expiry instant, so being admitted exactly then is already too late.
    /// </param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task An_invite_that_expires_while_the_write_lock_is_awaited_grants_nothing(long ticksPastExpiry)
    {
        var fixture = new Fixture();
        fixture.SeedInvite(BusinessRole.Manager, expiresAtUtc: ExpiresAt);
        fixture.WriteStore.WhileWaitingForTheWriteLock = () =>
            fixture.Clock.UtcNow = ExpiresAt.AddTicks(ticksPastExpiry);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() =>
            fixture.Redeem().Handle(Token, CancellationToken.None));

        Assert.Equal(MemberInviteRedemptionPolicy.InvalidInviteMessage, refusal.Message);
        Assert.False(fixture.Store.HasMembership(InvitingBusinessId, fixture.Joiner));
        AssertTheInviteWasNotConsumed(fixture);
    }

    /// <summary>
    /// The second half of the same defect: the link is live when the in-transaction read judges it,
    /// and has expired by the time the invite would be consumed. The conditional close is made at a
    /// fresh instant, so it matches nothing and the membership this attempt prepared goes back with
    /// the transaction - the same refusal the single-use race already produced.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task An_invite_that_expires_before_it_is_consumed_grants_nothing(long ticksPastExpiry)
    {
        var fixture = new Fixture();
        fixture.SeedInvite(BusinessRole.Manager, expiresAtUtc: ExpiresAt);
        fixture.Store.OnceAfterLookup = () => fixture.Clock.UtcNow = ExpiresAt.AddTicks(ticksPastExpiry);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() =>
            fixture.Redeem().Handle(Token, CancellationToken.None));

        Assert.Equal(MemberInviteRedemptionPolicy.InvalidInviteMessage, refusal.Message);
        AssertTheInviteWasNotConsumed(fixture);
    }

    /// <summary>
    /// The same late refusal for somebody rejoining: the revoked row is reactivated and the link has
    /// expired before it can be consumed, so nothing is committed. That the reactivation is really
    /// undone is proved against a real database by <c>MemberInviteRedemptionExpiryTests</c>; what
    /// this asserts is that the use case refuses rather than commits.
    /// </summary>
    [Fact]
    public async Task A_revoked_member_rejoining_commits_nothing_when_the_invite_expires_first()
    {
        var fixture = new Fixture();
        fixture.SeedInvite(BusinessRole.Owner, expiresAtUtc: ExpiresAt);
        fixture.Store.AddMembership(InvitingBusinessId, fixture.Joiner, membershipId: 77, isActive: false);
        fixture.Store.OnceAfterLookup = () => fixture.Clock.UtcNow = ExpiresAt;

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() =>
            fixture.Redeem().Handle(Token, CancellationToken.None));

        Assert.Equal(MemberInviteRedemptionPolicy.InvalidInviteMessage, refusal.Message);
        Assert.Contains(FakeMemberInviteRedemptionStore.ReactivateMembership, fixture.Operations);
        AssertTheInviteWasNotConsumed(fixture);
    }

    /// <summary>
    /// A link that is still live redeems normally however long the request waited, and the instants
    /// recorded describe what really happened rather than the wait: the membership's state instant is
    /// the one the transaction decided at, and the invite's used instant and the audit event's are
    /// the instant it was actually consumed.
    /// </summary>
    [Fact]
    public async Task A_still_live_invite_records_the_instants_the_redemption_really_happened_at()
    {
        var admitted = Now.AddMinutes(1);
        var consumed = Now.AddMinutes(2);

        var fixture = new Fixture();
        fixture.SeedInvite(BusinessRole.Manager, expiresAtUtc: ExpiresAt);
        fixture.WriteStore.WhileWaitingForTheWriteLock = () => fixture.Clock.UtcNow = admitted;
        fixture.Store.OnceAfterLookup = () => fixture.Clock.UtcNow = consumed;

        var redeemed = await fixture.Redeem().Handle(Token, CancellationToken.None);

        Assert.Equal(BusinessRole.Manager, redeemed.Role);

        var membership = fixture.Store.MembershipFor(InvitingBusinessId, fixture.Joiner);
        Assert.Equal(admitted, membership.StatusChangedAtUtc);

        var invite = fixture.Store.InviteFor(MemberInviteToken.Hash(Token));
        Assert.Equal(MemberInviteStatus.Used, invite.StoredStatus);
        Assert.Equal(consumed, invite.UsedAtUtc);

        Assert.Equal(consumed, Assert.Single(fixture.Audit.Entries).OccurredAtUtc);
    }

    /// <summary>
    /// Nothing about the invite changed and nothing was committed or audited: the four facts a late
    /// refusal must leave untouched, in one place because every timing test asserts all of them.
    /// </summary>
    private static void AssertTheInviteWasNotConsumed(Fixture fixture)
    {
        var invite = fixture.Store.InviteFor(MemberInviteToken.Hash(Token));
        Assert.Equal(MemberInviteStatus.Pending, invite.StoredStatus);
        Assert.Null(invite.UsedAtUtc);
        Assert.Null(invite.UsedByMembershipId);

        Assert.Empty(fixture.Audit.Entries);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.Commit, fixture.Operations);
    }

    #endregion

    #region Eligibility

    /// <summary>
    /// The three states that may redeem, as the issue lists them - and the rule that admits them is
    /// <c>MembershipEligibility</c>: all three hold no active membership.
    /// </summary>
    [Theory]
    [InlineData(AccountState.NoMembership)]
    [InlineData(AccountState.MembershipRevoked)]
    [InlineData(AccountState.ApplicationRejected)]
    public async Task The_three_states_with_no_active_membership_may_redeem(AccountState state)
    {
        var fixture = new Fixture(memberships: MembershipsFor(state));
        fixture.SeedInvite(BusinessRole.Manager);

        var redeemed = await fixture.Redeem().Handle(Token, CancellationToken.None);

        Assert.Equal(BusinessRole.Manager, redeemed.Role);
        Assert.Single(fixture.Audit.Entries);
    }

    /// <summary>
    /// The four states that hold an active membership are refused with a conflict before any invite
    /// is looked up, and nothing is written. The invite is deliberately left live: being refused
    /// must not consume somebody else's link.
    /// </summary>
    [Theory]
    [InlineData(AccountState.Member)]
    [InlineData(AccountState.ApplicationPending)]
    [InlineData(AccountState.MembershipAmbiguous)]
    public async Task A_caller_who_already_holds_an_active_membership_is_refused(AccountState state)
    {
        var fixture = new Fixture(memberships: MembershipsFor(state));
        fixture.SeedInvite(BusinessRole.Manager);

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() =>
            fixture.Redeem().Handle(Token, CancellationToken.None));

        Assert.Equal(MembershipEligibility.AlreadyAMemberMessage, refusal.Message);
        Assert.Empty(fixture.Operations);
        Assert.Equal(MemberInviteStatus.Pending, fixture.Store.InviteFor(MemberInviteToken.Hash(Token)).StoredStatus);
        Assert.Empty(fixture.Audit.Entries);
    }

    /// <summary>
    /// The fourth refusal has its own sentence: a caller whose business was deactivated is told they
    /// must first be released by the platform administrator, because no Owner of that business can
    /// be reached to revoke their membership.
    /// </summary>
    [Fact]
    public async Task A_deactivated_business_caller_is_told_to_be_released_first()
    {
        var fixture = new Fixture(memberships: MembershipsFor(AccountState.BusinessDeactivated));
        fixture.SeedInvite(BusinessRole.Manager);

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() =>
            fixture.Redeem().Handle(Token, CancellationToken.None));

        Assert.Equal(MemberInviteRedemptionPolicy.ReleaseRequiredMessage, refusal.Message);
        Assert.Empty(fixture.Operations);
        Assert.Equal(MemberInviteStatus.Pending, fixture.Store.InviteFor(MemberInviteToken.Hash(Token)).StoredStatus);
    }

    /// <summary>
    /// The eligibility question is asked about the identity the request authenticated as, and about
    /// nothing a request could supply.
    /// </summary>
    [Fact]
    public async Task Eligibility_is_asked_about_the_authenticated_identity()
    {
        var fixture = new Fixture();
        fixture.SeedInvite(BusinessRole.Manager);

        await fixture.Redeem().Handle(Token, CancellationToken.None);

        Assert.Equal(fixture.Joiner, fixture.Memberships.LastQueriedActor);
    }

    /// <summary>
    /// A request with no identifiable actor creates nothing: there is nobody to create a membership
    /// for, and the invite is left untouched.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_identifiable_actor_writes_nothing()
    {
        var fixture = new Fixture(profileAccessor: FakeActorProfileAccessor.Unidentifiable());
        fixture.SeedInvite(BusinessRole.Manager);

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(() =>
            fixture.Redeem().Handle(Token, CancellationToken.None));

        Assert.Empty(fixture.Operations);
        Assert.Equal(MemberInviteStatus.Pending, fixture.Store.InviteFor(MemberInviteToken.Hash(Token)).StoredStatus);
        Assert.Empty(fixture.Audit.Entries);
    }

    #endregion

    #region Auditing

    /// <summary>
    /// One audit event per committed redemption, carrying the actor, the business, the invite and
    /// the role - and nothing else. The token and the note are structurally absent: the entry has no
    /// field for either.
    /// </summary>
    [Fact]
    public async Task A_redemption_is_audited_with_the_actor_business_invite_and_role()
    {
        var fixture = new Fixture();
        fixture.SeedInvite(BusinessRole.Manager);

        await fixture.Redeem().Handle(Token, CancellationToken.None);

        var entry = Assert.Single(fixture.Audit.Entries);
        Assert.Equal(Tid, entry.ActorDirectoryTenantId, ignoreCase: true);
        Assert.Equal(JoinerOid, entry.ActorObjectId, ignoreCase: true);
        Assert.Equal(InvitingBusinessId, entry.BusinessId);
        Assert.Equal(InviteId, entry.InviteId);
        Assert.Equal(MemberInviteChange.Redeemed, entry.Change);
        Assert.Equal(BusinessRole.Manager, entry.Role);
        Assert.Equal(Now, entry.OccurredAtUtc);
    }

    /// <summary>
    /// The audit event carries no token in any form, which this asserts over the whole rendered
    /// entry rather than field by field: a field added later that happened to carry one would fail
    /// here.
    /// </summary>
    [Fact]
    public async Task The_audit_event_carries_no_token_in_any_form()
    {
        var fixture = new Fixture();
        fixture.SeedInvite(BusinessRole.Manager);

        await fixture.Redeem().Handle(Token, CancellationToken.None);

        var rendered = Assert.Single(fixture.Audit.Entries).ToString();
        Assert.DoesNotContain(Token, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(MemberInviteToken.Hash(Token), rendered, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    /// <summary>
    /// The membership records an identity in each state the issue names holds. Only the active flag
    /// and the business status matter to either rule, which is the point: the state is a label on
    /// these records, not an input the redemption reads instead of them.
    /// </summary>
    private static ActorBusinessMembership[] MembershipsFor(AccountState state) =>
        state switch
        {
            AccountState.NoMembership => [],
            AccountState.MembershipRevoked => [Membership(OtherBusinessId, false, BusinessStatus.Active)],
            AccountState.ApplicationRejected => [Membership(OtherBusinessId, false, BusinessStatus.Rejected)],
            AccountState.Member => [Membership(OtherBusinessId, true, BusinessStatus.Active)],
            AccountState.ApplicationPending => [Membership(OtherBusinessId, true, BusinessStatus.Pending)],
            AccountState.BusinessDeactivated => [Membership(OtherBusinessId, true, BusinessStatus.Deactivated)],
            AccountState.MembershipAmbiguous =>
            [
                Membership(OtherBusinessId, true, BusinessStatus.Active),
                Membership(OtherBusinessId + 1, true, BusinessStatus.Active),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unhandled account state."),
        };

    private static ActorBusinessMembership Membership(int businessId, bool isActive, BusinessStatus status) =>
        new(
            BusinessId.From(businessId),
            BusinessRole.Operator,
            isActive,
            status,
            Now.AddDays(-10),
            status == BusinessStatus.Rejected ? "Not enough information." : null);

    /// <summary>
    /// The use case over one redemption store, the shared write guard, one audit and one clock, with
    /// every call reported in order so a test can assert what ran as well as what it answered.
    /// </summary>
    private sealed class Fixture
    {
        private readonly FakeActorProfileAccessor _profileAccessor;
        private readonly MemberProfile _profile;

        public Fixture(
            MemberProfile? profile = null,
            FakeActorProfileAccessor? profileAccessor = null,
            ActorBusinessMembership[]? memberships = null)
        {
            Joiner = Identity();
            _profile = profile ?? MemberProfile.None;
            _profileAccessor = profileAccessor ?? FakeActorProfileAccessor.For(Joiner, _profile);
            Memberships = new FakeBusinessMembershipStore(memberships ?? []);
            WriteStore = new FakeBusinessMembershipWriteStore();
            // Both stores report into the guard's own ordered list, so the transaction's boundaries
            // and the redemption's reads and writes are one sequence a test can assert.
            Store = new FakeMemberInviteRedemptionStore { Observer = WriteStore.Record };
        }

        public ActorIdentity Joiner { get; }

        public FakeMemberInviteRedemptionStore Store { get; }

        public FakeBusinessMembershipWriteStore WriteStore { get; }

        public FakeBusinessMembershipStore Memberships { get; }

        public FakeMemberInviteAudit Audit { get; } = new();

        /// <summary>
        /// The one clock the redemption reads, held here so a test can move it on from a store hook
        /// - which is how the timing regressions of issue #608 cross an expiry inside one request
        /// without a wall-clock sleep.
        /// </summary>
        public FakeClock Clock { get; } = new(Now);

        /// <summary>
        /// Every call both stores saw, in one order. The guard's own operations and the
        /// redemption's are interleaved on purpose: "the invite was read inside the transaction" is
        /// only assertable if the two sequences are one.
        /// </summary>
        public IReadOnlyList<string> Operations => WriteStore.Operations;

        public void SeedInvite(
            BusinessRole role,
            MemberInviteStatus status = MemberInviteStatus.Pending,
            DateTime? expiresAtUtc = null,
            BusinessStatus businessStatus = BusinessStatus.Active) =>
            Store.AddInvite(
                MemberInviteToken.Hash(Token),
                InviteId,
                InvitingBusinessId,
                role,
                status,
                expiresAtUtc ?? MemberInvitePolicy.ExpiresAt(Now.AddDays(-1)),
                businessStatus);

        public RedeemMemberInvite Redeem() =>
            new(
                _profileAccessor,
                Memberships,
                Store,
                new MembershipWriteGuard(WriteStore),
                Audit,
                Clock);

        private static ActorIdentity Identity()
        {
            Assert.True(ActorIdentity.TryCreate(Tid, JoinerOid, out var identity));
            return identity!;
        }
    }
}
