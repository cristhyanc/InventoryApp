using System.Buffers.Text;
using Inventory.Application.Members;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Members;

/// <summary>
/// The invite use cases of issue #531, against fakes: what each one refuses, when it writes, what
/// the token is, and what reaches the audit trail.
///
/// The relational and isolation behaviour is proved elsewhere - <c>EfMemberInviteStoreTests</c> and
/// <c>MemberInvitesApiTests</c>. What these tests are for is the decisions, and above all the ones
/// that are invisible in a passing happy path: that the store never sees a plaintext token, that an
/// invite redeemed between the read and the write is not cancelled on the strength of the stale
/// read, and that nothing is audited for a change that did not happen.
/// </summary>
public class MemberInviteTests
{
    private const int BusinessIdValue = 7;
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string ActorOid = "22222222-2222-2222-2222-222222222222";

    private static readonly DateTime Now = new(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc);

    #region Creating

    /// <summary>
    /// The whole creation: the role and the note are stored, the expiry is seven days out, the
    /// invite comes back pending, and the token comes back once.
    /// </summary>
    [Fact]
    public async Task It_creates_a_pending_invite_with_the_fixed_seven_day_expiry()
    {
        var fixture = new Fixture();

        var created = await fixture.Create().Handle("Manager", "  Tuesday run  ", CancellationToken.None);

        Assert.Equal(BusinessRole.Manager, created.Invite.Role);
        Assert.Equal("Tuesday run", created.Invite.Note);
        Assert.Equal(MemberInviteStatus.Pending, created.Invite.Status);
        Assert.Equal(Now, created.Invite.CreatedAtUtc);
        Assert.Equal(Now.AddDays(7), created.Invite.ExpiresAtUtc);
        Assert.Null(created.Invite.UsedAtUtc);
        Assert.Null(created.Invite.CancelledAtUtc);
        Assert.Null(created.Invite.RedeemedByMembershipId);

        Assert.Equal([FakeMemberInviteStore.Create], fixture.Operations);
    }

    /// <summary>
    /// The store is handed the token's hash and never the token itself, so no adapter, no database
    /// row and no log can hold the plaintext - the guarantee is in what crosses the port, not in
    /// what each adapter remembers not to do.
    /// </summary>
    [Fact]
    public async Task The_store_receives_only_the_tokens_hash()
    {
        var fixture = new Fixture();

        var created = await fixture.Create().Handle("Operator", null, CancellationToken.None);

        Assert.Equal(MemberInviteToken.Hash(created.Token), fixture.Store.LastTokenHash);
        Assert.NotEqual(created.Token, fixture.Store.LastTokenHash);
    }

    /// <summary>
    /// Every invite gets its own token, of at least the agreed 128 bits, from the cryptographic
    /// generator: a guessable invite token is a way into somebody else's business.
    /// </summary>
    [Fact]
    public async Task Every_invite_gets_its_own_token_of_at_least_the_agreed_entropy()
    {
        var fixture = new Fixture();
        var create = fixture.Create();

        var tokens = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            tokens.Add((await create.Handle("Operator", null, CancellationToken.None)).Token);
        }

        Assert.Equal(5, tokens.Distinct(StringComparer.Ordinal).Count());
        Assert.All(
            tokens,
            token => Assert.True(
                Base64Url.DecodeFromChars(token).Length >= MemberInviteToken.MinimumEntropyBytes));
    }

    /// <summary>
    /// The submitted role is checked against the declared vocabulary before anything else happens -
    /// before the business is resolved, a token is generated or a row is written - so an unsupported
    /// value cannot reach a write or the audit trail.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Administrator")]
    [InlineData("Viewer")]
    [InlineData("40")]
    public async Task An_unsupported_role_name_is_refused_before_anything_is_written(string? role)
    {
        var fixture = new Fixture();

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() =>
            fixture.Create().Handle(role, null, CancellationToken.None));

        Assert.Equal(BusinessRoles.UnsupportedMessage, refusal.Message);
        Assert.Empty(fixture.Operations);
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    public async Task An_over_long_note_is_refused_before_anything_is_written()
    {
        var fixture = new Fixture();

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() =>
            fixture.Create().Handle("Manager", new string('n', 201), CancellationToken.None));

        Assert.Equal(MemberInvitePolicy.NoteTooLongMessage, refusal.Message);
        Assert.Empty(fixture.Operations);
        Assert.Empty(fixture.Audit.Entries);
    }

    /// <summary>
    /// One audit event per creation, carrying the actor, the business, the invite and the role -
    /// and nothing else. The note and the token are structurally absent: the entry has no field for
    /// either.
    /// </summary>
    [Fact]
    public async Task A_creation_is_audited_with_the_actor_business_invite_and_role()
    {
        var fixture = new Fixture();

        var created = await fixture.Create().Handle("Manager", "Invite for Ada", CancellationToken.None);

        var entry = Assert.Single(fixture.Audit.Entries);
        Assert.Equal(Tid, entry.ActorDirectoryTenantId);
        Assert.Equal(ActorOid, entry.ActorObjectId);
        Assert.Equal(BusinessIdValue, entry.BusinessId);
        Assert.Equal(created.Invite.InviteId, entry.InviteId);
        Assert.Equal(MemberInviteChange.Created, entry.Change);
        Assert.Equal(BusinessRole.Manager, entry.Role);
        Assert.Equal(Now, entry.OccurredAtUtc);
    }

    [Fact]
    public async Task A_caller_with_no_usable_membership_creates_nothing()
    {
        var fixture = new Fixture(FakeCurrentBusinessProvider.Denied());

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(() =>
            fixture.Create().Handle("Manager", null, CancellationToken.None));

        Assert.Empty(fixture.Operations);
        Assert.Empty(fixture.Audit.Entries);
    }

    #endregion

    #region Listing

    /// <summary>
    /// The list reports every state, with expiry derived at one instant for the whole response - so
    /// two invites that expire a tick apart are not judged against two different "nows".
    /// </summary>
    [Fact]
    public async Task It_lists_every_invite_with_its_state_at_one_instant()
    {
        var fixture = new Fixture();
        fixture.Store.Add(Invite(1));
        fixture.Store.Add(Invite(2, expiresAtUtc: Now));
        fixture.Store.Add(Invite(3, status: MemberInviteStatus.Used, usedAtUtc: Now.AddDays(-1)));
        fixture.Store.Add(Invite(4, status: MemberInviteStatus.Cancelled, cancelledAtUtc: Now.AddDays(-1)));
        fixture.Store.Add(Invite(5, storedStatusValue: 99));

        var invites = await fixture.List().Handle(CancellationToken.None);

        Assert.Equal(
            [
                MemberInviteStatus.Pending,
                MemberInviteStatus.Expired,
                MemberInviteStatus.Used,
                MemberInviteStatus.Cancelled,
                null,
            ],
            invites.Select(invite => invite.Status));
    }

    [Fact]
    public async Task A_caller_with_no_usable_membership_lists_nothing()
    {
        var fixture = new Fixture(FakeCurrentBusinessProvider.Denied());
        fixture.Store.Add(Invite(1));

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(() =>
            fixture.List().Handle(CancellationToken.None));

        Assert.Empty(fixture.Operations);
    }

    #endregion

    #region Cancelling

    [Fact]
    public async Task Cancelling_a_live_invite_writes_once_and_audits_once()
    {
        var fixture = new Fixture();
        fixture.Store.Add(Invite(1, role: BusinessRole.Operator));

        var cancelled = await fixture.Cancel().Handle(1, CancellationToken.None);

        Assert.NotNull(cancelled);
        Assert.Equal(MemberInviteStatus.Cancelled, cancelled.Status);
        Assert.Equal(Now, cancelled.CancelledAtUtc);
        Assert.Equal(MemberInviteStatus.Cancelled, fixture.Store.Row(1).StoredStatus);

        // The read that tells "no such invite" from "here is what became of it", then the one
        // conditional write - and no second read, because nothing else about an invite can change.
        Assert.Equal([FakeMemberInviteStore.Find, FakeMemberInviteStore.Cancel], fixture.Operations);

        var entry = Assert.Single(fixture.Audit.Entries);
        Assert.Equal(MemberInviteChange.Cancelled, entry.Change);
        Assert.Equal(BusinessRole.Operator, entry.Role);
        Assert.Equal(1, entry.InviteId);
        Assert.Equal(BusinessIdValue, entry.BusinessId);
    }

    [Fact]
    public async Task An_invite_the_business_does_not_hold_is_not_found_and_nothing_is_written()
    {
        var fixture = new Fixture();
        fixture.Store.Add(Invite(1));

        Assert.Null(await fixture.Cancel().Handle(2, CancellationToken.None));

        Assert.Equal([FakeMemberInviteStore.Find], fixture.Operations);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Equal(MemberInviteStatus.Pending, fixture.Store.Row(1).StoredStatus);
    }

    [Fact]
    public async Task Cancelling_an_already_cancelled_invite_writes_nothing_and_audits_nothing()
    {
        var fixture = new Fixture();
        var cancelledAt = Now.AddDays(-2);
        fixture.Store.Add(Invite(1, status: MemberInviteStatus.Cancelled, cancelledAtUtc: cancelledAt));

        var answer = await fixture.Cancel().Handle(1, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Equal(MemberInviteStatus.Cancelled, answer.Status);
        // The original instant survives: a replay must not re-stamp when the invite was withdrawn.
        Assert.Equal(cancelledAt, answer.CancelledAtUtc);
        Assert.Equal(cancelledAt, fixture.Store.Row(1).CancelledAtUtc);
        Assert.Empty(fixture.Audit.Entries);
    }

    [Theory]
    [InlineData(MemberInviteStatus.Used, CancelMemberInvite.AlreadyUsedMessage)]
    public async Task A_used_invite_is_refused_with_the_domains_own_sentence(
        MemberInviteStatus status,
        string message)
    {
        var fixture = new Fixture();
        fixture.Store.Add(Invite(1, status: status, usedAtUtc: Now.AddDays(-1), redeemedByMembershipId: 42));

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() =>
            fixture.Cancel().Handle(1, CancellationToken.None));

        Assert.Equal(message, refusal.Message);
        Assert.Equal(status, fixture.Store.Row(1).StoredStatus);
        Assert.Null(fixture.Store.Row(1).CancelledAtUtc);
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    public async Task An_expired_invite_is_refused_and_left_as_it_is()
    {
        var fixture = new Fixture();
        fixture.Store.Add(Invite(1, expiresAtUtc: Now.AddTicks(-1)));

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() =>
            fixture.Cancel().Handle(1, CancellationToken.None));

        Assert.Equal(CancelMemberInvite.AlreadyExpiredMessage, refusal.Message);
        Assert.Equal(MemberInviteStatus.Pending, fixture.Store.Row(1).StoredStatus);
        Assert.Null(fixture.Store.Row(1).CancelledAtUtc);
        Assert.Empty(fixture.Audit.Entries);
    }

    /// <summary>
    /// An invite whose stored state this deployment does not declare is refused rather than
    /// rewritten into a state nobody can explain. It is already unusable - nothing but a declared
    /// pending state is live - so the fail-closed answer costs nothing.
    /// </summary>
    [Fact]
    public async Task An_invite_in_an_undeclared_state_is_refused()
    {
        var fixture = new Fixture();
        fixture.Store.Add(Invite(1, storedStatusValue: 99));

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() =>
            fixture.Cancel().Handle(1, CancellationToken.None));

        Assert.Equal(CancelMemberInvite.NotCancellableMessage, refusal.Message);
        Assert.Empty(fixture.Audit.Entries);
    }

    /// <summary>
    /// The regression this feature's one real race needs: an invite redeemed <em>after</em> the
    /// cancellation read it and <em>before</em> the write lands is not cancelled. The condition
    /// travels in the write itself, so the write matches nothing, the use case re-reads, and the
    /// caller is told the invite was used - rather than a used invite being recorded as cancelled
    /// while the membership it created carries on existing.
    /// </summary>
    [Fact]
    public async Task An_invite_redeemed_between_the_read_and_the_write_is_not_cancelled()
    {
        var fixture = new Fixture();
        fixture.Store.Add(Invite(1));
        fixture.Store.OnceAfterFind = () => fixture.Store.ChangeBehindTheCallersBack(
            1,
            row => row with
            {
                StoredStatus = MemberInviteStatus.Used,
                UsedAtUtc = Now,
                RedeemedByMembershipId = 42,
            });

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() =>
            fixture.Cancel().Handle(1, CancellationToken.None));

        Assert.Equal(CancelMemberInvite.AlreadyUsedMessage, refusal.Message);

        var row = fixture.Store.Row(1);
        Assert.Equal(MemberInviteStatus.Used, row.StoredStatus);
        Assert.Null(row.CancelledAtUtc);
        Assert.Equal(42, row.RedeemedByMembershipId);
        Assert.Empty(fixture.Audit.Entries);

        // Read, conditional write that declined, and the re-read that explains it.
        Assert.Equal(
            [FakeMemberInviteStore.Find, FakeMemberInviteStore.Cancel, FakeMemberInviteStore.Find],
            fixture.Operations);
    }

    [Fact]
    public async Task A_caller_with_no_usable_membership_cancels_nothing()
    {
        var fixture = new Fixture(FakeCurrentBusinessProvider.Denied());
        fixture.Store.Add(Invite(1));

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(() =>
            fixture.Cancel().Handle(1, CancellationToken.None));

        Assert.Empty(fixture.Operations);
        Assert.Equal(MemberInviteStatus.Pending, fixture.Store.Row(1).StoredStatus);
    }

    #endregion

    #region Cancelling all of a business's live invites

    /// <summary>
    /// The store method #509's deactivation calls: every live invite is cancelled, and nothing else
    /// is touched. A used invite must keep saying it was used, and an invite that expired or was
    /// cancelled on its own must keep the instant it was closed at - which is also what makes a
    /// second call idempotent, as a restarted deactivation would make.
    /// </summary>
    [Fact]
    public async Task Cancelling_all_live_invites_closes_only_the_live_ones_and_is_idempotent()
    {
        var store = new FakeMemberInviteStore();
        var closedAt = Now.AddDays(-3);
        store.Add(Invite(1));
        store.Add(Invite(2));
        store.Add(Invite(3, status: MemberInviteStatus.Used, usedAtUtc: closedAt, redeemedByMembershipId: 42));
        store.Add(Invite(4, status: MemberInviteStatus.Cancelled, cancelledAtUtc: closedAt));
        store.Add(Invite(5, expiresAtUtc: Now.AddTicks(-1)));

        var cancelled = await store.CancelAllLiveAsync(Now, CancellationToken.None);

        Assert.Equal(2, cancelled);
        Assert.Equal(MemberInviteStatus.Cancelled, store.Row(1).StoredStatus);
        Assert.Equal(Now, store.Row(1).CancelledAtUtc);
        Assert.Equal(MemberInviteStatus.Cancelled, store.Row(2).StoredStatus);
        Assert.Equal(MemberInviteStatus.Used, store.Row(3).StoredStatus);
        Assert.Equal(42, store.Row(3).RedeemedByMembershipId);
        Assert.Equal(closedAt, store.Row(4).CancelledAtUtc);
        Assert.Equal(MemberInviteStatus.Pending, store.Row(5).StoredStatus);
        Assert.Null(store.Row(5).CancelledAtUtc);

        // Idempotent: a second call finds nothing live and changes nothing.
        Assert.Equal(0, await store.CancelAllLiveAsync(Now.AddMinutes(1), CancellationToken.None));
        Assert.Equal(Now, store.Row(1).CancelledAtUtc);
    }

    #endregion

    private static StoredMemberInvite Invite(
        int inviteId,
        BusinessRole role = BusinessRole.Manager,
        string? note = null,
        MemberInviteStatus status = MemberInviteStatus.Pending,
        int? storedStatusValue = null,
        DateTime? expiresAtUtc = null,
        DateTime? usedAtUtc = null,
        DateTime? cancelledAtUtc = null,
        int? redeemedByMembershipId = null) =>
        new(
            inviteId,
            role,
            note,
            storedStatusValue is { } undeclared ? (MemberInviteStatus)undeclared : status,
            Now.AddDays(-1),
            expiresAtUtc ?? MemberInvitePolicy.ExpiresAt(Now.AddDays(-1)),
            usedAtUtc,
            cancelledAtUtc,
            redeemedByMembershipId);

    /// <summary>
    /// The three use cases over one store, one audit and one clock, with every call reported in
    /// order so a test can assert what ran as well as what it answered.
    /// </summary>
    private sealed class Fixture
    {
        private readonly ICurrentBusinessProvider _currentBusiness;
        private readonly List<string> _operations = [];

        public Fixture(ICurrentBusinessProvider? currentBusiness = null)
        {
            _currentBusiness = currentBusiness ?? new FakeCurrentBusinessProvider(BusinessIdValue);
            Store = new FakeMemberInviteStore { Observer = _operations.Add };
        }

        public FakeMemberInviteStore Store { get; }

        public FakeMemberInviteAudit Audit { get; } = new();

        public IReadOnlyList<string> Operations => _operations;

        private FakeAuthenticatedActorAccessor Actor { get; } =
            FakeAuthenticatedActorAccessor.Identified(Identity());

        private FakeClock Clock { get; } = new(Now);

        public CreateMemberInvite Create() => new(_currentBusiness, Actor, Store, Audit, Clock);

        public ListMemberInvites List() => new(_currentBusiness, Store, Clock);

        public CancelMemberInvite Cancel() => new(_currentBusiness, Actor, Store, Audit, Clock);

        private static ActorIdentity Identity()
        {
            Assert.True(ActorIdentity.TryCreate(Tid, ActorOid, out var identity));
            return identity!;
        }
    }
}
