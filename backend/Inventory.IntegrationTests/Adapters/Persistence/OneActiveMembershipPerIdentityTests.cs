using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The database half of the one-active-membership rule (issue #522): a unique partial index over
/// <c>(DirectoryTenantId, ObjectId)</c> for active rows only, so one identity cannot hold an
/// active membership in two businesses whatever the owning businesses' state.
///
/// Before this slice the schema allowed that state and only
/// <see cref="BusinessMembershipResolutionPolicy"/> refused it, by denying the actor every
/// business. Denying access after the fact is the right fail-closed answer but a poor boundary: it
/// is discovered at sign-in, by the person who has just been locked out of both businesses. The
/// index refuses the write instead.
///
/// Relational SQLite rather than InMemory, because a filtered unique index is exactly the
/// relational behaviour InMemory does not have.
/// </summary>
public class OneActiveMembershipPerIdentityTests
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "22222222-2222-2222-2222-222222222222";
    private const string OtherOid = "33333333-3333-3333-3333-333333333333";
    private const string OtherTid = "44444444-4444-4444-4444-444444444444";

    private static async Task<(SqliteConnection Connection, DbContextOptions<AppDbContext> Options)> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.EnsureCreatedAsync();
        return (connection, options);
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
        bool isActive = true,
        BusinessRole role = BusinessRole.Owner)
    {
        db.BusinessMemberships.Add(new BusinessMembership
        {
            BusinessId = businessId,
            DirectoryTenantId = tid,
            ObjectId = oid,
            Role = role,
            IsActive = isActive,
            CreatedAtUtc = DateTime.UtcNow,
            StatusChangedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The rule the index exists for: a second active membership in another business is refused by
    /// the database, whether the second business is active or not. "Whatever the business's status"
    /// is the point - a membership in a business nobody can currently reach still occupies the
    /// identity's one active membership, so it has to be revoked deliberately before that person
    /// joins somewhere else.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_second_active_membership_in_another_business_is_refused(bool secondBusinessIsActive)
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var first = await AddBusinessAsync(db, "Vending Co");
            var second = await AddBusinessAsync(db, "Other Vending Co", isActive: secondBusinessIsActive);
            await AddMembershipAsync(db, first.Id);

            await Assert.ThrowsAsync<DbUpdateException>(() => AddMembershipAsync(db, second.Id));

            Assert.Equal(1, await db.BusinessMemberships.AsNoTracking().CountAsync());
        }
    }

    /// <summary>
    /// The same refusal when the two rows differ only in GUID casing, which the NOCASE columns
    /// make the same identity. A case-shifted hand-entered row must not be a way around the rule.
    /// </summary>
    [Fact]
    public async Task A_second_active_membership_in_different_casing_is_refused()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var first = await AddBusinessAsync(db, "Vending Co");
            var second = await AddBusinessAsync(db, "Other Vending Co");
            await AddMembershipAsync(db, first.Id);

            await Assert.ThrowsAsync<DbUpdateException>(() => AddMembershipAsync(
                db,
                second.Id,
                tid: Tid.ToLowerInvariant(),
                oid: Oid.ToLowerInvariant()));
        }
    }

    /// <summary>
    /// Reactivating a revoked membership while another one is active is refused too. The index is
    /// filtered on the active rows, so it covers an <c>UPDATE</c> that makes a row active, not
    /// only an <c>INSERT</c> - which is what makes it a rule about state rather than about how the
    /// state was reached.
    /// </summary>
    [Fact]
    public async Task Reactivating_a_revoked_membership_while_another_is_active_is_refused()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var first = await AddBusinessAsync(db, "Vending Co");
            var second = await AddBusinessAsync(db, "Other Vending Co");
            await AddMembershipAsync(db, first.Id, isActive: false);
            await AddMembershipAsync(db, second.Id);

            var revoked = await db.BusinessMemberships.SingleAsync(m => m.BusinessId == first.Id);
            revoked.IsActive = true;

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    /// <summary>
    /// What the index must still allow: any number of revoked rows for one identity, so a
    /// membership history is kept rather than deleted, and the move from one business to another
    /// stays possible once the first membership is revoked.
    /// </summary>
    [Fact]
    public async Task Revoked_memberships_for_one_identity_may_exist_in_several_businesses()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var first = await AddBusinessAsync(db, "Vending Co");
            var second = await AddBusinessAsync(db, "Other Vending Co");
            var third = await AddBusinessAsync(db, "Third Vending Co");

            await AddMembershipAsync(db, first.Id, isActive: false);
            await AddMembershipAsync(db, second.Id, isActive: false);
            await AddMembershipAsync(db, third.Id);

            Assert.Equal(3, await db.BusinessMemberships.AsNoTracking().CountAsync());
            Assert.Equal(1, await db.BusinessMemberships.AsNoTracking().CountAsync(m => m.IsActive));
        }
    }

    /// <summary>
    /// And that the index constrains one identity, not a business or a directory: two people in
    /// one business, and the same object id in a different Entra directory, are all unaffected.
    /// </summary>
    [Fact]
    public async Task Different_identities_may_each_hold_an_active_membership()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var first = await AddBusinessAsync(db, "Vending Co");
            var second = await AddBusinessAsync(db, "Other Vending Co");

            await AddMembershipAsync(db, first.Id);
            await AddMembershipAsync(db, first.Id, oid: OtherOid);
            await AddMembershipAsync(db, second.Id, tid: OtherTid);

            Assert.Equal(3, await db.BusinessMemberships.AsNoTracking().CountAsync(m => m.IsActive));
        }
    }
}
