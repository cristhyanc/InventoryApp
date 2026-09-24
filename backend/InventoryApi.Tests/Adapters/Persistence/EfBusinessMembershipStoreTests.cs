using Inventory.Domain.Tenancy;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational tests for the membership lookup behind issue #64. These use SQLite rather than
/// InMemory on purpose: the unique index, the NOCASE collation, and the case-insensitive lookup
/// are relational behaviour that InMemory would not prove.
/// </summary>
public class EfBusinessMembershipStoreTests
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "22222222-2222-2222-2222-222222222222";
    private const string OtherOid = "33333333-3333-3333-3333-333333333333";

    private static async Task<(SqliteConnection Connection, DbContextOptions<AppDbContext> Options)> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.EnsureCreatedAsync();
        return (connection, options);
    }

    private static ActorIdentity Actor(string tid = Tid, string oid = Oid)
    {
        Assert.True(ActorIdentity.TryCreate(tid, oid, out var actor));
        return actor!;
    }

    private static async Task<Business> AddBusinessAsync(AppDbContext db, string name, bool isActive = true)
    {
        var business = new Business { Name = name, IsActive = isActive, CreatedAtUtc = DateTime.UtcNow };
        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        return business;
    }

    private static async Task AddMembershipAsync(
        AppDbContext db,
        int businessId,
        string tid = Tid,
        string oid = Oid,
        bool isActive = true)
    {
        db.BusinessMemberships.Add(new BusinessMembership
        {
            BusinessId = businessId,
            DirectoryTenantId = tid,
            ObjectId = oid,
            IsActive = isActive,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Returns_the_membership_and_its_business_active_state()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var business = await AddBusinessAsync(db, "Vending Co");
            await AddMembershipAsync(db, business.Id);

            var memberships = await new EfBusinessMembershipStore(db)
                .FindMembershipsAsync(Actor(), CancellationToken.None);

            var membership = Assert.Single(memberships);
            Assert.Equal(BusinessId.From(business.Id), membership.BusinessId);
            Assert.True(membership.IsActive);
            Assert.True(membership.BusinessIsActive);
        }
    }

    [Fact]
    public async Task An_actor_with_no_membership_row_gets_nothing()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var business = await AddBusinessAsync(db, "Vending Co");
            await AddMembershipAsync(db, business.Id);

            var memberships = await new EfBusinessMembershipStore(db)
                .FindMembershipsAsync(Actor(oid: OtherOid), CancellationToken.None);

            Assert.Empty(memberships);
            Assert.Equal(
                BusinessAccessDenialReason.MembershipMissing,
                BusinessMembershipResolutionPolicy.Resolve(memberships).DenialReason);
        }
    }

    /// <summary>
    /// Name is a display name, not an identifier. Two businesses may trade under the same name,
    /// and ownership is decided by the key and the membership rows alone, so the schema must not
    /// impose a global uniqueness constraint on it.
    /// </summary>
    [Fact]
    public async Task Two_businesses_may_share_a_display_name()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);

            var first = await AddBusinessAsync(db, "Vending Co");
            var second = await AddBusinessAsync(db, "Vending Co");

            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(2, await db.Businesses.AsNoTracking().CountAsync());
        }
    }

    /// <summary>
    /// The same object id in a different Entra directory is a different actor, so the lookup
    /// must not match on either half of the pair alone.
    /// </summary>
    [Fact]
    public async Task Same_object_id_in_a_different_directory_tenant_does_not_match()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var business = await AddBusinessAsync(db, "Vending Co");
            await AddMembershipAsync(db, business.Id);

            var memberships = await new EfBusinessMembershipStore(db)
                .FindMembershipsAsync(
                    Actor(tid: "44444444-4444-4444-4444-444444444444"),
                    CancellationToken.None);

            Assert.Empty(memberships);
        }
    }

    /// <summary>
    /// Entra GUID claim values are case-insensitive, and a bootstrap row may be entered by hand
    /// in either casing. The NOCASE columns make the lookup match regardless.
    /// </summary>
    [Fact]
    public async Task Lookup_matches_a_membership_row_stored_in_different_casing()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var business = await AddBusinessAsync(db, "Vending Co");
            await AddMembershipAsync(db, business.Id, tid: Tid.ToLowerInvariant(), oid: Oid.ToLowerInvariant());

            var memberships = await new EfBusinessMembershipStore(db)
                .FindMembershipsAsync(Actor(), CancellationToken.None);

            Assert.Single(memberships);
        }
    }

    /// <summary>
    /// The adapter must surface both memberships rather than picking one, so the policy can deny
    /// the ambiguous actor. This is the cross-business leak this boundary exists to prevent.
    /// </summary>
    [Fact]
    public async Task Memberships_in_two_businesses_are_all_returned_and_resolve_to_a_denial()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var first = await AddBusinessAsync(db, "Vending Co");
            var second = await AddBusinessAsync(db, "Other Vending Co");
            await AddMembershipAsync(db, first.Id);
            await AddMembershipAsync(db, second.Id);

            var memberships = await new EfBusinessMembershipStore(db)
                .FindMembershipsAsync(Actor(), CancellationToken.None);

            Assert.Equal(2, memberships.Count);
            Assert.Equal(
                BusinessAccessDenialReason.MembershipAmbiguous,
                BusinessMembershipResolutionPolicy.Resolve(memberships).DenialReason);
        }
    }

    [Fact]
    public async Task A_revoked_membership_is_returned_and_resolves_to_a_denial()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var business = await AddBusinessAsync(db, "Vending Co");
            await AddMembershipAsync(db, business.Id, isActive: false);

            var memberships = await new EfBusinessMembershipStore(db)
                .FindMembershipsAsync(Actor(), CancellationToken.None);

            Assert.False(Assert.Single(memberships).IsActive);
            Assert.Equal(
                BusinessAccessDenialReason.MembershipInactive,
                BusinessMembershipResolutionPolicy.Resolve(memberships).DenialReason);
        }
    }

    [Fact]
    public async Task A_membership_in_a_deactivated_business_resolves_to_a_denial()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var business = await AddBusinessAsync(db, "Closed Vending Co", isActive: false);
            await AddMembershipAsync(db, business.Id);

            var memberships = await new EfBusinessMembershipStore(db)
                .FindMembershipsAsync(Actor(), CancellationToken.None);

            Assert.False(Assert.Single(memberships).BusinessIsActive);
            Assert.Equal(
                BusinessAccessDenialReason.BusinessInactive,
                BusinessMembershipResolutionPolicy.Resolve(memberships).DenialReason);
        }
    }

    /// <summary>
    /// The schema refuses a duplicate membership for the same (actor, business), including one
    /// that differs only in GUID casing - so duplicates cannot quietly accumulate and make an
    /// otherwise valid actor ambiguous.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_membership_for_the_same_actor_and_business_is_rejected(bool differentCasing)
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var business = await AddBusinessAsync(db, "Vending Co");
            await AddMembershipAsync(db, business.Id);

            var duplicateTid = differentCasing ? Tid.ToLowerInvariant() : Tid;
            var duplicateOid = differentCasing ? Oid.ToLowerInvariant() : Oid;

            await Assert.ThrowsAsync<DbUpdateException>(
                () => AddMembershipAsync(db, business.Id, tid: duplicateTid, oid: duplicateOid));
        }
    }

    /// <summary>
    /// Deleting a business that still has approved actors must fail rather than silently drop its
    /// authorization rows. The check is a raw SQL delete so it proves the foreign key is enforced
    /// by the database itself, not only by whatever EF happens to have in its change tracker.
    /// </summary>
    [Fact]
    public async Task Deleting_a_business_that_still_has_memberships_is_rejected_by_the_database()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var business = await AddBusinessAsync(db, "Vending Co");
            await AddMembershipAsync(db, business.Id);

            var exception = await Assert.ThrowsAsync<SqliteException>(
                () => db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM Businesses WHERE Id = {0};",
                    business.Id));

            Assert.Contains("FOREIGN KEY constraint failed", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Single(await db.BusinessMemberships.AsNoTracking().ToListAsync());
        }
    }
}
