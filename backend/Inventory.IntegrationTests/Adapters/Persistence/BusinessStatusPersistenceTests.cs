using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// What a business's lifecycle status may be on its way into the database, and what a business that
/// never had one declared can reach (issue #584).
///
/// These run on a database built by the real migration history rather than by <c>EnsureCreated</c>,
/// because half of what is under test is the <em>schema</em>: what the <c>Status</c> column gives a
/// row when an <c>INSERT</c> states no value is a property of the migrations, and
/// <c>EnsureCreated</c> builds the column from the model, which carries no default at all and so
/// would quietly prove the wrong thing.
///
/// The access half is asserted through the real adapter and the real policy rather than by reading
/// the column: "is this business reachable" is a decision, and the decision is what a security rule
/// has to be stated in.
/// </summary>
public class BusinessStatusPersistenceTests
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "22222222-2222-2222-2222-222222222222";

    private static readonly DateTime CreatedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The stored value no <see cref="BusinessStatus"/> is declared at.</summary>
    private const int UndeclaredStatus = 0;

    private static async Task<(SqliteConnection Connection, DbContextOptions<AppDbContext> Options)> MigratedAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.MigrateAsync();
        return (connection, options);
    }

    private static Business NewBusiness(BusinessStatus status) =>
        new()
        {
            Name = "Vending Co",
            Status = status,
            CreatedAtUtc = CreatedAt,
        };

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

    private static async Task AddMembershipAsync(AppDbContext db, int businessId)
    {
        db.BusinessMemberships.Add(new BusinessMembership
        {
            BusinessId = businessId,
            DirectoryTenantId = Tid,
            ObjectId = Oid,
            Role = BusinessRole.Owner,
            IsActive = true,
            CreatedAtUtc = CreatedAt,
            StatusChangedAtUtc = CreatedAt,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The authorized legacy case, and the one the application's existing business is in: a path
    /// that means "already trading" says so, and its member reaches its data.
    /// </summary>
    [Fact]
    public async Task An_explicitly_active_business_is_stored_active_and_resolves()
    {
        var (connection, options) = await MigratedAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var business = NewBusiness(BusinessStatus.Active);
            db.Businesses.Add(business);
            await db.SaveChangesAsync();
            await AddMembershipAsync(db, business.Id);

            Assert.Equal(BusinessStatus.Active, (await db.Businesses.SingleAsync()).Status);

            var resolution = await ResolveAsync(db);
            Assert.True(resolution.IsResolved);
            Assert.Equal(BusinessId.From(business.Id), resolution.ResolvedBusinessId);
        }
    }

    /// <summary>
    /// The case the self-service application (#541) creates: a business that has applied is stored
    /// as <c>Pending</c>, stays <c>Pending</c>, and reaches nothing at all - its member holds a real
    /// membership, which is what makes the application theirs, and it still grants no data.
    /// </summary>
    [Fact]
    public async Task An_explicitly_pending_business_is_stored_pending_and_reaches_nothing()
    {
        var (connection, options) = await MigratedAsync();
        await using (connection)
        {
            await using var db = TestAppDbContext.Unrestricted(options);
            var business = NewBusiness(BusinessStatus.Pending);
            db.Businesses.Add(business);
            await db.SaveChangesAsync();
            await AddMembershipAsync(db, business.Id);

            Assert.Equal(BusinessStatus.Pending, (await db.Businesses.SingleAsync()).Status);

            var resolution = await ResolveAsync(db);
            Assert.False(resolution.IsResolved);
            Assert.Equal(BusinessAccessDenialReason.BusinessInactive, resolution.DenialReason);
        }
    }

    /// <summary>
    /// A status nobody declared is refused on the way in, and nothing is stored. <c>0</c> is the
    /// value a write path that never assigned the property hands over - <c>required</c> makes that a
    /// compile error, and this is what covers the cast, the stale entity and anything else that
    /// reaches <c>SaveChanges</c> with a value the vocabulary does not hold.
    /// </summary>
    [Theory]
    [InlineData(UndeclaredStatus)]
    [InlineData(15)]
    [InlineData(99)]
    [InlineData(-20)]
    public async Task A_status_that_is_not_declared_is_refused_and_nothing_is_stored(int storedStatus)
    {
        var (connection, options) = await MigratedAsync();
        await using (connection)
        {
            await using (var db = TestAppDbContext.Unrestricted(options))
            {
                db.Businesses.Add(NewBusiness((BusinessStatus)storedStatus));

                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
                Assert.Contains(BusinessStatuses.UnsupportedMessage, error.Message, StringComparison.Ordinal);
            }

            await using var verify = TestAppDbContext.Unrestricted(options);
            Assert.Empty(await verify.Businesses.ToListAsync());
        }
    }

    /// <summary>
    /// And on an update, not only on insert: an existing business cannot be edited into a status
    /// nobody declared, and the stored status is left exactly as it was.
    /// </summary>
    [Fact]
    public async Task An_existing_business_cannot_be_updated_to_an_undeclared_status()
    {
        var (connection, options) = await MigratedAsync();
        await using (connection)
        {
            await using (var db = TestAppDbContext.Unrestricted(options))
            {
                db.Businesses.Add(NewBusiness(BusinessStatus.Active));
                await db.SaveChangesAsync();
            }

            await using (var db = TestAppDbContext.Unrestricted(options))
            {
                (await db.Businesses.SingleAsync()).Status = (BusinessStatus)UndeclaredStatus;

                await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            }

            await using var verify = TestAppDbContext.Unrestricted(options);
            Assert.Equal(BusinessStatus.Active, (await verify.Businesses.SingleAsync()).Status);
        }
    }

    /// <summary>
    /// The half the schema owns. An <c>INSERT</c> that states no status - something outside the
    /// application entirely, a maintenance script or a future repair command - no longer produces a
    /// trading business: the column's default is a value no status is declared at, so the row is
    /// created unreachable and its member, whose membership is otherwise perfectly valid, is denied.
    /// </summary>
    [Fact]
    public async Task An_insert_that_omits_the_status_column_creates_a_business_that_reaches_nothing()
    {
        var (connection, options) = await MigratedAsync();
        await using (connection)
        {
            await ExecuteAsync(connection, """
                INSERT INTO Businesses (Id, Name, CreatedAtUtc, TimeZoneId)
                    VALUES (1, 'Vending Co', '2026-01-01 00:00:00', 'Australia/Sydney');
                INSERT INTO BusinessMemberships
                    (BusinessId, DirectoryTenantId, ObjectId, Role, IsActive, CreatedAtUtc, StatusChangedAtUtc)
                    VALUES (1, '11111111-1111-1111-1111-111111111111',
                        '22222222-2222-2222-2222-222222222222', 40, 1,
                        '2026-01-01 00:00:00', '2026-01-01 00:00:00');
                """);

            Assert.Equal(
                (long)UndeclaredStatus,
                await ScalarAsync(connection, "SELECT Status FROM Businesses WHERE Id = 1;"));

            await using var db = TestAppDbContext.Unrestricted(options);
            var resolution = await ResolveAsync(db);

            Assert.False(resolution.IsResolved);
            Assert.Equal(BusinessAccessDenialReason.BusinessInactive, resolution.DenialReason);
        }
    }
}
