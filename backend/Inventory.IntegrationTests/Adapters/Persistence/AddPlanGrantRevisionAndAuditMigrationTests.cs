using Inventory.Domain.Subscriptions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddPlanGrantRevisionAndAudit</c> (issue #982): the revision column the Super
/// Admin surface's concurrency check compares, and the append-only audit table its decisions are
/// recorded in.
///
/// <para>Two claims are worth proving. The first is that <strong>the migration changes nothing
/// anybody can see</strong>: a grant that already existed - the grandfathered one of issue #966 -
/// keeps its plan, its source, its start, its end and its reason, and simply gains revision
/// <c>0</c>, which is what makes "this surface has never written this row" the truthful default. The
/// second is the tenancy decision: the new table carries a business column and is therefore ordinary
/// tenant-owned data rather than a platform table.</para>
/// </summary>
public class AddPlanGrantRevisionAndAuditMigrationTests
{
    private const string PreviousMigration = "20261029032958_AddBusinessSubscriptions";

    private const string TargetMigration = "AddPlanGrantRevisionAndAudit";

    private static readonly DateTime Created = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Cutoff = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The grandfathered Pro grant issue #966's command writes - plan <c>30</c>, source <c>10</c> -
    /// seeded on the pre-migration schema with SQL, because the EF model this test compiles against
    /// already has the revision column that schema does not.
    /// </summary>
    private const string GrandfatheredGrantSeed = """
        INSERT INTO BusinessPlanGrants
            (BusinessId, Plan, Source, StartsAtUtc, EndsAtUtc, Reason, GrantedAtUtc)
            VALUES (1, 30, 10, '2026-10-01 00:00:00', NULL, NULL, '2026-10-02 06:00:00');
        """;

    [Fact]
    public async Task Migration_adds_the_audit_table_and_leaves_the_grant_that_existed_unchanged()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            Assert.DoesNotContain(
                "BusinessPlanGrantAudits",
                await MigrationSchemaProbe.TableNamesAsync(connection));
            Assert.DoesNotContain(
                "Revision",
                await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessPlanGrants"));

            before.Businesses.Add(new Business
            {
                Id = 1,
                Name = "Vending Co",
                TimeZoneId = "Australia/Sydney",
                Status = BusinessStatus.Active,
                CreatedAtUtc = Created,
            });
            await before.SaveChangesAsync();

            // Written through raw SQL rather than the model, because the model this test runs against
            // already has the revision column the previous schema does not.
            await ExecuteAsync(connection, GrandfatheredGrantSeed);
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            Assert.Contains(
                "BusinessPlanGrantAudits",
                await MigrationSchemaProbe.TableNamesAsync(connection));

            // The trail arrives empty: the migration records no decision, because none was made.
            Assert.Equal(0, await MigrationSchemaProbe.RowCountAsync(connection, "BusinessPlanGrantAudits"));

            // And the grandfathered grant is exactly as it was, with revision 0 - never 1, which
            // would claim this surface had already written it.
            var grant = await after.BusinessPlanGrants.AsNoTracking().SingleAsync();
            Assert.Equal(1, grant.BusinessId);
            Assert.Equal(SubscriptionPlan.Pro, grant.Plan);
            Assert.Equal(ManualPlanGrantSource.Grandfathered, grant.Source);
            Assert.Equal(Cutoff, grant.StartsAtUtc);
            Assert.Null(grant.EndsAtUtc);
            Assert.Null(grant.Reason);
            Assert.Equal(0, grant.Revision);
        }
    }

    /// <summary>
    /// The tenancy decision, read from the schema: a decision is a fact about one business, so the
    /// audit table carries that business's key and is ordinary tenant-owned data - filtered, stamped
    /// and enforced centrally - rather than a platform table about nobody.
    /// </summary>
    [Fact]
    public async Task The_audit_table_carries_a_business_key()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await db.Database.MigrateAsync();

        Assert.Contains(
            "BusinessId",
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessPlanGrantAudits"));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
