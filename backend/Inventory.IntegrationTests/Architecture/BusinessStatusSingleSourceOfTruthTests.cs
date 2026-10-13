using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Architecture;

/// <summary>
/// The acceptance criterion of issue #540 that is a standing rule rather than one behaviour: no
/// code path reads a business active flag to decide access, because there is no such flag.
///
/// The strongest available proof is structural, so that is what this test asserts: the property is
/// gone from the entity, the column is gone from the schema the migrations produce, and the one
/// fact carrier the access decision reads carries the lifecycle status instead. A code path that
/// tried to read the old flag would not compile and could not query, which is a stronger guarantee
/// than any search over today's source could give - and it stays true for code nobody has written
/// yet.
///
/// Re-adding the flag as a convenience mirror is the specific thing this prevents: two columns
/// describing whether a business may be reached is one too many, and the next write path to update
/// only one of them would leave the access decision reading a value nobody maintained.
/// </summary>
public class BusinessStatusSingleSourceOfTruthTests
{
    private static async Task<(SqliteConnection Connection, AppDbContext Db)> MigratedDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = TestAppDbContext.Unrestricted(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        return (connection, db);
    }

    /// <summary>
    /// The entity carries the status and no active flag. Nothing in any layer can read
    /// <c>Business.IsActive</c>, in a query or in memory, because the member does not exist.
    /// </summary>
    [Fact]
    public void The_Business_entity_carries_a_lifecycle_status_and_no_active_flag()
    {
        Assert.Null(typeof(Business).GetProperty("IsActive"));

        var status = typeof(Business).GetProperty(nameof(Business.Status));
        Assert.NotNull(status);
        Assert.Equal(typeof(BusinessStatus), status!.PropertyType);
    }

    /// <summary>
    /// The membership's own <c>IsActive</c> is deliberately untouched: it says whether that
    /// person's approval is current, which is a different fact from the business's lifecycle and
    /// is still read by the resolution rule.
    /// </summary>
    [Fact]
    public void The_membership_keeps_its_own_active_flag()
    {
        Assert.NotNull(typeof(BusinessMembership).GetProperty(nameof(BusinessMembership.IsActive)));
    }

    /// <summary>
    /// The one thing the access decision is handed about the owning business is its status. A
    /// business active boolean on this record is what the policy used to read, and its absence is
    /// what makes "resolution reads the status" structural rather than conventional.
    /// </summary>
    [Fact]
    public void The_membership_fact_the_resolution_reads_carries_the_status_and_no_business_flag()
    {
        Assert.Null(typeof(ActorBusinessMembership).GetProperty("BusinessIsActive"));

        var status = typeof(ActorBusinessMembership).GetProperty(nameof(ActorBusinessMembership.BusinessStatus));
        Assert.NotNull(status);
        Assert.Equal(typeof(BusinessStatus), status!.PropertyType);
    }

    /// <summary>
    /// And the schema agrees, on a database built by the real migration history: no query - EF,
    /// hand-written SQL, or a future report - can read a business active column, because the
    /// column is not there.
    /// </summary>
    [Fact]
    public async Task The_migrated_schema_holds_a_status_column_and_no_active_column()
    {
        var (connection, db) = await MigratedDatabaseAsync();
        await using (connection)
        {
            await using (db)
            {
                var columns = await Adapters.Persistence.MigrationSchemaProbe
                    .ColumnNamesAsync(connection, "Businesses");

                Assert.Contains("Status", columns);
                Assert.DoesNotContain("IsActive", columns);
            }
        }
    }

    /// <summary>
    /// The mapped model reaches the same columns, so a mirror cannot arrive as an unmapped shadow
    /// property either.
    /// </summary>
    [Fact]
    public async Task The_mapped_model_declares_no_business_active_property()
    {
        var (connection, db) = await MigratedDatabaseAsync();
        await using (connection)
        {
            await using (db)
            {
                var properties = db.Model
                    .FindEntityType(typeof(Business))!
                    .GetProperties()
                    .Select(property => property.Name)
                    .ToList();

                Assert.Contains(nameof(Business.Status), properties);
                Assert.DoesNotContain("IsActive", properties);
            }
        }
    }
}
