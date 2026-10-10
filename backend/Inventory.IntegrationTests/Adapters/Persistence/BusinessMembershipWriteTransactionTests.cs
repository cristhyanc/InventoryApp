using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The shared membership write path under real concurrency (issue #522).
///
/// This is the test the design exists for. The one-active-membership and last-Owner rules are
/// check-then-write rules, and a check-then-write rule that is not serialised is not a rule: two
/// simultaneous requests both read "no active membership", or both read "there is another Owner",
/// and both write. The outcomes are not cosmetic - a person with active memberships in two
/// businesses is denied both of them, and a business with no active Owner cannot be administered
/// by anybody inside it.
///
/// It therefore runs against a real SQLite **file** with two independent connections, because that
/// is the only way to have two genuine writers: a shared in-memory database is one connection, and
/// InMemory has no locking at all. The two operations are released together at a barrier, so the
/// window the rules have to close is actually opened.
/// </summary>
public class BusinessMembershipWriteTransactionTests : IDisposable
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string FirstOid = "22222222-2222-2222-2222-222222222222";
    private const string SecondOid = "33333333-3333-3333-3333-333333333333";
    private const int FirstBusinessId = 1;
    private const int SecondBusinessId = 2;

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"inventory-membership-writes-{Guid.NewGuid():N}.db");

    private readonly string _connectionString;

    public BusinessMembershipWriteTransactionTests()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

        using var db = TestAppDbContext.Unrestricted(Options());
        db.Database.Migrate();
        db.Businesses.AddRange(
            new Business { Id = FirstBusinessId, Name = "Vending Co", IsActive = true, CreatedAtUtc = DateTime.UtcNow },
            new Business { Id = SecondBusinessId, Name = "Other Vending Co", IsActive = true, CreatedAtUtc = DateTime.UtcNow });
        db.SaveChanges();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        GC.SuppressFinalize(this);
    }

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options;

    private static ActorIdentity Actor(string oid)
    {
        Assert.True(ActorIdentity.TryCreate(Tid, oid, out var actor));
        return actor!;
    }

    private static BusinessMembership NewMembership(
        int businessId,
        string oid,
        BusinessRole role = BusinessRole.Owner,
        bool isActive = true) =>
        new()
        {
            BusinessId = businessId,
            DirectoryTenantId = Tid,
            ObjectId = oid,
            Role = role,
            IsActive = isActive,
            CreatedAtUtc = DateTime.UtcNow,
            StatusChangedAtUtc = DateTime.UtcNow,
        };

    private void Seed(params BusinessMembership[] memberships)
    {
        using var db = TestAppDbContext.Unrestricted(Options());
        db.BusinessMemberships.AddRange(memberships);
        db.SaveChanges();
    }

    private List<BusinessMembership> ReadMemberships()
    {
        using var db = TestAppDbContext.Unrestricted(Options());
        return db.BusinessMemberships.AsNoTracking().OrderBy(membership => membership.Id).ToList();
    }

    /// <summary>
    /// Runs two membership operations released at the same instant, and returns the failure each
    /// one produced (<c>null</c> for the one that succeeded).
    /// </summary>
    private static async Task<Exception?[]> RaceAsync(params Func<CancellationToken, Task>[] operations)
    {
        using var barrier = new Barrier(operations.Length);

        var attempts = operations
            .Select(operation => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                try
                {
                    await operation(CancellationToken.None);
                    return (Exception?)null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            }))
            .ToArray();

        return await Task.WhenAll(attempts);
    }

    /// <summary>
    /// Two requests at once trying to give one person an active membership in two different
    /// businesses. Exactly one may win, and the loser has to be told the same thing it would have
    /// been told sequentially - not a database error, and certainly not silence.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_activations_of_one_identity_leave_exactly_one()
    {
        var failures = await RaceAsync(
            token => JoinAsync(FirstBusinessId, token),
            token => JoinAsync(SecondBusinessId, token));

        var refused = Assert.Single(failures.Where(failure => failure is not null));
        Assert.Single(failures.Where(failure => failure is null));

        var conflict = Assert.IsType<DomainConflictException>(refused);
        Assert.Equal(MembershipEligibility.AlreadyAMemberMessage, conflict.Message);

        // The losing attempt's row is gone with its transaction, not left behind inactive.
        var membership = Assert.Single(ReadMemberships());
        Assert.True(membership.IsActive);

        async Task JoinAsync(int businessId, CancellationToken token)
        {
            // A denied scope: somebody joining a business is not yet a member of any, which is
            // exactly the scope such a request carries. Membership rows are not tenant-owned, so
            // the write is still the ordinary one.
            await using var db = TestAppDbContext.Denied(Options());
            var guard = new MembershipWriteGuard(new EfBusinessMembershipWriteStore(db));

            await guard.ExecuteAsync(
                new MembershipWriteIntent(IdentityGainingAnActiveMembership: Actor(FirstOid)),
                async writeToken =>
                {
                    db.BusinessMemberships.Add(NewMembership(businessId, FirstOid));
                    await db.SaveChangesAsync(writeToken);
                },
                token);
        }
    }

    /// <summary>
    /// Two Owners demoting each other at the same instant. Sequentially the second demotion is
    /// refused; simultaneously, without the shared write transaction, both would read "there is
    /// another Owner" and the business would end up with none.
    /// </summary>
    [Fact]
    public async Task Two_owners_demoting_each_other_leave_at_least_one_owner()
    {
        Seed(
            NewMembership(FirstBusinessId, FirstOid),
            NewMembership(FirstBusinessId, SecondOid));

        var memberships = ReadMemberships();
        var first = memberships[0].Id;
        var second = memberships[1].Id;

        var failures = await RaceAsync(
            token => DemoteAsync(second, token),
            token => DemoteAsync(first, token));

        var refused = Assert.Single(failures.Where(failure => failure is not null));
        Assert.Single(failures.Where(failure => failure is null));

        var conflict = Assert.IsType<DomainConflictException>(refused);
        Assert.Equal(BusinessOwnerRetention.LastActiveOwnerMessage, conflict.Message);

        // Exactly one Owner left: one demotion applied, the other rolled back whole.
        var after = ReadMemberships();
        Assert.Single(after.Where(membership => membership.IsActive && membership.Role == BusinessRole.Owner));
        Assert.Single(after.Where(membership => membership.Role == BusinessRole.Manager));

        async Task DemoteAsync(int membershipId, CancellationToken token)
        {
            await using var db = TestAppDbContext.For(Options(), FirstBusinessId);
            var guard = new MembershipWriteGuard(new EfBusinessMembershipWriteStore(db));

            await guard.ExecuteAsync(
                new MembershipWriteIntent(
                    BusinessKeepingAnActiveOwner: BusinessId.From(FirstBusinessId)),
                async writeToken =>
                {
                    var membership = await db.BusinessMemberships
                        .SingleAsync(row => row.Id == membershipId, writeToken);
                    membership.Role = BusinessRole.Manager;
                    await db.SaveChangesAsync(writeToken);
                },
                token);
        }
    }

    /// <summary>
    /// The sequential half of the same rule, and the one an operator meets in practice: the last
    /// active Owner cannot be revoked, and the revocation is rolled back rather than reported as
    /// applied.
    /// </summary>
    [Fact]
    public async Task Revoking_the_last_active_owner_is_refused_and_rolled_back()
    {
        Seed(
            NewMembership(FirstBusinessId, FirstOid),
            NewMembership(FirstBusinessId, SecondOid, role: BusinessRole.Manager));

        var owner = ReadMemberships().Single(membership => membership.Role == BusinessRole.Owner);

        await using var db = TestAppDbContext.For(Options(), FirstBusinessId);
        var guard = new MembershipWriteGuard(new EfBusinessMembershipWriteStore(db));

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() => guard.ExecuteAsync(
            new MembershipWriteIntent(BusinessKeepingAnActiveOwner: BusinessId.From(FirstBusinessId)),
            async writeToken =>
            {
                var membership = await db.BusinessMemberships
                    .SingleAsync(row => row.Id == owner.Id, writeToken);
                membership.IsActive = false;
                // A revocation changes the membership's state, so it stamps the instant with it -
                // the convention #504's revocation inherits.
                membership.StatusChangedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(writeToken);
            },
            CancellationToken.None));

        Assert.Equal(BusinessOwnerRetention.LastActiveOwnerMessage, conflict.Message);
        Assert.True(ReadMemberships().Single(membership => membership.Id == owner.Id).IsActive);
    }

    /// <summary>
    /// The exclusion through the real database: promoting a member who already holds an active
    /// membership in this business must not be refused by that same membership. Without the
    /// exclusion every role change on an active member would be impossible.
    /// </summary>
    [Fact]
    public async Task A_members_own_active_membership_does_not_refuse_a_change_to_it()
    {
        Seed(
            NewMembership(FirstBusinessId, FirstOid),
            NewMembership(FirstBusinessId, SecondOid, role: BusinessRole.Manager));

        var manager = ReadMemberships().Single(membership => membership.Role == BusinessRole.Manager);

        await using var db = TestAppDbContext.For(Options(), FirstBusinessId);
        var guard = new MembershipWriteGuard(new EfBusinessMembershipWriteStore(db));

        await guard.ExecuteAsync(
            new MembershipWriteIntent(
                IdentityGainingAnActiveMembership: Actor(SecondOid),
                ExcludedMembershipId: manager.Id,
                BusinessKeepingAnActiveOwner: BusinessId.From(FirstBusinessId)),
            async writeToken =>
            {
                var membership = await db.BusinessMemberships
                    .SingleAsync(row => row.Id == manager.Id, writeToken);
                membership.Role = BusinessRole.Owner;
                await db.SaveChangesAsync(writeToken);
            },
            CancellationToken.None);

        Assert.Equal(
            2,
            ReadMemberships().Count(membership => membership.IsActive && membership.Role == BusinessRole.Owner));
    }

    /// <summary>
    /// Without the exclusion, the same change is refused - which is what makes the exclusion an
    /// explicit decision rather than a formality the rule could do without.
    /// </summary>
    [Fact]
    public async Task The_same_change_is_refused_when_the_membership_is_not_excluded()
    {
        Seed(NewMembership(FirstBusinessId, FirstOid));

        var owner = ReadMemberships().Single();

        await using var db = TestAppDbContext.For(Options(), FirstBusinessId);
        var guard = new MembershipWriteGuard(new EfBusinessMembershipWriteStore(db));

        await Assert.ThrowsAsync<DomainConflictException>(() => guard.ExecuteAsync(
            new MembershipWriteIntent(IdentityGainingAnActiveMembership: Actor(FirstOid)),
            async writeToken =>
            {
                var membership = await db.BusinessMemberships
                    .SingleAsync(row => row.Id == owner.Id, writeToken);
                membership.Role = BusinessRole.Manager;
                await db.SaveChangesAsync(writeToken);
            },
            CancellationToken.None));

        Assert.Equal(BusinessRole.Owner, ReadMemberships().Single().Role);
    }

    /// <summary>
    /// The index as the last line of defence. The intent here deliberately declares no identity, so
    /// the eligibility check does not run and the write reaches the database - where the filtered
    /// unique index refuses it. The caller must still be given the same stable conflict, because an
    /// index violation surfacing as a provider error would be a <c>500</c> for a refusal the
    /// contract answers with <c>409</c>.
    /// </summary>
    [Fact]
    public async Task A_second_active_membership_refused_by_the_index_becomes_the_same_conflict()
    {
        Seed(NewMembership(FirstBusinessId, FirstOid));

        await using var db = TestAppDbContext.Denied(Options());
        var guard = new MembershipWriteGuard(new EfBusinessMembershipWriteStore(db));

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() => guard.ExecuteAsync(
            default,
            async writeToken =>
            {
                db.BusinessMemberships.Add(NewMembership(SecondBusinessId, FirstOid));
                await db.SaveChangesAsync(writeToken);
            },
            CancellationToken.None));

        Assert.Equal(MembershipEligibility.AlreadyAMemberMessage, conflict.Message);
        Assert.Single(ReadMemberships());
    }

    /// <summary>
    /// A different unique-index violation on the same table is a different fact - this person
    /// already has a row in this business - and must not be reported as "already a member of a
    /// business". It keeps propagating as the provider failure it is, which the API answers as an
    /// unexpected error rather than as a membership refusal nobody can act on.
    /// </summary>
    [Fact]
    public async Task A_duplicate_row_in_the_same_business_is_not_reported_as_a_membership_conflict()
    {
        Seed(NewMembership(FirstBusinessId, FirstOid, isActive: false));

        await using var db = TestAppDbContext.Denied(Options());
        var guard = new MembershipWriteGuard(new EfBusinessMembershipWriteStore(db));

        await Assert.ThrowsAsync<DbUpdateException>(() => guard.ExecuteAsync(
            default,
            async writeToken =>
            {
                db.BusinessMemberships.Add(NewMembership(FirstBusinessId, FirstOid, isActive: false));
                await db.SaveChangesAsync(writeToken);
            },
            CancellationToken.None));

        Assert.Single(ReadMemberships());
    }

    /// <summary>
    /// A membership write that no rule refuses commits, and the row it wrote is really there -
    /// the transaction is a transaction, not a rollback with extra steps.
    /// </summary>
    [Fact]
    public async Task A_permitted_membership_write_is_committed()
    {
        await using var db = TestAppDbContext.Denied(Options());
        var guard = new MembershipWriteGuard(new EfBusinessMembershipWriteStore(db));

        var created = await guard.ExecuteAsync(
            new MembershipWriteIntent(IdentityGainingAnActiveMembership: Actor(FirstOid)),
            async writeToken =>
            {
                var membership = NewMembership(FirstBusinessId, FirstOid);
                db.BusinessMemberships.Add(membership);
                await db.SaveChangesAsync(writeToken);
                return membership.Id;
            },
            CancellationToken.None);

        var stored = Assert.Single(ReadMemberships());
        Assert.Equal(created, stored.Id);
        Assert.True(stored.IsActive);
        Assert.Equal(BusinessRole.Owner, stored.Role);
    }

    /// <summary>
    /// A revoked membership in another business does not block joining a new one: revoking is how
    /// a person moves between businesses, and the write path has to allow exactly that.
    /// </summary>
    [Fact]
    public async Task A_revoked_membership_elsewhere_does_not_block_a_new_one()
    {
        Seed(NewMembership(SecondBusinessId, FirstOid, isActive: false));

        await using var db = TestAppDbContext.Denied(Options());
        var guard = new MembershipWriteGuard(new EfBusinessMembershipWriteStore(db));

        await guard.ExecuteAsync(
            new MembershipWriteIntent(IdentityGainingAnActiveMembership: Actor(FirstOid)),
            async writeToken =>
            {
                db.BusinessMemberships.Add(NewMembership(FirstBusinessId, FirstOid));
                await db.SaveChangesAsync(writeToken);
            },
            CancellationToken.None);

        var memberships = ReadMemberships();
        Assert.Equal(2, memberships.Count);
        Assert.Single(memberships.Where(membership => membership.IsActive));
    }

    /// <summary>
    /// A membership in a deactivated business still occupies the identity's one active membership.
    /// This is the case the rule is deliberately blind to the business's state for: a business
    /// nobody can currently reach is still the business that person belongs to, and the remedy is
    /// to revoke the membership deliberately rather than to let a second one be created.
    /// </summary>
    [Fact]
    public async Task An_active_membership_in_a_deactivated_business_still_blocks_a_new_one()
    {
        using (var setup = TestAppDbContext.Unrestricted(Options()))
        {
            var business = setup.Businesses.Single(row => row.Id == SecondBusinessId);
            business.IsActive = false;
            setup.SaveChanges();
        }

        Seed(NewMembership(SecondBusinessId, FirstOid));

        await using var db = TestAppDbContext.Denied(Options());
        var guard = new MembershipWriteGuard(new EfBusinessMembershipWriteStore(db));

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() => guard.ExecuteAsync(
            new MembershipWriteIntent(IdentityGainingAnActiveMembership: Actor(FirstOid)),
            async writeToken =>
            {
                db.BusinessMemberships.Add(NewMembership(FirstBusinessId, FirstOid));
                await db.SaveChangesAsync(writeToken);
            },
            CancellationToken.None));

        Assert.Equal(MembershipEligibility.AlreadyAMemberMessage, conflict.Message);
        Assert.Single(ReadMemberships());
    }
}
