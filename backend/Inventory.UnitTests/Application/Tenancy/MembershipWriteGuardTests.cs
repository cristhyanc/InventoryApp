using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Application.Tenancy;

/// <summary>
/// The shared membership write path (issue #522).
///
/// What these tests pin down is the shape of the operation rather than the rules themselves - the
/// rules have their own Domain tests, and the real serialisation has a relational concurrency test.
/// The shape is what the later member-management tasks depend on: the rules are re-checked inside
/// the transaction the write runs in, the owner rule is judged on the state the write left behind,
/// and a refusal commits nothing and reports the one stable conflict message.
/// </summary>
public class MembershipWriteGuardTests
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "22222222-2222-2222-2222-222222222222";
    private const string Written = "write";

    private static ActorIdentity Actor()
    {
        Assert.True(ActorIdentity.TryCreate(Tid, Oid, out var actor));
        return actor!;
    }

    private static MembershipWriteGuard Guard(FakeBusinessMembershipWriteStore store) => new(store);

    /// <summary>
    /// The happy path for a write that gives somebody an active membership: the transaction opens,
    /// eligibility is read inside it, the write runs, and the change is committed.
    /// </summary>
    [Fact]
    public async Task An_eligible_identity_gains_a_membership_inside_one_committed_transaction()
    {
        var store = new FakeBusinessMembershipWriteStore();

        var created = await Guard(store).ExecuteAsync(
            new MembershipWriteIntent(IdentityGainingAnActiveMembership: Actor()),
            token =>
            {
                store.Record(Written);
                return Task.FromResult(42);
            },
            CancellationToken.None);

        Assert.Equal(42, created);
        Assert.Equal(
            [
                FakeBusinessMembershipWriteStore.Begin,
                FakeBusinessMembershipWriteStore.IdentityRead,
                Written,
                FakeBusinessMembershipWriteStore.Commit,
                FakeBusinessMembershipWriteStore.Dispose,
            ],
            store.Operations);
        Assert.Equal(Actor(), store.LastQueriedIdentity);
    }

    /// <summary>
    /// The refusal: the write never runs, nothing is committed, and the caller is given the one
    /// stable conflict - a <see cref="DomainConflictException"/>, which the API boundary returns as
    /// <c>409</c> with this message verbatim.
    /// </summary>
    [Fact]
    public async Task An_identity_with_an_active_membership_elsewhere_is_refused_before_the_write()
    {
        var store = new FakeBusinessMembershipWriteStore();
        store.IdentityMemberships.Add(new IdentityMembership(7, IsActive: true));

        var exception = await Assert.ThrowsAsync<DomainConflictException>(() => Guard(store).ExecuteAsync(
            new MembershipWriteIntent(IdentityGainingAnActiveMembership: Actor()),
            token =>
            {
                store.Record(Written);
                return Task.CompletedTask;
            },
            CancellationToken.None));

        Assert.Equal(MembershipEligibility.AlreadyAMemberMessage, exception.Message);
        Assert.DoesNotContain(Written, store.Operations);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.Commit, store.Operations);
        Assert.Contains(FakeBusinessMembershipWriteStore.Dispose, store.Operations);
    }

    /// <summary>
    /// The exclusion travels from the intent into the rule: an operation on an already-active
    /// membership - an approval, a role change - is not refused by its own row.
    /// </summary>
    [Fact]
    public async Task The_excluded_membership_does_not_refuse_the_operation()
    {
        var store = new FakeBusinessMembershipWriteStore();
        store.IdentityMemberships.Add(new IdentityMembership(7, IsActive: true));

        await Guard(store).ExecuteAsync(
            new MembershipWriteIntent(
                IdentityGainingAnActiveMembership: Actor(),
                ExcludedMembershipId: 7),
            token =>
            {
                store.Record(Written);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Contains(Written, store.Operations);
        Assert.Contains(FakeBusinessMembershipWriteStore.Commit, store.Operations);
    }

    /// <summary>
    /// And the exclusion is not a way to skip the rule: another active membership still refuses the
    /// operation.
    /// </summary>
    [Fact]
    public async Task An_excluded_membership_does_not_hide_a_second_active_membership()
    {
        var store = new FakeBusinessMembershipWriteStore();
        store.IdentityMemberships.Add(new IdentityMembership(7, IsActive: true));
        store.IdentityMemberships.Add(new IdentityMembership(9, IsActive: true));

        await Assert.ThrowsAsync<DomainConflictException>(() => Guard(store).ExecuteAsync(
            new MembershipWriteIntent(
                IdentityGainingAnActiveMembership: Actor(),
                ExcludedMembershipId: 7),
            token => Task.CompletedTask,
            CancellationToken.None));

        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.Commit, store.Operations);
    }

    /// <summary>
    /// An exclusion with no identity to check is a check that was meant to run and would not.
    /// It is refused as the developer error it is, before a transaction is even opened.
    /// </summary>
    [Fact]
    public async Task An_exclusion_without_an_identity_is_refused_as_a_developer_error()
    {
        var store = new FakeBusinessMembershipWriteStore();

        await Assert.ThrowsAsync<ArgumentException>(() => Guard(store).ExecuteAsync(
            new MembershipWriteIntent(ExcludedMembershipId: 7),
            token => Task.CompletedTask,
            CancellationToken.None));

        Assert.Empty(store.Operations);
    }

    /// <summary>
    /// The owner rule is judged after the write, on what the business is left with. The write here
    /// demotes the only Owner, which the guard can only notice by reading the business back.
    /// </summary>
    [Fact]
    public async Task A_change_that_removes_the_last_active_owner_is_refused_and_rolled_back()
    {
        var store = new FakeBusinessMembershipWriteStore();
        store.BusinessMembershipRoles.Add(new BusinessMembershipRole(BusinessRole.Owner, IsActive: true));

        var exception = await Assert.ThrowsAsync<DomainConflictException>(() => Guard(store).ExecuteAsync(
            new MembershipWriteIntent(BusinessKeepingAnActiveOwner: BusinessId.From(3)),
            token =>
            {
                store.Record(Written);
                store.BusinessMembershipRoles.Clear();
                store.BusinessMembershipRoles.Add(
                    new BusinessMembershipRole(BusinessRole.Manager, IsActive: true));
                return Task.CompletedTask;
            },
            CancellationToken.None));

        Assert.Equal(BusinessOwnerRetention.LastActiveOwnerMessage, exception.Message);
        Assert.Equal(BusinessId.From(3), store.LastQueriedBusiness);

        // Read after the write, and never committed - so the demotion is rolled back with the
        // transaction rather than reported after the fact.
        Assert.Equal(
            [
                FakeBusinessMembershipWriteStore.Begin,
                Written,
                FakeBusinessMembershipWriteStore.BusinessRead,
                FakeBusinessMembershipWriteStore.Dispose,
            ],
            store.Operations);
    }

    /// <summary>
    /// The same change commits while another active Owner remains, which is the case that must not
    /// be refused: the rule is about the business keeping an Owner, not about Owners being
    /// unchangeable.
    /// </summary>
    [Fact]
    public async Task A_change_that_leaves_another_active_owner_is_committed()
    {
        var store = new FakeBusinessMembershipWriteStore();
        store.BusinessMembershipRoles.AddRange(
            new BusinessMembershipRole(BusinessRole.Owner, IsActive: true),
            new BusinessMembershipRole(BusinessRole.Owner, IsActive: true));

        await Guard(store).ExecuteAsync(
            new MembershipWriteIntent(BusinessKeepingAnActiveOwner: BusinessId.From(3)),
            token =>
            {
                store.BusinessMembershipRoles.RemoveAt(0);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Contains(FakeBusinessMembershipWriteStore.Commit, store.Operations);
    }

    /// <summary>
    /// Both rules at once - the shape of #509's approval, which activates one membership and must
    /// leave its business administrable. Each is read from its own side of the write.
    /// </summary>
    [Fact]
    public async Task Both_rules_are_checked_around_one_write()
    {
        var store = new FakeBusinessMembershipWriteStore();
        store.BusinessMembershipRoles.Add(new BusinessMembershipRole(BusinessRole.Owner, IsActive: true));

        await Guard(store).ExecuteAsync(
            new MembershipWriteIntent(
                IdentityGainingAnActiveMembership: Actor(),
                ExcludedMembershipId: 7,
                BusinessKeepingAnActiveOwner: BusinessId.From(3)),
            token =>
            {
                store.Record(Written);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(
            [
                FakeBusinessMembershipWriteStore.Begin,
                FakeBusinessMembershipWriteStore.IdentityRead,
                Written,
                FakeBusinessMembershipWriteStore.BusinessRead,
                FakeBusinessMembershipWriteStore.Commit,
                FakeBusinessMembershipWriteStore.Dispose,
            ],
            store.Operations);
    }

    /// <summary>
    /// The database refusing the write through the filtered unique index becomes the same stable
    /// conflict as the eligibility check it backs up, rather than an unexplained failure. Which
    /// provider exception that is stays behind the port: the store answers whether it is one.
    /// </summary>
    [Fact]
    public async Task A_unique_index_violation_becomes_the_same_stable_conflict()
    {
        var store = new FakeBusinessMembershipWriteStore
        {
            DuplicateActiveMembership = exception => exception is InvalidOperationException,
        };

        var exception = await Assert.ThrowsAsync<DomainConflictException>(() => Guard(store).ExecuteAsync(
            new MembershipWriteIntent(IdentityGainingAnActiveMembership: Actor()),
            token => throw new InvalidOperationException("the index refused it"),
            CancellationToken.None));

        Assert.Equal(MembershipEligibility.AlreadyAMemberMessage, exception.Message);
        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.Commit, store.Operations);
    }

    /// <summary>
    /// Every other failure keeps propagating as itself. A guard that translated any exception from
    /// a membership write into a 409 would report a broken database as a routine refusal.
    /// </summary>
    [Fact]
    public async Task Any_other_failure_propagates_untranslated()
    {
        var store = new FakeBusinessMembershipWriteStore();

        await Assert.ThrowsAsync<TimeoutException>(() => Guard(store).ExecuteAsync(
            new MembershipWriteIntent(IdentityGainingAnActiveMembership: Actor()),
            token => throw new TimeoutException("the database went away"),
            CancellationToken.None));

        Assert.DoesNotContain(FakeBusinessMembershipWriteStore.Commit, store.Operations);
        Assert.Contains(FakeBusinessMembershipWriteStore.Dispose, store.Operations);
    }

    /// <summary>
    /// A write that declares no rule still gets the transaction - the only thing a deactivation in
    /// a business that needs no Owner yet (#507's rejection) asks for.
    /// </summary>
    [Fact]
    public async Task A_write_declaring_no_rule_still_runs_in_the_transaction()
    {
        var store = new FakeBusinessMembershipWriteStore();

        await Guard(store).ExecuteAsync(
            default,
            token =>
            {
                store.Record(Written);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(
            [
                FakeBusinessMembershipWriteStore.Begin,
                Written,
                FakeBusinessMembershipWriteStore.Commit,
                FakeBusinessMembershipWriteStore.Dispose,
            ],
            store.Operations);
    }

    [Fact]
    public async Task A_missing_write_is_refused()
    {
        var store = new FakeBusinessMembershipWriteStore();

        await Assert.ThrowsAsync<ArgumentNullException>(() => Guard(store).ExecuteAsync(
            default,
            (Func<CancellationToken, Task>)null!,
            CancellationToken.None));

        Assert.Empty(store.Operations);
    }
}
