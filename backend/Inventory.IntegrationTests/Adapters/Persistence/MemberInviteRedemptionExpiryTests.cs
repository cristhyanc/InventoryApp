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
/// The invite redemption's expiry boundary <em>in time</em> (issue #608), against a real SQLite file
/// with the real migration history through <see cref="MembershipWriteRaceHarness"/>.
///
/// <para><strong>What went wrong.</strong> The redemption used to read the clock when the request
/// entered the process and judge the invite, create the membership and close the invite by that one
/// instant. Between reading it and acting on it the request waits for <c>BEGIN IMMEDIATE</c> to grant
/// the write lock - for as long as any other membership write holds it - so a link that expired
/// during the wait still satisfied every check, including the conditional <c>UPDATE</c>'s
/// <c>ExpiresAtUtc &gt; usedAtUtc</c>, and was consumed after expiry. An expired invite granting
/// access is an authorization failure, not a cosmetic one.</para>
///
/// <para><strong>Why these tests are relational.</strong> The decisions are asserted over fakes in
/// <c>RedeemMemberInviteTests</c>, which is where the interleavings are cheap to describe. What a
/// fake cannot prove is the half that matters most here: that a refusal at the very last step really
/// takes the membership row back. The insert, the conditional reactivation and the conditional close
/// all have to roll back together with the transaction they were saved in, so these run the real
/// <see cref="MembershipWriteGuard"/>, the real <see cref="EfMemberInviteRedemptionStore"/> and the
/// real <see cref="EfBusinessMembershipWriteStore"/> over a migrated database and read the rows back
/// on a separate connection.</para>
///
/// <para><strong>Nothing sleeps.</strong> Time moves because a hook moves a <see cref="FakeClock"/>:
/// one standing in for the wait at <c>BEGIN IMMEDIATE</c>, and one for the gap between the
/// authoritative read and the consumption. Both are deterministic, which a wall-clock wait across a
/// real lock would not be.</para>
/// </summary>
public class MemberInviteRedemptionExpiryTests : IAsyncDisposable
{
    private const int InvitingBusinessId = 1;
    private const int OtherBusinessId = 2;

    private const string JoinerOid = "22222222-2222-2222-2222-222222222222";

    /// <summary>When the request entered the process, while the link was still live.</summary>
    private static readonly DateTime Arrived = MembershipWriteRaceHarness.SeededAt.AddHours(1);

    /// <summary>The seeded invite's expiry, deliberately just ahead of <see cref="Arrived"/>.</summary>
    private static readonly DateTime ExpiresAt = Arrived.AddMinutes(5);

    private readonly MembershipWriteRaceHarness _harness =
        MembershipWriteRaceHarness.Create(InvitingBusinessId, OtherBusinessId);

    private readonly FakeClock _clock = new(Arrived);
    private readonly FakeMemberInviteAudit _audit = new();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    /// <summary>
    /// The request arrives while the link is live and is admitted after it has expired. The
    /// authoritative decision is made at the instant the lock was granted, so the link grants
    /// nothing and nothing at all is written - not even the membership row, because the invite is
    /// refused before one is prepared.
    /// </summary>
    /// <param name="ticksPastExpiry">
    /// <c>0</c> is the inclusive boundary <c>MemberInvitePolicy</c> promises: an invite is expired
    /// *at* its expiry instant, so being admitted exactly then is already too late.
    /// </param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task An_invite_that_expires_while_the_write_lock_is_awaited_grants_nothing(long ticksPastExpiry)
    {
        var inviteId = _harness.SeedInvite(
            InvitingBusinessId, "the-link", BusinessRole.Manager, expiresAtUtc: ExpiresAt);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() => RedeemAsync(
            "the-link",
            whileAcquiringTheWriteLock: () => _clock.UtcNow = ExpiresAt.AddTicks(ticksPastExpiry)));

        Assert.Equal(MemberInviteRedemptionPolicy.InvalidInviteMessage, refusal.Message);
        Assert.Empty(_harness.Memberships());
        AssertTheInviteWasNotConsumed(inviteId);
    }

    /// <summary>
    /// The link is live when the in-transaction read judges it, and has expired by the time it would
    /// be consumed. The conditional close is made at a fresh instant, matches nothing, and the
    /// membership row this attempt already inserted and saved goes back with the transaction - which
    /// is the whole reason the refusal happens inside it.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task A_new_membership_is_rolled_back_when_the_invite_expires_before_it_is_consumed(
        long ticksPastExpiry)
    {
        var inviteId = _harness.SeedInvite(
            InvitingBusinessId, "the-link", BusinessRole.Manager, expiresAtUtc: ExpiresAt);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() => RedeemAsync(
            "the-link",
            afterTheAuthoritativeRead: () => _clock.UtcNow = ExpiresAt.AddTicks(ticksPastExpiry)));

        Assert.Equal(MemberInviteRedemptionPolicy.InvalidInviteMessage, refusal.Message);
        Assert.Empty(_harness.Memberships());
        AssertTheInviteWasNotConsumed(inviteId);
    }

    /// <summary>
    /// The same late refusal for somebody rejoining a business that revoked them. The reactivation is
    /// an <c>UPDATE</c> rather than an insert, so "nothing was committed" is a different assertion:
    /// the row is still there, still revoked, still holding the role and the state instant it was
    /// seeded with, rather than quietly carrying the expired invite's role and today's instant.
    /// </summary>
    [Fact]
    public async Task A_reactivated_membership_is_rolled_back_when_the_invite_expires_before_it_is_consumed()
    {
        var inviteId = _harness.SeedInvite(
            InvitingBusinessId, "come-back", BusinessRole.Owner, expiresAtUtc: ExpiresAt);
        var membershipId = _harness.SeedMembership(
            InvitingBusinessId, JoinerOid, BusinessRole.Operator, isActive: false);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() => RedeemAsync(
            "come-back",
            afterTheAuthoritativeRead: () => _clock.UtcNow = ExpiresAt));

        Assert.Equal(MemberInviteRedemptionPolicy.InvalidInviteMessage, refusal.Message);

        var membership = Assert.Single(_harness.Memberships());
        Assert.Equal(membershipId, membership.Id);
        Assert.False(membership.IsActive);
        Assert.Equal(BusinessRole.Operator, membership.Role);
        Assert.Equal(MembershipWriteRaceHarness.SeededAt, membership.StatusChangedAtUtc);

        AssertTheInviteWasNotConsumed(inviteId);
    }

    /// <summary>
    /// A link that is still live redeems normally however long the request waited, and the instants
    /// persisted describe what happened rather than the waiting: the membership records the instant
    /// the transaction admitted this person, and the invite records the instant it was really
    /// consumed - which is also the instant the audit event reports.
    /// </summary>
    [Fact]
    public async Task A_still_live_invite_redeems_and_records_the_instants_it_really_happened_at()
    {
        var admitted = Arrived.AddMinutes(1);
        var consumed = Arrived.AddMinutes(2);

        var inviteId = _harness.SeedInvite(
            InvitingBusinessId, "still-live", BusinessRole.Manager, expiresAtUtc: ExpiresAt);

        await RedeemAsync(
            "still-live",
            whileAcquiringTheWriteLock: () => _clock.UtcNow = admitted,
            afterTheAuthoritativeRead: () => _clock.UtcNow = consumed);

        var membership = Assert.Single(_harness.Memberships());
        Assert.True(membership.IsActive);
        Assert.Equal(BusinessRole.Manager, membership.Role);
        Assert.Equal(admitted, membership.CreatedAtUtc);
        Assert.Equal(admitted, membership.StatusChangedAtUtc);

        var invite = Assert.Single(_harness.Invites());
        Assert.Equal(inviteId, invite.Id);
        Assert.Equal(MemberInviteStatus.Used, invite.Status);
        Assert.Equal(consumed, invite.UsedAtUtc);
        Assert.Equal(membership.Id, invite.RedeemedByMembershipId);

        Assert.Equal(consumed, Assert.Single(_audit.Entries).OccurredAtUtc);
    }

    /// <summary>
    /// The invite is exactly as it was seeded and nothing was audited: what a late refusal must
    /// leave behind, asserted the same way by every refusal here.
    /// </summary>
    private void AssertTheInviteWasNotConsumed(int inviteId)
    {
        var invite = Assert.Single(_harness.Invites());
        Assert.Equal(inviteId, invite.Id);
        Assert.Equal(MemberInviteStatus.Pending, invite.Status);
        Assert.Null(invite.UsedAtUtc);
        Assert.Null(invite.RedeemedByMembershipId);

        Assert.Empty(_audit.Entries);
    }

    /// <summary>
    /// One real redemption on its own connection, as a request makes it: a denied business scope,
    /// the real guard and the real stores, and the two hooks that move the clock on at the points
    /// where a real request can lose time.
    /// </summary>
    private async Task RedeemAsync(
        string token,
        Action? whileAcquiringTheWriteLock = null,
        Action? afterTheAuthoritativeRead = null)
    {
        var identity = MembershipWriteRaceHarness.Actor(JoinerOid);

        // Denied, because somebody joining a business is not yet a member of any - which is exactly
        // the scope the membership-not-required endpoint gives this request.
        await using var db = TestAppDbContext.Denied(_harness.Options);

        var redeem = new RedeemMemberInvite(
            FakeActorProfileAccessor.For(identity, MemberProfile.None),
            new EfBusinessMembershipStore(db),
            new HookedRedemptionStore(new EfMemberInviteRedemptionStore(db), afterTheAuthoritativeRead),
            new MembershipWriteGuard(
                new HookedMembershipWriteStore(
                    new EfBusinessMembershipWriteStore(db),
                    whileAcquiringTheWriteLock)),
            _audit,
            _clock);

        await redeem.Handle(token, CancellationToken.None);
    }

    /// <summary>
    /// The real redemption adapter with one hook, run once the authoritative in-transaction read has
    /// answered: the point at which a real request still has its membership to prepare before it can
    /// consume the invite. Every other call goes straight through, so what is being tested is the
    /// real SQL.
    /// </summary>
    private sealed class HookedRedemptionStore(IMemberInviteRedemptionStore inner, Action? afterLookup)
        : IMemberInviteRedemptionStore
    {
        private Action? _afterLookup = afterLookup;

        public async Task<RedeemableMemberInvite?> FindByTokenHashAsync(
            string tokenHash,
            CancellationToken cancellationToken)
        {
            var found = await inner.FindByTokenHashAsync(tokenHash, cancellationToken);

            var staged = _afterLookup;
            _afterLookup = null;
            staged?.Invoke();

            return found;
        }

        public Task<RedeemingMemberMembership?> FindMembershipAsync(
            BusinessId businessId,
            ActorIdentity identity,
            CancellationToken cancellationToken) =>
            inner.FindMembershipAsync(businessId, identity, cancellationToken);

        public Task<int> CreateActiveMembershipAsync(
            BusinessId businessId,
            ActorIdentity identity,
            MemberProfile profile,
            BusinessRole role,
            DateTime createdAtUtc,
            CancellationToken cancellationToken) =>
            inner.CreateActiveMembershipAsync(
                businessId, identity, profile, role, createdAtUtc, cancellationToken);

        public Task<bool> ReactivateMembershipAsync(
            BusinessId businessId,
            int membershipId,
            BusinessRole role,
            MemberProfile profile,
            DateTime reactivatedAtUtc,
            CancellationToken cancellationToken) =>
            inner.ReactivateMembershipAsync(
                businessId, membershipId, role, profile, reactivatedAtUtc, cancellationToken);

        public Task<bool> MarkUsedAsync(
            BusinessId businessId,
            int inviteId,
            int membershipId,
            DateTime usedAtUtc,
            CancellationToken cancellationToken) =>
            inner.MarkUsedAsync(businessId, inviteId, membershipId, usedAtUtc, cancellationToken);
    }

    /// <summary>
    /// The real membership write store with one hook, run before <c>BEGIN IMMEDIATE</c> returns:
    /// the deterministic stand-in for the time a writer spends blocked while another holds the write
    /// lock. The transaction it hands back is the real one, so a refusal still rolls back for real.
    /// </summary>
    private sealed class HookedMembershipWriteStore(
        IBusinessMembershipWriteStore inner,
        Action? whileAcquiringTheLock) : IBusinessMembershipWriteStore
    {
        public Task<IBusinessMembershipWriteTransaction> BeginWriteTransactionAsync(
            CancellationToken cancellationToken)
        {
            whileAcquiringTheLock?.Invoke();
            return inner.BeginWriteTransactionAsync(cancellationToken);
        }

        public Task<IReadOnlyList<IdentityMembership>> FindIdentityMembershipsAsync(
            ActorIdentity identity,
            CancellationToken cancellationToken) =>
            inner.FindIdentityMembershipsAsync(identity, cancellationToken);

        public Task<IReadOnlyList<BusinessMembershipRole>> FindBusinessMembershipRolesAsync(
            BusinessId businessId,
            CancellationToken cancellationToken) =>
            inner.FindBusinessMembershipRolesAsync(businessId, cancellationToken);

        public Task<BusinessStatus?> FindBusinessStatusAsync(
            BusinessId businessId,
            CancellationToken cancellationToken) =>
            inner.FindBusinessStatusAsync(businessId, cancellationToken);

        public bool IsDuplicateActiveMembership(Exception exception) =>
            inner.IsDuplicateActiveMembership(exception);
    }
}
