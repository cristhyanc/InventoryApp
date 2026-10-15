using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// What a membership's role and activation may be on their way into the database, and what a
/// membership that never had either declared can reach (issue #679).
///
/// These are the membership counterpart of <see cref="BusinessStatusPersistenceTests"/> and run
/// the same way, on a database built by the real migration history rather than by
/// <c>EnsureCreated</c>: half of what is under test is the <em>schema</em>. What the <c>Role</c>
/// column gives a row when an <c>INSERT</c> states no role is a property of the migrations, and
/// <c>EnsureCreated</c> builds the column from the model, which carries no default at all and so
/// would quietly prove the wrong thing.
///
/// The access half is asserted through the real adapter and the real policy rather than by reading
/// the column, because "what may this member do" is a decision and a security rule has to be
/// stated in the decision.
/// </summary>
public class BusinessMembershipRolePersistenceTests
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "22222222-2222-2222-2222-222222222222";

    private static readonly DateTime CreatedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The stored value no <see cref="BusinessRole"/> is declared at.</summary>
    private const long UndeclaredRole = 0;

    /// <summary><see cref="BusinessRole.Owner"/>'s stored value, written out as the schema holds it.</summary>
    private const long OwnerRole = 40;

    private static async Task<(SqliteConnection Connection, DbContextOptions<AppDbContext> Options)> MigratedAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.MigrateAsync();
        return (connection, options);
    }

    private static ActorIdentity Actor()
    {
        Assert.True(ActorIdentity.TryCreate(Tid, Oid, out var actor));
        return actor!;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private static async Task<BusinessMembershipResolution> ResolveAsync(AppDbContext db) =>
        BusinessMembershipResolutionPolicy.Resolve(
            await new EfBusinessMembershipStore(db).FindMembershipsAsync(Actor(), CancellationToken.None));

    private static async Task<int> AddActiveBusinessAsync(AppDbContext db)
    {
        var business = new Business
        {
            Name = "Vending Co",
            Status = BusinessStatus.Active,
            CreatedAtUtc = CreatedAt,
        };

        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        return business.Id;
    }

    /// <summary>
    /// The defect this issue exists for: an <c>INSERT</c> that never names the column - a raw
    /// statement, or a creation path a future change forgets - must not produce a member who may
    /// manage members, the Nayax integration and data repairs. It produces no access at all.
    /// </summary>
    [Fact]
    public async Task An_insert_that_states_no_role_resolves_to_no_access()
    {
        var (connection, options) = await MigratedAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var businessId = await AddActiveBusinessAsync(db);

            await ExecuteAsync(connection, $"""
                INSERT INTO BusinessMemberships
                    (BusinessId, DirectoryTenantId, ObjectId, IsActive, CreatedAtUtc, StatusChangedAtUtc)
                    VALUES ({businessId}, '{Tid}', '{Oid}', 1, '2026-01-01 00:00:00', '2026-01-01 00:00:00');
                """);

            var stored = (long)(await ScalarAsync(connection, "SELECT Role FROM BusinessMemberships;"))!;
            Assert.Equal(UndeclaredRole, stored);
            Assert.NotEqual(OwnerRole, stored);
            Assert.False(BusinessRoles.IsSupported((BusinessRole)stored));

            var resolution = await ResolveAsync(db);
            Assert.False(resolution.IsResolved);
            Assert.Equal(BusinessAccessDenialReason.RoleUnrecognised, resolution.DenialReason);
        }
    }

    /// <summary>
    /// The ordinary case, and the one every creation path is in: a path that states a role stores
    /// exactly that role, and its member resolves with it. <see cref="BusinessRole.Operator"/>
    /// rather than <see cref="BusinessRole.Owner"/> deliberately, so a role that is still being
    /// carried by the removed default could not make this pass.
    /// </summary>
    [Fact]
    public async Task A_stated_role_is_stored_and_resolves_as_that_role()
    {
        var (connection, options) = await MigratedAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var businessId = await AddActiveBusinessAsync(db);

            db.BusinessMemberships.Add(new BusinessMembership
            {
                BusinessId = businessId,
                DirectoryTenantId = Tid,
                ObjectId = Oid,
                Role = BusinessRole.Operator,
                IsActive = true,
                CreatedAtUtc = CreatedAt,
                StatusChangedAtUtc = CreatedAt,
            });
            await db.SaveChangesAsync();

            Assert.Equal(
                (long)BusinessRole.Operator,
                await ScalarAsync(connection, "SELECT Role FROM BusinessMemberships;"));

            var resolution = await ResolveAsync(db);
            Assert.True(resolution.IsResolved);
            Assert.Equal(BusinessRole.Operator, resolution.ResolvedRole);
        }
    }

    /// <summary>
    /// The neighbouring column, and why issue #679 needed no migration for it: <c>IsActive</c> has
    /// never carried a database default, so a row that states no activation is refused by SQLite
    /// outright rather than arriving active. Its C# half is <c>required</c> on exactly the same
    /// terms as the role's.
    /// </summary>
    [Fact]
    public async Task An_insert_that_states_no_activation_is_refused_by_the_schema()
    {
        var (connection, options) = await MigratedAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var businessId = await AddActiveBusinessAsync(db);

            var refused = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"""
                INSERT INTO BusinessMemberships
                    (BusinessId, DirectoryTenantId, ObjectId, Role, CreatedAtUtc, StatusChangedAtUtc)
                    VALUES ({businessId}, '{Tid}', '{Oid}', {OwnerRole}, '2026-01-01 00:00:00', '2026-01-01 00:00:00');
                """));

            Assert.Contains("NOT NULL", refused.Message, StringComparison.Ordinal);
            Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM BusinessMemberships;"));
        }
    }
}
