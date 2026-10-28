using Inventory.Application.Businesses;
using Inventory.Application.Members;
using Inventory.Application.PlatformAdmin;
using Inventory.Application.Suppliers;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Members;
using InventoryApi.Tests.Application.PlatformAdmin;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The three Owner writes of issue #680 interleaved with the Super Admin's deactivation, on
/// independent connections to one real SQLite file through <see cref="MembershipWriteRaceHarness"/>.
///
/// <para><strong>The race these tests exist for.</strong> An Owner's request takes its business id
/// from <c>BusinessScopeMiddleware</c>, which resolved it while the business was still
/// <see cref="BusinessStatus.Active"/>. If the Super Admin's deactivation commits after that, the
/// Owner's insert or update would land on a business that has been switched off - and the invite
/// case is the one with teeth: a <c>Pending</c> seven-day invite committed onto a deactivated
/// business is redeemable again the moment the business is reactivated, which is exactly the way
/// back in that cancelling every live invite on deactivation (issues #509/#531/#545) exists to
/// close. The settings save and the member change are the same root cause with lower impact: a
/// reinstatement in flight could otherwise re-activate a membership the Super Admin has just
/// released.</para>
///
/// <para><strong>Why both a sequential and a concurrent half.</strong> The sequential tests are the
/// deterministic statement of the rule: the deactivation has <em>committed</em>, the Owner's
/// business id was resolved before it, and the write must be refused with the one fixed sentence and
/// persist and audit nothing. The barrier tests are what prove the two writes are actually
/// serialised rather than merely ordered by the test - and what they may assert is the invariant
/// rather than the winner, because an Owner write that commits before the deactivation and is then
/// cancelled by it is a correct outcome, as is one refused by it.</para>
///
/// <para>The whole use cases run, not just the adapters: the real <see cref="MembershipWriteGuard"/>
/// and the real EF adapters over the migrated schema, with only the resolved business, the
/// authenticated actor, the clock and the audit sinks faked - because those are the things a test
/// has to decide rather than observe.</para>
/// </summary>
public class OwnerWriteDuringDeactivationTests : IAsyncDisposable
{
    private const int BusinessIdValue = 1;
    private const int OtherBusinessId = 2;

    private const string OwnerOid = "22222222-2222-2222-2222-222222222222";
    private const string ManagerOid = "33333333-3333-3333-3333-333333333333";
    private const string AdministratorOid = "99999999-9999-9999-9999-999999999999";

    private const string Sydney = "Australia/Sydney";

    private static readonly DateTime Now = MembershipWriteRaceHarness.SeededAt.AddHours(1);

    private static readonly BusinessSettingsSubmission Renamed =
        new("Renamed Co", "owner@example.com", null, true, Sydney, "NSW");

    private readonly MembershipWriteRaceHarness _harness =
        MembershipWriteRaceHarness.Create(BusinessIdValue, OtherBusinessId);

    private readonly int _managerMembershipId;

    public OwnerWriteDuringDeactivationTests()
    {
        _harness.SeedMembership(BusinessIdValue, OwnerOid);
        _managerMembershipId = _harness.SeedMembership(BusinessIdValue, ManagerOid, BusinessRole.Manager);
    }

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    /// <summary>
    /// The defect the issue reports, in its simplest deterministic form: the deactivation has
    /// committed, and the Owner's create-invite request - which passed the middleware while the
    /// business was still trading - stores no invite at all.
    /// </summary>
    [Fact]
    public async Task An_invite_creation_that_arrives_after_a_deactivation_is_refused_and_stores_nothing()
    {
        await DeactivateAsync(CancellationToken.None);

        var audit = new FakeMemberInviteAudit();

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => CreateInviteAsync(audit, CancellationToken.None));

        Assert.Equal(ActiveBusinessWritePolicy.NotActiveMessage, conflict.Message);
        Assert.Empty(_harness.Invites());
        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// The same for the business settings save: the row the Super Admin has just switched off is not
    /// renamed by a request that was already in flight, and nothing is audited as a change.
    /// </summary>
    [Fact]
    public async Task A_settings_save_that_arrives_after_a_deactivation_is_refused_and_stores_nothing()
    {
        await DeactivateAsync(CancellationToken.None);

        var before = Business();
        var audit = new RecordingSettingsAudit();

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => SaveSettingsAsync(audit, CancellationToken.None));

        Assert.Equal(ActiveBusinessWritePolicy.NotActiveMessage, conflict.Message);

        var after = Business();
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.ContactEmail, after.ContactEmail);
        Assert.Equal(before.GstRegistered, after.GstRegistered);
        Assert.Equal(BusinessStatus.Deactivated, after.Status);
        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// And for a member change: the membership row is left exactly as the deactivation left it -
    /// flag and state-change instant alike - so a reactivation restores the business to the
    /// membership state the Super Admin saw.
    /// </summary>
    [Fact]
    public async Task A_member_change_that_arrives_after_a_deactivation_is_refused_and_stores_nothing()
    {
        await DeactivateAsync(CancellationToken.None);

        var audit = new FakeMemberManagementAudit();

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => RevokeManagerAsync(audit, CancellationToken.None));

        Assert.Equal(ActiveBusinessWritePolicy.NotActiveMessage, conflict.Message);

        var manager = _harness.Memberships().Single(membership => membership.Id == _managerMembershipId);
        Assert.True(manager.IsActive);
        Assert.Equal(MembershipWriteRaceHarness.SeededAt, manager.StatusChangedAtUtc);
        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// The acceptance criterion the invite case is really about: whatever order a create-invite
    /// request and a deactivation arrive in, a reactivation finds no redeemable invite. An invite
    /// that committed first was cancelled by the deactivation; one that arrived after it was never
    /// stored.
    /// </summary>
    [Fact]
    public async Task A_deactivation_and_a_reactivation_leave_no_redeemable_invite()
    {
        // The invite that exists when the deactivation runs, and the one racing it.
        _harness.SeedInvite(BusinessIdValue, "the-link");

        var failures = await MembershipWriteRaceHarness.RunAsync(
            token => CreateInviteAsync(new FakeMemberInviteAudit(), token),
            token => DeactivateAsync(token));

        AssertTheDeactivationLanded(failures);

        await ReactivateAsync(CancellationToken.None);
        Assert.Equal(BusinessStatus.Active, Business().Status);

        // Live means redeemable: pending and not past its expiry (MemberInvitePolicy). Whether the
        // racing creation stored a row at all depends on which writer took the lock first, so what
        // is asserted is that no row is redeemable - including the seeded one, and including any the
        // reactivation might wrongly have revived.
        Assert.NotEmpty(_harness.Invites());
        Assert.DoesNotContain(
            _harness.Invites(),
            invite => MemberInvitePolicy.IsLive(invite.Status, invite.ExpiresAtUtc, Now));
    }

    /// <summary>
    /// The concurrent half for the invite creation on its own: the deactivation always lands, and
    /// the creation either committed before it - in which case the deactivation cancelled the row it
    /// wrote - or was refused with the one fixed sentence and wrote nothing.
    /// </summary>
    [Fact]
    public async Task An_invite_creation_racing_a_deactivation_is_either_cancelled_by_it_or_refused()
    {
        var audit = new FakeMemberInviteAudit();

        var failures = await MembershipWriteRaceHarness.RunAsync(
            token => CreateInviteAsync(audit, token),
            token => DeactivateAsync(token));

        var refusal = AssertTheDeactivationLanded(failures);

        if (refusal is null)
        {
            var invite = Assert.Single(_harness.Invites());
            Assert.Equal(MemberInviteStatus.Cancelled, invite.Status);
            Assert.Equal(Now, invite.CancelledAtUtc);
            Assert.Single(audit.Entries);
        }
        else
        {
            Assert.Empty(_harness.Invites());
            Assert.Empty(audit.Entries);
        }
    }

    /// <summary>
    /// The concurrent half for the settings save: the business is deactivated either way, and the
    /// save either committed its five settings before the deactivation or changed none of them.
    /// </summary>
    [Fact]
    public async Task A_settings_save_racing_a_deactivation_either_lands_before_it_or_is_refused()
    {
        var before = Business();
        var audit = new RecordingSettingsAudit();

        var failures = await MembershipWriteRaceHarness.RunAsync(
            token => SaveSettingsAsync(audit, token),
            token => DeactivateAsync(token));

        var refusal = AssertTheDeactivationLanded(failures);
        var after = Business();

        if (refusal is null)
        {
            Assert.Equal(Renamed.Name, after.Name);
            Assert.Equal(Renamed.ContactEmail, after.ContactEmail);
            Assert.Single(audit.Entries);
        }
        else
        {
            Assert.Equal(before.Name, after.Name);
            Assert.Equal(before.ContactEmail, after.ContactEmail);
            Assert.Empty(audit.Entries);
        }
    }

    /// <summary>
    /// The concurrent half for a member change, which is the one that would otherwise re-activate a
    /// membership after the business holding it was switched off: the revocation either committed
    /// before the deactivation or left the row untouched, instant included.
    /// </summary>
    [Fact]
    public async Task A_member_change_racing_a_deactivation_either_lands_before_it_or_is_refused()
    {
        var audit = new FakeMemberManagementAudit();

        var failures = await MembershipWriteRaceHarness.RunAsync(
            token => RevokeManagerAsync(audit, token),
            token => DeactivateAsync(token));

        var refusal = AssertTheDeactivationLanded(failures);
        var manager = _harness.Memberships().Single(membership => membership.Id == _managerMembershipId);

        if (refusal is null)
        {
            Assert.False(manager.IsActive);
            Assert.Equal(Now, manager.StatusChangedAtUtc);
            Assert.Single(audit.Entries);
        }
        else
        {
            Assert.True(manager.IsActive);
            Assert.Equal(MembershipWriteRaceHarness.SeededAt, manager.StatusChangedAtUtc);
            Assert.Empty(audit.Entries);
        }
    }

    /// <summary>
    /// The other half of every test above, and the one that would hide a guard that refused
    /// everything: all three writes still succeed on a business that is trading, are persisted, and
    /// are audited once each.
    /// </summary>
    [Fact]
    public async Task Ordinary_owner_writes_on_an_active_business_still_succeed()
    {
        var inviteAudit = new FakeMemberInviteAudit();
        var settingsAudit = new RecordingSettingsAudit();
        var memberAudit = new FakeMemberManagementAudit();

        await CreateInviteAsync(inviteAudit, CancellationToken.None);
        await SaveSettingsAsync(settingsAudit, CancellationToken.None);
        await RevokeManagerAsync(memberAudit, CancellationToken.None);

        var invite = Assert.Single(_harness.Invites());
        Assert.Equal(MemberInviteStatus.Pending, invite.Status);
        Assert.Equal(BusinessIdValue, invite.BusinessId);
        Assert.Single(inviteAudit.Entries);

        var business = Business();
        Assert.Equal(Renamed.Name, business.Name);
        Assert.Equal(Renamed.ContactEmail, business.ContactEmail);
        Assert.Equal(BusinessStatus.Active, business.Status);
        Assert.Single(settingsAudit.Entries);

        var manager = _harness.Memberships().Single(membership => membership.Id == _managerMembershipId);
        Assert.False(manager.IsActive);
        Assert.Equal(Now, manager.StatusChangedAtUtc);
        Assert.Single(memberAudit.Entries);
    }

    /// <summary>
    /// The deactivation is the one writer in these races that must always win: none of the three
    /// Owner writes changes a lifecycle status, so nothing can refuse it. Returns the Owner write's
    /// refusal, or <see langword="null"/> when it committed first.
    /// </summary>
    private Exception? AssertTheDeactivationLanded(Exception?[] failures)
    {
        Assert.Null(failures[1]);
        Assert.Equal(BusinessStatus.Deactivated, Business().Status);

        if (failures[0] is { } refusal)
        {
            Assert.Equal(
                ActiveBusinessWritePolicy.NotActiveMessage,
                Assert.IsType<DomainConflictException>(refusal).Message);
        }

        return failures[0];
    }

    /// <summary>
    /// One real create-invite request on its own connection, with the business resolved the way
    /// <c>BusinessScopeMiddleware</c> resolved it before the deactivation committed - which is
    /// precisely the stale fact this slice is about.
    /// </summary>
    private async Task CreateInviteAsync(FakeMemberInviteAudit audit, CancellationToken cancellationToken)
    {
        await using var db = TestAppDbContext.For(_harness.Options, BusinessIdValue);

        await new CreateMemberInvite(
                new FakeCurrentBusinessProvider(BusinessIdValue),
                FakeAuthenticatedActorAccessor.Identified(MembershipWriteRaceHarness.Actor(OwnerOid)),
                new EfMemberInviteStore(db),
                new MembershipWriteGuard(new EfBusinessMembershipWriteStore(db)),
                audit,
                new FakeClock(Now))
            .Handle("Manager", null, cancellationToken);
    }

    /// <summary>The same for one real settings save.</summary>
    private async Task SaveSettingsAsync(RecordingSettingsAudit audit, CancellationToken cancellationToken)
    {
        await using var db = TestAppDbContext.For(_harness.Options, BusinessIdValue);

        await new UpdateBusinessSettings(
                new FakeCurrentBusinessProvider(BusinessIdValue),
                FakeAuthenticatedActorAccessor.Identified(MembershipWriteRaceHarness.Actor(OwnerOid)),
                new EfBusinessSettingsStore(db),
                new AddMatchingSupplierPresets(new EfAvailableSupplierPresetStore(db)),
                new MembershipWriteGuard(new EfBusinessMembershipWriteStore(db)),
                audit,
                new FakeClock(Now))
            .Handle(Renamed, cancellationToken);
    }

    /// <summary>The same for one real member change - the Owner revoking the business's Manager.</summary>
    private async Task RevokeManagerAsync(
        FakeMemberManagementAudit audit,
        CancellationToken cancellationToken)
    {
        await using var db = TestAppDbContext.For(_harness.Options, BusinessIdValue);

        await new RevokeMember(Writer(db, audit)).Handle(_managerMembershipId, cancellationToken);
    }

    private static MemberChangeWriter Writer(AppDbContext db, FakeMemberManagementAudit audit) =>
        new(
            new FakeCurrentBusinessProvider(BusinessIdValue),
            FakeAuthenticatedActorAccessor.Identified(MembershipWriteRaceHarness.Actor(OwnerOid)),
            new EfBusinessMemberStore(db),
            new MembershipWriteGuard(new EfBusinessMembershipWriteStore(db)),
            audit,
            new FakeClock(Now));

    /// <summary>
    /// One real deactivation, as the Super Admin's request makes it: a denied business scope - the
    /// platform administrator is a member of nothing - the real guard and the real platform store.
    /// </summary>
    private async Task DeactivateAsync(CancellationToken cancellationToken)
    {
        await using var db = TestAppDbContext.Denied(_harness.Options);

        await Lifecycle(db).DeactivateAsync(BusinessIdValue, cancellationToken);
    }

    private async Task ReactivateAsync(CancellationToken cancellationToken)
    {
        await using var db = TestAppDbContext.Denied(_harness.Options);

        await Lifecycle(db).ReactivateAsync(BusinessIdValue, cancellationToken);
    }

    private static BusinessLifecycleWriter Lifecycle(AppDbContext db) =>
        new(
            FakeAuthenticatedActorAccessor.Identified(MembershipWriteRaceHarness.Actor(AdministratorOid)),
            new EfPlatformBusinessDirectoryStore(db),
            new EfMemberInviteStore(db),
            new MembershipWriteGuard(new EfBusinessMembershipWriteStore(db)),
            new FakeBusinessDeactivationHook(),
            new FakePlatformBusinessLifecycleAudit(),
            new FakeClock(Now));

    private Inventory.Infrastructure.Models.Business Business() =>
        _harness.Businesses().Single(business => business.Id == BusinessIdValue);

    /// <summary>
    /// Collects the business-settings audit events, so a test can assert that a refused save is
    /// recorded as nothing at all.
    /// </summary>
    private sealed class RecordingSettingsAudit : IBusinessSettingsAudit
    {
        private readonly List<BusinessSettingsAuditEntry> _entries = [];

        public IReadOnlyList<BusinessSettingsAuditEntry> Entries => _entries;

        public void Record(BusinessSettingsAuditEntry entry) => _entries.Add(entry);
    }
}
