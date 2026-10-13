using Inventory.Application.Members;
using Inventory.Application.PlatformAdmin;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Members;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.PlatformAdmin;

/// <summary>
/// Switching a business off and on again, and releasing its members while it is off (issue #545):
/// the rules, the order they are applied in, what each action writes, and what each refusal leaves
/// behind.
///
/// <para>These tests are over the real <see cref="MembershipWriteGuard"/> with the membership write
/// port faked, because the guard's transaction is where the facts that matter are re-read. Two
/// properties are what the fakes exist to make visible: that the invite cancellation and the
/// deactivation hook happen <em>between</em> the transaction opening and the commit, and that a
/// release never asks the guard its "keep an active Owner" question. The relational halves - the
/// columns each write touches, and the cross-business boundary - are in
/// <c>EfPlatformBusinessDirectoryStoreTests</c>, and the HTTP half in
/// <c>SuperAdminBusinessLifecycleApiTests</c>.</para>
/// </summary>
public class BusinessLifecycleTests
{
    // The business every test acts on. It is FakeMemberInviteStore's own default owner, so a seeded
    // invite belongs to this business unless a test says otherwise.
    private const int BusinessId = FakeMemberInviteStore.OwningBusinessId;
    private const int AnotherBusinessId = BusinessId + 1;
    private const int OwnerMembershipId = 41;
    private const int ManagerMembershipId = 42;
    private const string DirectoryTenantId = "11111111-1111-1111-1111-111111111111";
    private const string AdministratorObjectId = "99999999-9999-9999-9999-999999999999";

    private static readonly DateTime ActedAt = new(2026, 10, 13, 2, 15, 0, DateTimeKind.Utc);
    private static readonly DateTime SeededAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);

    private readonly FakePlatformBusinessDirectoryStore _store = new();
    private readonly FakeMemberInviteStore _invites = new();
    private readonly FakeBusinessMembershipWriteStore _writeStore = new();
    private readonly FakeBusinessDeactivationHook _hook = new();
    private readonly FakePlatformBusinessLifecycleAudit _audit = new();

    public BusinessLifecycleTests()
    {
        // Both report into the write store's own list, so one assertion can read the whole order:
        // the transaction opening, the invite cancellation, the hook, and the commit.
        _invites.Observer = _writeStore.Record;
        _hook.Observer = _writeStore.Record;
    }

    #region Deactivating

    /// <summary>
    /// The criterion: one transaction sets the status and cancels every invite of that business that
    /// is still live, and the count is reported. An invite that was already closed, and another
    /// business's live invite, are both untouched.
    /// </summary>
    [Fact]
    public async Task Deactivating_sets_the_status_and_cancels_every_live_invite_of_that_business()
    {
        GivenBusiness(BusinessStatus.Active);
        var firstLive = GivenInvite();
        var secondLive = GivenInvite();
        var alreadyCancelled = GivenInvite(MemberInviteStatus.Cancelled);
        var anotherBusinesss = GivenInvite(businessId: AnotherBusinessId);

        var deactivation = await Deactivate().Handle(BusinessId, CancellationToken.None);

        Assert.NotNull(deactivation);
        Assert.Equal(BusinessId, deactivation!.BusinessId);
        Assert.Equal("Vending Co", deactivation.Name);
        Assert.Equal(BusinessStatus.Deactivated, deactivation.Status);
        Assert.Equal(2, deactivation.InvitesCancelled);

        Assert.Contains(FakePlatformBusinessDirectoryStore.Deactivate, _store.Operations);
        Assert.Equal(MemberInviteStatus.Cancelled, _invites.Row(firstLive).StoredStatus);
        Assert.Equal(ActedAt, _invites.Row(secondLive).CancelledAtUtc);

        // A cancellation records a deliberate decision at an instant: rewriting an invite that was
        // already closed would make the record claim something that did not happen.
        Assert.Equal(SeededAt, _invites.Row(alreadyCancelled).CancelledAtUtc);
        Assert.Equal(MemberInviteStatus.Pending, _invites.Row(anotherBusinesss).StoredStatus);
    }

    /// <summary>
    /// The status write, the invite cancellation and the hook are all inside one transaction, in that
    /// order, and the audit event comes after the commit. The order is the point: a hook that ran
    /// before the status was written could not see what it is reacting to, and an invite cancelled
    /// outside the transaction would survive a rolled-back deactivation.
    /// </summary>
    [Fact]
    public async Task Deactivating_runs_the_invite_cancellation_and_the_hook_inside_the_transaction()
    {
        GivenBusiness(BusinessStatus.Active);
        GivenInvite();

        await Deactivate().Handle(BusinessId, CancellationToken.None);

        Assert.Equal(
            [
                FakeBusinessMembershipWriteStore.Begin,
                FakeMemberInviteStore.CancelAll,
                FakeBusinessDeactivationHook.Hook,
                FakeBusinessMembershipWriteStore.Commit,
                FakeBusinessMembershipWriteStore.Dispose,
            ],
            _writeStore.Operations);

        // The hook is told the business and the instant, and nothing else about it.
        Assert.Equal(new BusinessDeactivated(BusinessId, ActedAt), _hook.Deactivated);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(DirectoryTenantId, entry.ActorDirectoryTenantId);
        Assert.Equal(AdministratorObjectId, entry.ActorObjectId);
        Assert.Equal(BusinessId, entry.BusinessId);
        Assert.Equal(PlatformBusinessLifecycleChange.Deactivated, entry.Change);
        Assert.Equal(BusinessStatus.Active, entry.FromStatus);
        Assert.Equal(BusinessStatus.Deactivated, entry.ToStatus);
        Assert.Null(entry.MembershipId);
        Assert.Equal(1, entry.InvitesCancelled);
        Assert.Equal(ActedAt, entry.OccurredAtUtc);
    }

    /// <summary>
    /// A hook that refuses refuses the whole deactivation: nothing is committed and nothing is
    /// audited. That is the direction an operation about <em>stopping</em> access has to fail in -
    /// a business recorded as deactivated while whatever the hook was meant to end is still running
    /// would be exactly the access the deactivation exists to stop.
    /// </summary>
    [Fact]
    public async Task A_deactivation_hook_that_refuses_rolls_the_whole_deactivation_back()
    {
        GivenBusiness(BusinessStatus.Active);
        _hook.Refusal = new InvalidOperationException("the hook refused");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Deactivate().Handle(BusinessId, CancellationToken.None));

        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.Commit, _writeStore.Operations);
        Assert.Empty(_audit.Entries);
    }

    /// <summary>
    /// Only a trading business may be switched off. A pending application, a refused one, an
    /// already-deactivated business and an undeclared stored status are each refused with the one
    /// sentence, and nothing at all is written - no status, no invite, no hook and no audit event.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Pending)]
    [InlineData(BusinessStatus.Rejected)]
    [InlineData(BusinessStatus.Deactivated)]
    [InlineData((BusinessStatus)0)]
    [InlineData((BusinessStatus)99)]
    public async Task Only_an_active_business_may_be_deactivated(BusinessStatus status)
    {
        GivenBusiness(status);
        var live = GivenInvite();

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => Deactivate().Handle(BusinessId, CancellationToken.None));

        Assert.Equal(BusinessLifecyclePolicy.NotActiveMessage, conflict.Message);
        Assert.Equal(MemberInviteStatus.Pending, _invites.Row(live).StoredStatus);
        Assert.Null(_hook.Deactivated);
        AssertNothingWasWritten();
    }

    /// <summary>
    /// The authoritative read is inside the write transaction, not wherever the caller last looked.
    /// The status changes while this deactivation waits for the write lock - which is what another
    /// administrator's action, or a rejection, committing looks like - and the re-read inside the
    /// lock refuses it.
    /// </summary>
    [Fact]
    public async Task A_status_that_changes_while_the_write_lock_is_awaited_refuses_the_deactivation()
    {
        GivenBusiness(BusinessStatus.Active);
        var live = GivenInvite();
        _writeStore.WhileWaitingForTheWriteLock = () => SetStatus(BusinessStatus.Deactivated);

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => Deactivate().Handle(BusinessId, CancellationToken.None));

        Assert.Equal(BusinessLifecyclePolicy.NotActiveMessage, conflict.Message);
        Assert.DoesNotContain(FakePlatformBusinessDirectoryStore.Deactivate, _store.Operations);
        Assert.Equal(MemberInviteStatus.Pending, _invites.Row(live).StoredStatus);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.Commit, _writeStore.Operations);
        Assert.Empty(_audit.Entries);
    }

    #endregion

    #region Reactivating

    /// <summary>
    /// The criterion: a deactivated business with at least one active Owner comes back, and the
    /// action restores nothing else - no membership and no invite, which is why neither store is
    /// asked to.
    /// </summary>
    [Fact]
    public async Task Reactivating_a_business_with_an_active_owner_sets_the_status_and_nothing_else()
    {
        GivenBusiness(BusinessStatus.Deactivated);
        var cancelled = GivenInvite(MemberInviteStatus.Cancelled);

        var reactivation = await Reactivate().Handle(BusinessId, CancellationToken.None);

        Assert.NotNull(reactivation);
        Assert.Equal(BusinessStatus.Active, reactivation!.Status);
        Assert.Contains(FakePlatformBusinessDirectoryStore.Reactivate, _store.Operations);
        Assert.Null(_store.ReleasedMembershipId);
        Assert.Equal(MemberInviteStatus.Cancelled, _invites.Row(cancelled).StoredStatus);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(PlatformBusinessLifecycleChange.Reactivated, entry.Change);
        Assert.Equal(BusinessStatus.Deactivated, entry.FromStatus);
        Assert.Equal(BusinessStatus.Active, entry.ToStatus);
        Assert.Null(entry.MembershipId);
        Assert.Null(entry.InvitesCancelled);
    }

    /// <summary>
    /// The criterion: reactivating a business with no active Owner membership is refused and nothing
    /// changes. All three rows are "not an Owner" by the same Domain rule the rest of the
    /// application uses - a released Owner, a Manager who is active, and a stored role this code
    /// cannot read.
    /// </summary>
    [Theory]
    [InlineData(BusinessRole.Owner, false)]
    [InlineData(BusinessRole.Manager, true)]
    [InlineData((BusinessRole)99, true)]
    public async Task Reactivating_is_refused_when_no_membership_is_an_active_owner(
        BusinessRole role,
        bool isActive)
    {
        GivenBusiness(
            BusinessStatus.Deactivated,
            new PlatformBusinessMember(OwnerMembershipId, "Robin Owner", role, isActive));

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => Reactivate().Handle(BusinessId, CancellationToken.None));

        Assert.Equal(BusinessLifecyclePolicy.NoActiveOwnerMessage, conflict.Message);
        Assert.DoesNotContain(FakePlatformBusinessDirectoryStore.Reactivate, _store.Operations);
        AssertNothingWasWritten();
    }

    /// <summary>
    /// The Owner rule is judged on what the business holds <em>inside</em> the write transaction: an
    /// Owner released while this reactivation waited for the lock refuses it, rather than being
    /// overtaken by a view that was accurate a moment earlier.
    /// </summary>
    [Fact]
    public async Task An_owner_released_while_the_write_lock_is_awaited_refuses_the_reactivation()
    {
        GivenBusiness(BusinessStatus.Deactivated);
        _writeStore.WhileWaitingForTheWriteLock = () => SetMembers(
            new PlatformBusinessMember(OwnerMembershipId, "Robin Owner", BusinessRole.Owner, IsActive: false));

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => Reactivate().Handle(BusinessId, CancellationToken.None));

        Assert.Equal(BusinessLifecyclePolicy.NoActiveOwnerMessage, conflict.Message);
        AssertNothingWasWritten();
    }

    /// <summary>
    /// Only a deactivated business may be reactivated: a trading one, a pending application, a
    /// refused one and an undeclared stored status are each refused with the one sentence.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Active)]
    [InlineData(BusinessStatus.Pending)]
    [InlineData(BusinessStatus.Rejected)]
    [InlineData((BusinessStatus)0)]
    [InlineData((BusinessStatus)99)]
    public async Task Only_a_deactivated_business_may_be_reactivated(BusinessStatus status)
    {
        GivenBusiness(status);

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => Reactivate().Handle(BusinessId, CancellationToken.None));

        Assert.Equal(BusinessLifecyclePolicy.NotDeactivatedMessage, conflict.Message);
        AssertNothingWasWritten();
    }

    #endregion

    #region Releasing a member

    /// <summary>
    /// The criterion: the release marks that membership inactive with the instant it changed, and
    /// the audit event names the membership.
    /// </summary>
    [Fact]
    public async Task Releasing_a_member_records_the_membership_and_the_instant()
    {
        GivenBusiness(BusinessStatus.Deactivated);

        var member = await Release().Handle(BusinessId, ManagerMembershipId, CancellationToken.None);

        Assert.NotNull(member);
        Assert.Equal(ManagerMembershipId, member!.MembershipId);
        Assert.False(member.IsActive);
        Assert.Equal(BusinessRole.Manager, member.StoredRole);
        Assert.Equal(ManagerMembershipId, _store.ReleasedMembershipId);
        Assert.Equal(ActedAt, _store.ReleasedAtUtc);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(PlatformBusinessLifecycleChange.MemberReleased, entry.Change);
        Assert.Equal(ManagerMembershipId, entry.MembershipId);
        Assert.Equal(BusinessStatus.Deactivated, entry.FromStatus);
        Assert.Equal(BusinessStatus.Deactivated, entry.ToStatus);
        Assert.Null(entry.InvitesCancelled);
        Assert.Equal(ActedAt, entry.OccurredAtUtc);
    }

    /// <summary>
    /// The criterion "the last-Owner rule doesn't apply": releasing the only active Owner of a
    /// deactivated business succeeds. The guard is never even asked its "keep an active Owner"
    /// question - the business read that rule is judged from does not happen - because a business
    /// nobody can reach has no administrator to protect, and refusing here would leave its last
    /// Owner permanently attached to it.
    /// </summary>
    [Fact]
    public async Task Releasing_the_last_active_owner_of_a_deactivated_business_is_allowed()
    {
        GivenBusiness(BusinessStatus.Deactivated);

        var member = await Release().Handle(BusinessId, OwnerMembershipId, CancellationToken.None);

        Assert.False(member!.IsActive);
        Assert.Equal(OwnerMembershipId, _store.ReleasedMembershipId);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.BusinessRead, _writeStore.Operations);
        Assert.Contains(FakeBusinessMembershipWriteStore.Commit, _writeStore.Operations);

        // And the business cannot come back while that is the state it is in, which is what makes
        // the released Owner safe rather than lost: the reactivation refuses it.
        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => Reactivate().Handle(BusinessId, CancellationToken.None));
        Assert.Equal(BusinessLifecyclePolicy.NoActiveOwnerMessage, conflict.Message);
    }

    /// <summary>
    /// A release never activates anything, so <see cref="MembershipEligibility"/> has nothing to be
    /// re-checked for: the identity read inside the transaction does not happen either.
    /// </summary>
    [Fact]
    public async Task Releasing_a_member_runs_no_eligibility_check()
    {
        GivenBusiness(BusinessStatus.Deactivated);

        await Release().Handle(BusinessId, ManagerMembershipId, CancellationToken.None);

        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.IdentityRead, _writeStore.Operations);
    }

    /// <summary>
    /// Releasing a membership that is already inactive is accepted and writes nothing: the
    /// administrator asked for a state the row already holds. Re-stamping its state-change instant
    /// would claim a transition that never happened (issue #522), and a second audit event would
    /// record a change the database does not have.
    /// </summary>
    [Fact]
    public async Task Releasing_an_already_inactive_membership_writes_nothing()
    {
        GivenBusiness(
            BusinessStatus.Deactivated,
            new PlatformBusinessMember(OwnerMembershipId, "Robin Owner", BusinessRole.Owner, IsActive: true),
            new PlatformBusinessMember(ManagerMembershipId, "Sam Manager", BusinessRole.Manager, IsActive: false));

        var member = await Release().Handle(BusinessId, ManagerMembershipId, CancellationToken.None);

        Assert.False(member!.IsActive);
        Assert.Null(_store.ReleasedMembershipId);
        Assert.Empty(_audit.Entries);
    }

    /// <summary>
    /// The criterion: a release is refused for any status other than deactivated, so a trading
    /// business's membership list stays its own Owner's business.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Active)]
    [InlineData(BusinessStatus.Pending)]
    [InlineData(BusinessStatus.Rejected)]
    [InlineData((BusinessStatus)0)]
    [InlineData((BusinessStatus)99)]
    public async Task A_member_may_be_released_only_while_the_business_is_deactivated(BusinessStatus status)
    {
        GivenBusiness(status);

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => Release().Handle(BusinessId, ManagerMembershipId, CancellationToken.None));

        Assert.Equal(BusinessLifecyclePolicy.MembersUnavailableMessage, conflict.Message);
        Assert.Null(_store.ReleasedMembershipId);
        AssertNothingWasWritten();
    }

    /// <summary>
    /// A membership key that the business does not hold is "not found", which is the same answer a
    /// key belonging to another business gets - so neither is confirmed.
    /// </summary>
    [Fact]
    public async Task A_membership_the_business_does_not_hold_is_not_found()
    {
        GivenBusiness(BusinessStatus.Deactivated);

        Assert.Null(await Release().Handle(BusinessId, 9999, CancellationToken.None));
        Assert.Null(_store.ReleasedMembershipId);
        Assert.Empty(_audit.Entries);
    }

    #endregion

    #region Listing the members of a deactivated business

    /// <summary>
    /// The list answers only while the business is deactivated, opens no transaction and audits
    /// nothing - it is a read, exactly as the business directory beside it is.
    /// </summary>
    [Fact]
    public async Task The_members_of_a_deactivated_business_are_listed_without_a_transaction()
    {
        GivenBusiness(BusinessStatus.Deactivated);

        var members = await ListMembers().Handle(BusinessId, CancellationToken.None);

        Assert.NotNull(members);
        Assert.Equal([OwnerMembershipId, ManagerMembershipId], members!.Select(member => member.MembershipId));
        Assert.Equal(["Robin Owner", "Sam Manager"], members.Select(member => member.DisplayName));
        Assert.Empty(_writeStore.Operations);
        Assert.Empty(_audit.Entries);
    }

    [Theory]
    [InlineData(BusinessStatus.Active)]
    [InlineData(BusinessStatus.Pending)]
    [InlineData(BusinessStatus.Rejected)]
    [InlineData((BusinessStatus)0)]
    [InlineData((BusinessStatus)99)]
    public async Task The_members_of_a_business_that_is_not_deactivated_are_not_listed(BusinessStatus status)
    {
        GivenBusiness(status);

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => ListMembers().Handle(BusinessId, CancellationToken.None));

        Assert.Equal(BusinessLifecyclePolicy.MembersUnavailableMessage, conflict.Message);
    }

    #endregion

    #region Every action

    /// <summary>An unknown business is "not found" for all four, and writes nothing.</summary>
    [Fact]
    public async Task An_unknown_business_is_not_found()
    {
        Assert.Null(await Deactivate().Handle(BusinessId, CancellationToken.None));
        Assert.Null(await Reactivate().Handle(BusinessId, CancellationToken.None));
        Assert.Null(await Release().Handle(BusinessId, ManagerMembershipId, CancellationToken.None));
        Assert.Null(await ListMembers().Handle(BusinessId, CancellationToken.None));

        AssertNothingWasWritten();
    }

    /// <summary>
    /// A request with no identifiable actor changes nothing at all. The platform-admin policy has
    /// already established that the caller is the configured administrator, so this is the
    /// fail-closed answer to a pipeline that somehow let an unidentified caller through - never an
    /// action nobody can be held to.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_identifiable_actor_changes_nothing()
    {
        GivenBusiness(BusinessStatus.Active);
        var writer = Writer(FakeAuthenticatedActorAccessor.NotAuthenticated());

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => new DeactivateBusiness(writer).Handle(BusinessId, CancellationToken.None));
        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => new ReactivateBusiness(writer).Handle(BusinessId, CancellationToken.None));
        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => new ReleaseBusinessMember(writer)
                .Handle(BusinessId, ManagerMembershipId, CancellationToken.None));

        Assert.Empty(_store.Operations);
        AssertNothingWasWritten();
    }

    #endregion

    #region Arrangement

    private void GivenBusiness(BusinessStatus status, params PlatformBusinessMember[] members) =>
        _store.LifecycleTargets[BusinessId] = new PlatformBusinessLifecycleTarget(
            BusinessId,
            "Vending Co",
            status,
            members.Length > 0
                ? members
                : [
                    new PlatformBusinessMember(OwnerMembershipId, "Robin Owner", BusinessRole.Owner, IsActive: true),
                    new PlatformBusinessMember(
                        ManagerMembershipId,
                        "Sam Manager",
                        BusinessRole.Manager,
                        IsActive: true),
                ]);

    private void SetStatus(BusinessStatus status) =>
        _store.LifecycleTargets[BusinessId] = _store.LifecycleTargets[BusinessId] with { Status = status };

    private void SetMembers(params PlatformBusinessMember[] members) =>
        _store.LifecycleTargets[BusinessId] = _store.LifecycleTargets[BusinessId] with { Members = members };

    private int GivenInvite(
        MemberInviteStatus status = MemberInviteStatus.Pending,
        int businessId = BusinessId) =>
        _invites.Add(
            new StoredMemberInvite(
                _invites.Rows.Count + 1,
                BusinessRole.Operator,
                Note: null,
                status,
                SeededAt,
                MemberInvitePolicy.ExpiresAt(ActedAt),
                UsedAtUtc: status == MemberInviteStatus.Used ? SeededAt : null,
                CancelledAtUtc: status == MemberInviteStatus.Cancelled ? SeededAt : null,
                RedeemedByMembershipId: null),
            businessId);

    private DeactivateBusiness Deactivate() => new(Writer());

    private ReactivateBusiness Reactivate() => new(Writer());

    private ReleaseBusinessMember Release() => new(Writer());

    private ListDeactivatedBusinessMembers ListMembers() => new(Writer());

    private BusinessLifecycleWriter Writer() =>
        Writer(FakeAuthenticatedActorAccessor.Identified(Administrator()));

    private BusinessLifecycleWriter Writer(IAuthenticatedActorAccessor actorAccessor) =>
        new(
            actorAccessor,
            _store,
            _invites,
            new MembershipWriteGuard(_writeStore),
            _hook,
            _audit,
            new FakeClock(ActedAt));

    private static ActorIdentity Administrator()
    {
        Assert.True(ActorIdentity.TryCreate(DirectoryTenantId, AdministratorObjectId, out var identity));
        return identity!;
    }

    private void AssertNothingWasWritten()
    {
        Assert.DoesNotContain(FakePlatformBusinessDirectoryStore.Deactivate, _store.Operations);
        Assert.DoesNotContain(FakePlatformBusinessDirectoryStore.Reactivate, _store.Operations);
        Assert.DoesNotContain(FakePlatformBusinessDirectoryStore.ReleaseMember, _store.Operations);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.Commit, _writeStore.Operations);
        Assert.Empty(_audit.Entries);
    }

    #endregion
}
