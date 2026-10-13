using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The member roster adapter of issue #533 against relational SQLite.
///
/// <c>BusinessMembership</c> carries no tenant query filter - it is the table that resolves the
/// tenant boundary - so the boundary for this adapter is the business id it puts in every
/// statement. These tests are where that is proved: a read sees one business's rows, a lookup by
/// membership key answers nothing for another business's key, and each write names only the column
/// it changes.
/// </summary>
public class EfBusinessMemberStoreTests
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string OidA = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string OidB = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string OidC = "cccccccc-cccc-cccc-cccc-cccccccccccc";
    private const string OidD = "dddddddd-dddd-dddd-dddd-dddddddddddd";
    private const int FirstBusinessId = 1;
    private const int SecondBusinessId = 2;

    private static readonly DateTime CreatedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ChangedAt = new(2026, 10, 11, 1, 0, 0, DateTimeKind.Utc);

    private static async Task<(SqliteConnection Connection, DbContextOptions<AppDbContext> Options)> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.MigrateAsync();
        setup.Businesses.AddRange(
            NewBusiness(FirstBusinessId, "Vending Co"),
            NewBusiness(SecondBusinessId, "Other Vending Co"));
        await setup.SaveChangesAsync();
        return (connection, options);
    }

    /// <summary>
    /// One business's members, in creation order, with revoked rows kept and the other business's
    /// rows absent. A member who has never signed in is reported with no display values rather
    /// than with a stand-in, and a stored role this deployment does not declare is reported as no
    /// known role - it already denies that member access everywhere, and naming it would put a
    /// role nobody granted on the wire.
    /// </summary>
    [Fact]
    public async Task It_lists_one_businesss_members_in_creation_order()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var seed = TestAppDbContext.Unrestricted(options);
            seed.BusinessMemberships.AddRange(
                Membership(FirstBusinessId, OidA, BusinessRole.Owner, "Ada Lovelace", "ada@vending.example"),
                Membership(FirstBusinessId, OidB, BusinessRole.Operator, null, null, isActive: false),
                // A role no BusinessRole declares: in production only a hand-written UPDATE or a
                // newer deployment can write one, and the cast is how a test writes the same thing.
                Membership(FirstBusinessId, OidC, (BusinessRole)0, "Grace Hopper", "grace@vending.example"),
                Membership(SecondBusinessId, OidD, BusinessRole.Owner, "Katherine Johnson", "k@other.example"));
            await seed.SaveChangesAsync();

            await using var db = TestAppDbContext.For(options, FirstBusinessId);
            var members = await new EfBusinessMemberStore(db)
                .ListAsync(BusinessId.From(FirstBusinessId), CancellationToken.None);

            Assert.Equal(
                [
                    ("Ada Lovelace", "ada@vending.example", BusinessRole.Owner, true),
                    (null, null, BusinessRole.Operator, false),
                    ("Grace Hopper", "grace@vending.example", null, true),
                ],
                members.Select(member => (member.DisplayName, member.Email, member.Role, member.IsActive)));
            Assert.All(members, member => Assert.Equal(CreatedAt, member.JoinedAtUtc));
        }
    }

    /// <summary>
    /// A lookup is by business <em>and</em> membership key together, so another business's key is
    /// not found rather than refused in a way that confirms it exists.
    /// </summary>
    [Fact]
    public async Task It_finds_nothing_for_another_businesss_membership_key()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            await using var seed = TestAppDbContext.Unrestricted(options);
            seed.BusinessMemberships.Add(Membership(SecondBusinessId, OidD, BusinessRole.Owner, null, null));
            await seed.SaveChangesAsync();
            var foreign = await seed.BusinessMemberships.AsNoTracking().Select(m => m.Id).SingleAsync();

            await using var db = TestAppDbContext.For(options, FirstBusinessId);
            var store = new EfBusinessMemberStore(db);

            Assert.Null(await store.FindAsync(BusinessId.From(FirstBusinessId), foreign, CancellationToken.None));
            Assert.Null(await store.FindAsync(BusinessId.From(FirstBusinessId), 999_999, CancellationToken.None));
            Assert.Empty(await store.ListAsync(BusinessId.From(FirstBusinessId), CancellationToken.None));
        }
    }

    /// <summary>
    /// The stored Entra pair is parsed by the same rule the sign-in path uses, so a membership row
    /// written in a different GUID spelling still names the same identity and the eligibility rule
    /// is re-checked for the right person. A pair that is not an Entra identity at all - which only
    /// a hand-written row can produce - yields no identity, and the caller fails closed on it.
    /// </summary>
    [Fact]
    public async Task It_normalises_the_stored_identity_and_reports_an_unusable_one_as_none()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            // Stored in the lower-case spelling a hand-entered row or another tool may use.
            var lowerCase = Membership(FirstBusinessId, OidA, BusinessRole.Manager, null, null);

            var unusable = Membership(FirstBusinessId, OidB, BusinessRole.Operator, null, null);
            unusable.ObjectId = "not-a-guid";

            await using var seed = TestAppDbContext.Unrestricted(options);
            seed.BusinessMemberships.AddRange(lowerCase, unusable);
            await seed.SaveChangesAsync();

            await using var db = TestAppDbContext.For(options, FirstBusinessId);
            var store = new EfBusinessMemberStore(db);

            var normalised = await store.FindAsync(BusinessId.From(FirstBusinessId), lowerCase.Id, CancellationToken.None);
            Assert.NotNull(normalised?.Identity);
            Assert.Equal(OidA.ToUpperInvariant(), normalised.Identity.ObjectId);
            Assert.Equal(Tid, normalised.Identity.DirectoryTenantId);
            Assert.Equal(BusinessRole.Manager, normalised.StoredRole);

            var broken = await store.FindAsync(BusinessId.From(FirstBusinessId), unusable.Id, CancellationToken.None);
            Assert.NotNull(broken);
            Assert.Null(broken.Identity);
        }
    }

    /// <summary>
    /// A role change moves what a member may do, not whether their membership is current, so it
    /// leaves <c>StatusChangedAtUtc</c> alone (issue #522) - and it names only that one column, so
    /// the display values, the identity pair and the created instant cannot move with it.
    /// </summary>
    [Fact]
    public async Task A_role_change_writes_the_role_alone()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var row = Membership(FirstBusinessId, "a", BusinessRole.Operator, "Ada Lovelace", "ada@vending.example");
            await using var seed = TestAppDbContext.Unrestricted(options);
            seed.BusinessMemberships.Add(row);
            await seed.SaveChangesAsync();

            await using (var db = TestAppDbContext.For(options, FirstBusinessId))
            {
                await new EfBusinessMemberStore(db).ChangeRoleAsync(
                    BusinessId.From(FirstBusinessId),
                    row.Id,
                    BusinessRole.Manager,
                    CancellationToken.None);
            }

            await using var read = TestAppDbContext.Unrestricted(options);
            var stored = await read.BusinessMemberships.AsNoTracking().SingleAsync();
            Assert.Equal(BusinessRole.Manager, stored.Role);
            Assert.True(stored.IsActive);
            Assert.Equal(CreatedAt, stored.StatusChangedAtUtc);
            Assert.Equal(CreatedAt, stored.CreatedAtUtc);
            Assert.Equal("Ada Lovelace", stored.DisplayName);
            Assert.Equal("ada@vending.example", stored.Email);
        }
    }

    /// <summary>
    /// A state change writes the flag and the instant it changed in one statement, so the row never
    /// records an instant for a state it does not hold - and still touches nothing else.
    /// </summary>
    [Fact]
    public async Task A_state_change_writes_the_flag_and_its_instant_together()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var row = Membership(FirstBusinessId, "a", BusinessRole.Manager, "Ada Lovelace", "ada@vending.example");
            await using var seed = TestAppDbContext.Unrestricted(options);
            seed.BusinessMemberships.Add(row);
            await seed.SaveChangesAsync();

            await using (var db = TestAppDbContext.For(options, FirstBusinessId))
            {
                await new EfBusinessMemberStore(db).ChangeActiveAsync(
                    BusinessId.From(FirstBusinessId),
                    row.Id,
                    isActive: false,
                    ChangedAt,
                    CancellationToken.None);
            }

            await using var read = TestAppDbContext.Unrestricted(options);
            var stored = await read.BusinessMemberships.AsNoTracking().SingleAsync();
            Assert.False(stored.IsActive);
            Assert.Equal(ChangedAt, stored.StatusChangedAtUtc);
            Assert.Equal(BusinessRole.Manager, stored.Role);
            Assert.Equal(CreatedAt, stored.CreatedAtUtc);
            Assert.Equal("Ada Lovelace", stored.DisplayName);
        }
    }

    /// <summary>
    /// A write names the business as well as the membership key, so a mismatched pair changes
    /// nothing at all rather than reaching another business's row.
    /// </summary>
    [Fact]
    public async Task A_write_for_another_business_changes_nothing()
    {
        var (connection, options) = await CreateSqliteAsync();
        await using (connection)
        {
            var row = Membership(SecondBusinessId, "d", BusinessRole.Owner, null, null);
            await using var seed = TestAppDbContext.Unrestricted(options);
            seed.BusinessMemberships.Add(row);
            await seed.SaveChangesAsync();

            await using (var db = TestAppDbContext.For(options, FirstBusinessId))
            {
                var store = new EfBusinessMemberStore(db);
                await store.ChangeRoleAsync(
                    BusinessId.From(FirstBusinessId), row.Id, BusinessRole.Operator, CancellationToken.None);
                await store.ChangeActiveAsync(
                    BusinessId.From(FirstBusinessId), row.Id, isActive: false, ChangedAt, CancellationToken.None);
            }

            await using var read = TestAppDbContext.Unrestricted(options);
            var stored = await read.BusinessMemberships.AsNoTracking().SingleAsync();
            Assert.Equal(BusinessRole.Owner, stored.Role);
            Assert.True(stored.IsActive);
            Assert.Equal(CreatedAt, stored.StatusChangedAtUtc);
        }
    }

    private static Business NewBusiness(int id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            TimeZoneId = "Australia/Sydney",
            Status = BusinessStatus.Active,
            CreatedAtUtc = CreatedAt,
        };

    private static BusinessMembership Membership(
        int businessId,
        string objectId,
        BusinessRole role,
        string? displayName,
        string? email,
        bool isActive = true) =>
        new()
        {
            BusinessId = businessId,
            DirectoryTenantId = Tid,
            ObjectId = objectId,
            DisplayName = displayName,
            Email = email,
            Role = role,
            IsActive = isActive,
            CreatedAtUtc = CreatedAt,
            StatusChangedAtUtc = CreatedAt,
        };
}
