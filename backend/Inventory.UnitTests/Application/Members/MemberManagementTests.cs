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
/// The member management use cases of issue #533, against fakes: which rules each change declares,
/// what it refuses, when it writes, and what reaches the audit trail.
///
/// The relational and concurrency behaviour is proved elsewhere - <c>EfBusinessMemberStoreTests</c>,
/// <c>MemberManagementConcurrencyTests</c> and <c>MembersApiTests</c>. What these tests are for is
/// the decisions, and above all the ones that are invisible in a passing happy path: that the
/// authoritative read happens inside the write transaction, that a revocation runs the Owner rule
/// while a reinstatement runs the eligibility rule, and that nothing is audited for a change that
/// did not happen.
/// </summary>
public class MemberManagementTests
{
    private const int BusinessIdValue = 7;
    private const int OtherBusinessIdValue = 8;
    private const int MembershipIdValue = 42;
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string ActorOid = "22222222-2222-2222-2222-222222222222";
    private const string MemberOid = "33333333-3333-3333-3333-333333333333";
    private const string MemberDisplayName = "Ada Lovelace";
    private const string MemberEmail = "ada@vending.example";

    private static readonly DateTime JoinedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ChangedAt = new(2026, 10, 11, 1, 0, 0, DateTimeKind.Utc);

    #region Listing

    /// <summary>
    /// The list is an answer about the caller's own business and takes no identifier: the use case
    /// asks <see cref="ICurrentBusinessProvider"/> and nothing else, so no request value can choose
    /// whose members are described.
    /// </summary>
    [Fact]
    public async Task It_lists_only_the_resolved_businesss_members()
    {
        var store = new FakeBusinessMemberStore();
        store.Add(BusinessIdValue, Membership(MembershipIdValue));
        store.Add(OtherBusinessIdValue, Membership(99, MemberOid, displayName: "Katherine Johnson"));

        var members = await new ListMembers(new FakeCurrentBusinessProvider(BusinessIdValue), store)
            .Handle(CancellationToken.None);

        var member = Assert.Single(members);
        Assert.Equal(MembershipIdValue, member.MembershipId);
        Assert.Equal(MemberDisplayName, member.DisplayName);
        Assert.Equal(MemberEmail, member.Email);
        Assert.Equal(JoinedAt, member.JoinedAtUtc);
    }

    [Fact]
    public async Task A_caller_with_no_usable_membership_lists_nothing()
    {
        var store = new FakeBusinessMemberStore();
        store.Add(BusinessIdValue, Membership(MembershipIdValue));

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(() =>
            new ListMembers(FakeCurrentBusinessProvider.Denied(), store).Handle(CancellationToken.None));
    }

    #endregion

    #region Changing a role

    /// <summary>
    /// The submitted role is checked against the declared vocabulary before anything else happens -
    /// before the business is even resolved - so an unsupported value cannot reach a transaction,
    /// a write or the audit trail.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Administrator")]
    [InlineData("Viewer")]
    [InlineData("40")]
    public async Task An_unsupported_role_name_is_refused_before_anything_is_read(string? role)
    {
        var fixture = new Fixture();

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() =>
            fixture.ChangeRole().Handle(MembershipIdValue, role, CancellationToken.None));

        Assert.Equal(BusinessRoles.UnsupportedMessage, refusal.Message);
        Assert.Empty(fixture.Operations);
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    public async Task A_membership_the_business_does_not_hold_is_not_found_and_opens_no_transaction()
    {
        var fixture = new Fixture();

        Assert.Null(await fixture.ChangeRole().Handle(MembershipIdValue + 1, "Manager", CancellationToken.None));
        Assert.Null(await fixture.Revoke().Handle(MembershipIdValue + 1, CancellationToken.None));
        Assert.Null(await fixture.Reinstate().Handle(MembershipIdValue + 1, CancellationToken.None));

        // Only the lookups ran: no transaction was opened, so nothing could have been written even
        // transiently, and no rule was asked a question about a membership that is not there.
        Assert.Equal(
            [FakeBusinessMemberStore.Find, FakeBusinessMemberStore.Find, FakeBusinessMemberStore.Find],
            fixture.Operations);
        Assert.Empty(fixture.Audit.Entries);
    }

    /// <summary>
    /// A role change writes the role, audits the old → new pair, and reports the member as the
    /// change left them.
    /// </summary>
    [Fact]
    public async Task A_role_change_writes_the_role_and_is_audited()
    {
        var fixture = new Fixture(role: BusinessRole.Operator);

        var member = await fixture.ChangeRole().Handle(MembershipIdValue, "Manager", CancellationToken.None);

        Assert.NotNull(member);
        Assert.Equal(BusinessRole.Manager, member.Role);
        Assert.True(member.IsActive);
        Assert.Equal(MemberDisplayName, member.DisplayName);

        var entry = Assert.Single(fixture.Audit.Entries);
        Assert.Equal(MemberChange.RoleChanged, entry.Change);
        Assert.Equal(Tid, entry.ActorDirectoryTenantId);
        Assert.Equal(ActorOid, entry.ActorObjectId);
        Assert.Equal(BusinessIdValue, entry.BusinessId);
        Assert.Equal(MembershipIdValue, entry.MembershipId);
        Assert.Equal(BusinessRole.Operator, entry.PreviousRole);
        Assert.Equal(BusinessRole.Manager, entry.NewRole);
        Assert.True(entry.PreviouslyActive);
        Assert.True(entry.NowActive);
        Assert.Equal(ChangedAt, entry.OccurredAtUtc);
    }

    /// <summary>
    /// The order the whole write path runs in, which is the reason it is built the way it is: the
    /// transaction is opened first, the authoritative read and the write happen inside it, the
    /// Owner rule is judged on what the write left behind, and only then is anything committed. A
    /// read before <c>begin</c> would be a decision taken outside the write lock.
    /// </summary>
    [Fact]
    public async Task The_authoritative_read_and_the_write_happen_inside_the_transaction()
    {
        var fixture = new Fixture(role: BusinessRole.Operator);

        await fixture.ChangeRole().Handle(MembershipIdValue, "Manager", CancellationToken.None);

        Assert.Equal(
            [
                // The lookup that decides "does this business hold this membership", outside the
                // transaction, for the immutable facts only.
                FakeBusinessMemberStore.Find,
                FakeBusinessMembershipWriteStore.Begin,
                FakeBusinessMemberStore.Find,
                FakeBusinessMemberStore.WriteRole,
                FakeBusinessMembershipWriteStore.BusinessRead,
                FakeBusinessMembershipWriteStore.Commit,
                FakeBusinessMembershipWriteStore.Dispose,
            ],
            fixture.Operations);
    }

    /// <summary>
    /// The in-transaction read is authoritative, not decorative: when the membership is revoked
    /// between the two reads, the refusal is the one the fresh state deserves and nothing is
    /// written or audited.
    /// </summary>
    [Fact]
    public async Task A_membership_revoked_between_the_two_reads_refuses_the_role_change()
    {
        var fixture = new Fixture(role: BusinessRole.Operator);
        fixture.Store.OnceAfterFind = () =>
            fixture.Store.RevokeBehindTheWritersBack(BusinessIdValue, MembershipIdValue);

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() =>
            fixture.ChangeRole().Handle(MembershipIdValue, "Manager", CancellationToken.None));

        Assert.Equal(ChangeMemberRole.RevokedMembershipMessage, refusal.Message);
        Assert.Equal(BusinessRole.Operator, fixture.Store.Row(BusinessIdValue, MembershipIdValue).StoredRole);
        Assert.Empty(fixture.Audit.Entries);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.Commit, fixture.Operations);
    }

    [Fact]
    public async Task A_revoked_memberships_role_is_not_changed()
    {
        var fixture = new Fixture(role: BusinessRole.Operator, isActive: false);

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() =>
            fixture.ChangeRole().Handle(MembershipIdValue, "Manager", CancellationToken.None));

        Assert.Equal(ChangeMemberRole.RevokedMembershipMessage, refusal.Message);
        Assert.Empty(fixture.Audit.Entries);
    }

    /// <summary>
    /// A role change declares the business that must keep an active Owner and declares no identity:
    /// it activates nothing, so the eligibility rule has no question to answer, and the Owner rule
    /// has to answer one.
    /// </summary>
    [Fact]
    public async Task A_role_change_declares_the_owner_rule_and_not_the_eligibility_rule()
    {
        var fixture = new Fixture(role: BusinessRole.Operator);

        await fixture.ChangeRole().Handle(MembershipIdValue, "Manager", CancellationToken.None);

        Assert.Contains(FakeBusinessMembershipWriteStore.BusinessRead, fixture.Operations);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.IdentityRead, fixture.Operations);
        Assert.Equal(BusinessId.From(BusinessIdValue), fixture.WriteStore.LastQueriedBusiness);
    }

    /// <summary>
    /// A demotion that would leave the business with no active Owner is refused on the state the
    /// write left behind, and the refusal rolls that write back - this is the rule that stops an
    /// Owner from locking everybody, themselves included, out of administering the business.
    /// </summary>
    [Fact]
    public async Task A_demotion_that_removes_the_last_active_owner_is_refused()
    {
        var fixture = new Fixture(role: BusinessRole.Owner);
        fixture.WriteStore.BusinessMembershipRoles.Clear();
        fixture.WriteStore.BusinessMembershipRoles.Add(new BusinessMembershipRole(BusinessRole.Manager, true));

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() =>
            fixture.ChangeRole().Handle(MembershipIdValue, "Manager", CancellationToken.None));

        Assert.Equal(BusinessOwnerRetention.LastActiveOwnerMessage, refusal.Message);
        Assert.Empty(fixture.Audit.Entries);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.Commit, fixture.Operations);
    }

    [Fact]
    public async Task Setting_the_role_a_member_already_holds_writes_nothing_and_audits_nothing()
    {
        var fixture = new Fixture(role: BusinessRole.Manager);

        var member = await fixture.ChangeRole().Handle(MembershipIdValue, "Manager", CancellationToken.None);

        Assert.Equal(BusinessRole.Manager, member?.Role);
        Assert.DoesNotContain(FakeBusinessMemberStore.WriteRole, fixture.Operations);
        Assert.DoesNotContain(FakeBusinessMemberStore.WriteActive, fixture.Operations);
        Assert.Empty(fixture.Audit.Entries);
    }

    #endregion

    #region Revoking

    /// <summary>
    /// A revocation writes the state and the instant it changed, carries the role through
    /// unchanged - a revoked row is history, and it is the role a reinstatement restores - and is
    /// audited once.
    /// </summary>
    [Fact]
    public async Task A_revocation_writes_the_state_with_its_instant_and_is_audited()
    {
        var fixture = new Fixture(role: BusinessRole.Manager);

        var member = await fixture.Revoke().Handle(MembershipIdValue, CancellationToken.None);

        Assert.False(member?.IsActive);
        Assert.Equal(BusinessRole.Manager, member?.Role);
        Assert.Equal(ChangedAt, fixture.Store.LastStatusChangedAtUtc);
        Assert.DoesNotContain(FakeBusinessMemberStore.WriteRole, fixture.Operations);

        var entry = Assert.Single(fixture.Audit.Entries);
        Assert.Equal(MemberChange.Revoked, entry.Change);
        Assert.Equal(BusinessRole.Manager, entry.PreviousRole);
        Assert.Equal(BusinessRole.Manager, entry.NewRole);
        Assert.True(entry.PreviouslyActive);
        Assert.False(entry.NowActive);
    }

    [Fact]
    public async Task A_revocation_that_removes_the_last_active_owner_is_refused()
    {
        var fixture = new Fixture(role: BusinessRole.Owner);
        fixture.WriteStore.BusinessMembershipRoles.Clear();

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() =>
            fixture.Revoke().Handle(MembershipIdValue, CancellationToken.None));

        Assert.Equal(BusinessOwnerRetention.LastActiveOwnerMessage, refusal.Message);
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    public async Task Revoking_an_already_revoked_membership_writes_nothing_and_audits_nothing()
    {
        var fixture = new Fixture(role: BusinessRole.Operator, isActive: false);

        var member = await fixture.Revoke().Handle(MembershipIdValue, CancellationToken.None);

        Assert.False(member?.IsActive);
        Assert.Null(fixture.Store.LastStatusChangedAtUtc);
        Assert.Empty(fixture.Audit.Entries);
    }

    #endregion

    #region Reinstating

    /// <summary>
    /// A reinstatement declares the identity the eligibility rule is re-checked for, with this
    /// membership's own row excluded, and declares no business for the Owner rule: it cannot remove
    /// an Owner, so there is nothing for that rule to judge.
    /// </summary>
    [Fact]
    public async Task A_reinstatement_declares_the_eligibility_rule_for_the_members_own_identity()
    {
        var fixture = new Fixture(role: BusinessRole.Operator, isActive: false);

        var member = await fixture.Reinstate().Handle(MembershipIdValue, CancellationToken.None);

        Assert.True(member?.IsActive);
        Assert.Equal(BusinessRole.Operator, member?.Role);
        Assert.Contains(FakeBusinessMembershipWriteStore.IdentityRead, fixture.Operations);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.BusinessRead, fixture.Operations);
        Assert.Equal(MemberOid, fixture.WriteStore.LastQueriedIdentity?.ObjectId);
        Assert.Equal(MemberChange.Reinstated, Assert.Single(fixture.Audit.Entries).Change);
    }

    /// <summary>
    /// The agreed refusal: a person who now holds an active membership anywhere is not reinstated
    /// here, because a second active membership would deny them both businesses. The message names
    /// no business, member or role.
    /// </summary>
    [Fact]
    public async Task Reinstating_a_person_who_belongs_elsewhere_is_refused()
    {
        var fixture = new Fixture(role: BusinessRole.Operator, isActive: false);
        fixture.WriteStore.IdentityMemberships.Add(new IdentityMembership(MembershipIdValue + 1, true));

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() =>
            fixture.Reinstate().Handle(MembershipIdValue, CancellationToken.None));

        Assert.Equal(MembershipEligibility.AlreadyAMemberMessage, refusal.Message);
        Assert.Empty(fixture.Audit.Entries);
        Assert.False(fixture.Store.Row(BusinessIdValue, MembershipIdValue).Member.IsActive);
    }

    /// <summary>
    /// The exclusion matters: this membership's own row is already in that identity's records once
    /// it is reinstated, and counting it would refuse every reinstatement by its own row.
    /// </summary>
    [Fact]
    public async Task The_memberships_own_row_does_not_refuse_its_reinstatement()
    {
        var fixture = new Fixture(role: BusinessRole.Operator, isActive: false);
        fixture.WriteStore.IdentityMemberships.Add(new IdentityMembership(MembershipIdValue, true));

        Assert.True((await fixture.Reinstate().Handle(MembershipIdValue, CancellationToken.None))?.IsActive);
    }

    /// <summary>
    /// A membership whose stored Entra pair is not an identity fails closed. Only a hand-written
    /// row can produce it, and reinstating it would mean skipping the one-active-membership check
    /// altogether, so no transaction is even opened.
    /// </summary>
    [Fact]
    public async Task A_membership_with_an_unusable_stored_identity_is_not_reinstated()
    {
        var fixture = new Fixture(role: BusinessRole.Operator, isActive: false, identity: null);

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() =>
            fixture.Reinstate().Handle(MembershipIdValue, CancellationToken.None));

        Assert.Equal(MemberChangeWriter.UnusableStoredIdentityMessage, refusal.Message);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.Begin, fixture.Operations);
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    public async Task Reinstating_an_already_active_membership_writes_nothing_and_audits_nothing()
    {
        var fixture = new Fixture(role: BusinessRole.Manager);

        Assert.True((await fixture.Reinstate().Handle(MembershipIdValue, CancellationToken.None))?.IsActive);
        Assert.Null(fixture.Store.LastStatusChangedAtUtc);
        Assert.Empty(fixture.Audit.Entries);
    }

    #endregion

    #region The audit contract

    /// <summary>
    /// The audit event carries no display name and no email address - a structural guarantee, not a
    /// formatting choice: the record has no field for either, so a sink cannot log one even by
    /// printing the whole entry. Issue #524 made never logging them a standing rule, and they
    /// identify nobody here anyway.
    /// </summary>
    [Fact]
    public async Task An_audit_entry_carries_no_display_name_and_no_email()
    {
        var fixture = new Fixture(role: BusinessRole.Operator);

        await fixture.ChangeRole().Handle(MembershipIdValue, "Manager", CancellationToken.None);

        var printed = Assert.Single(fixture.Audit.Entries).ToString();
        Assert.DoesNotContain(MemberDisplayName, printed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(MemberEmail, printed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ada", printed, StringComparison.OrdinalIgnoreCase);
        // The member's own Entra pair is not published either: within the business the event names,
        // the membership id identifies them.
        Assert.DoesNotContain(MemberOid, printed, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    private static MemberMembership Membership(
        int membershipId,
        string objectId = MemberOid,
        BusinessRole role = BusinessRole.Manager,
        bool isActive = true,
        string? displayName = MemberDisplayName,
        bool withIdentity = true) =>
        new(
            new BusinessMember(
                membershipId,
                displayName,
                MemberEmail,
                BusinessRoles.IsSupported(role) ? role : null,
                isActive,
                JoinedAt),
            withIdentity ? Identity(objectId) : null,
            role);

    private static ActorIdentity Identity(string objectId)
    {
        Assert.True(ActorIdentity.TryCreate(Tid, objectId, out var identity));
        return identity!;
    }

    /// <summary>
    /// One member change wired exactly as the composition root wires it, over fakes that share one
    /// operations log so a test can assert the order as well as the outcome.
    /// </summary>
    private sealed class Fixture
    {
        public Fixture(
            BusinessRole role = BusinessRole.Manager,
            bool isActive = true,
            string? identity = MemberOid)
        {
            Store = new FakeBusinessMemberStore { Observer = WriteStore.Record };
            Store.Add(
                BusinessIdValue,
                Membership(
                    MembershipIdValue,
                    identity ?? MemberOid,
                    role,
                    isActive,
                    withIdentity: identity is not null));

            // The business keeps an Owner unless a test says otherwise, so the Owner rule passes by
            // default and the tests that are about it have to arrange their own state.
            WriteStore.BusinessMembershipRoles.Add(new BusinessMembershipRole(BusinessRole.Owner, true));

            Writer = new MemberChangeWriter(
                new FakeCurrentBusinessProvider(BusinessIdValue),
                FakeAuthenticatedActorAccessor.Identified(Identity(ActorOid)),
                Store,
                new MembershipWriteGuard(WriteStore),
                Audit,
                new FakeClock(ChangedAt));
        }

        public FakeBusinessMembershipWriteStore WriteStore { get; } = new();

        public FakeBusinessMemberStore Store { get; }

        public FakeMemberManagementAudit Audit { get; } = new();

        public MemberChangeWriter Writer { get; }

        public IReadOnlyList<string> Operations => WriteStore.Operations;

        public ChangeMemberRole ChangeRole() => new(Writer);

        public RevokeMember Revoke() => new(Writer);

        public ReinstateMember Reinstate() => new(Writer);
    }
}
