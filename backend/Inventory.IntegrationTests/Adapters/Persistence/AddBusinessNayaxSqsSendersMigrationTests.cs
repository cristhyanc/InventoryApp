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
/// Upgrade test for <c>AddBusinessNayaxSqsSenders</c> (issue #1101): the tenant-owned table that
/// records which AWS IAM user sends a business's Nayax sales to the shared SQS queue, and the
/// append-only trail of the Super Admin's decisions about it.
///
/// <para>Three claims are worth proving. The first is that <strong>the migration changes nothing
/// anybody can see</strong>: it adds two empty tables and touches no existing row. The second is the
/// schema the feature's safety rests on - the per-business unique index, the <em>filtered global</em>
/// unique index over the sender id alone, and the check constraint that keeps an enabled mapping from
/// having nothing to match on. The third is the tenancy decision, read from the schema: both tables
/// carry a business column and are therefore ordinary tenant-owned data rather than platform
/// tables.</para>
///
/// <para>And <c>Down</c> removes both, so a release that has to be rolled back can be.</para>
/// </summary>
public class AddBusinessNayaxSqsSendersMigrationTests
{
    private const string PreviousMigration = "20261105035740_AddSupplierPresetSearchSchedule";

    private const string TargetMigration = "AddBusinessNayaxSqsSenders";

    private const string SenderTable = "BusinessNayaxSqsSenders";

    private const string AuditTable = "BusinessNayaxSqsSenderAudits";

    private static readonly DateTime Created = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Migration_adds_both_tables_empty_and_leaves_the_business_unchanged()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            var tables = await MigrationSchemaProbe.TableNamesAsync(connection);
            Assert.DoesNotContain(SenderTable, tables);
            Assert.DoesNotContain(AuditTable, tables);

            before.Businesses.Add(new Business
            {
                Id = 1,
                Name = "Vending Co",
                TimeZoneId = "Australia/Sydney",
                Status = BusinessStatus.Active,
                CreatedAtUtc = Created,
            });
            await before.SaveChangesAsync();
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            var tables = await MigrationSchemaProbe.TableNamesAsync(connection);
            Assert.Contains(SenderTable, tables);
            Assert.Contains(AuditTable, tables);

            // Both arrive empty: the migration records no sender and no decision, because none was
            // made. A business that existed before it has no mapping, which is exactly right - the
            // queue reader of #1103 must refuse a sender nobody recorded.
            Assert.Equal(0, await MigrationSchemaProbe.RowCountAsync(connection, SenderTable));
            Assert.Equal(0, await MigrationSchemaProbe.RowCountAsync(connection, AuditTable));

            // And the business is exactly as it was.
            var business = await after.Businesses.AsNoTracking().SingleAsync();
            Assert.Equal("Vending Co", business.Name);
            Assert.Equal(BusinessStatus.Active, business.Status);
            Assert.Equal(Created, business.CreatedAtUtc);
        }
    }

    /// <summary>
    /// The sender table's columns, by name: the five the issue's acceptance criteria list, plus the
    /// business key and the two instants. <c>LastReconciliationSucceededAtUtc</c> is deliberately
    /// <em>absent</em> - it is issue #1104's migration to add, and asserting its absence is what
    /// keeps this migration's scope honest.
    /// </summary>
    [Fact]
    public async Task The_sender_table_has_the_declared_columns_and_not_the_next_issues()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await db.Database.MigrateAsync();

        var columns = await MigrationSchemaProbe.ColumnNamesAsync(connection, SenderTable);

        Assert.Equal(
            [
                "Id",
                "BusinessId",
                "AwsSenderId",
                "IsEnabled",
                "LastMessageReceivedAtUtc",
                "Revision",
                "CreatedAtUtc",
                "UpdatedAtUtc",
            ],
            columns);

        Assert.DoesNotContain("LastReconciliationSucceededAtUtc", columns);

        // No AWS credential column of any kind: the sender id is an identifier, and this application
        // stores no access key, secret or session token anywhere.
        Assert.DoesNotContain(columns, column => column.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, column => column.Contains("AccessKey", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The three schema guarantees the feature's safety rests on, read from SQLite's own
    /// <c>CREATE TABLE</c> and index list.
    /// </summary>
    [Fact]
    public async Task The_sender_table_carries_both_unique_indexes_and_the_enabled_check_constraint()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await db.Database.MigrateAsync();

        var indexes = await MigrationSchemaProbe.IndexNamesAsync(connection, SenderTable);
        Assert.Contains("IX_BusinessNayaxSqsSenders_BusinessId", indexes);
        Assert.Contains("IX_BusinessNayaxSqsSenders_AwsSenderId", indexes);

        // The index definitions themselves, because "unique" and "filtered to non-null" are the two
        // halves that make one business per sender id true and a removed id reusable.
        var definitions = await IndexDefinitionsAsync(connection, SenderTable);

        Assert.Contains(
            definitions,
            sql => sql.Contains("CREATE UNIQUE INDEX", StringComparison.Ordinal)
                && sql.Contains(@"""BusinessId""", StringComparison.Ordinal)
                && !sql.Contains("WHERE", StringComparison.Ordinal));

        Assert.Contains(
            definitions,
            sql => sql.Contains("CREATE UNIQUE INDEX", StringComparison.Ordinal)
                && sql.Contains(@"""AwsSenderId""", StringComparison.Ordinal)
                && sql.Contains(@"WHERE ""AwsSenderId"" IS NOT NULL", StringComparison.Ordinal));

        var createTable = await MigrationSchemaProbe.CreateTableSqlAsync(connection, SenderTable);
        Assert.Contains("CK_BusinessNayaxSqsSenders_EnabledNeedsSenderId", createTable, StringComparison.Ordinal);
        Assert.Contains(
            @"""IsEnabled"" = 0 OR ""AwsSenderId"" IS NOT NULL",
            createTable,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The tenancy decision, read from the schema: a sender and a decision about one are facts about
    /// one business, so both tables carry that business's key and are ordinary tenant-owned data -
    /// filtered, stamped and enforced centrally - rather than platform tables about nobody.
    /// </summary>
    [Fact]
    public async Task Both_tables_carry_a_business_key()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await db.Database.MigrateAsync();

        Assert.Contains("BusinessId", await MigrationSchemaProbe.ColumnNamesAsync(connection, SenderTable));
        Assert.Contains("BusinessId", await MigrationSchemaProbe.ColumnNamesAsync(connection, AuditTable));

        // And the audit table records both sides of every decision, which is what makes "what was it
        // before?" answerable without the row before it.
        var audit = await MigrationSchemaProbe.ColumnNamesAsync(connection, AuditTable);
        Assert.Contains("FromAwsSenderId", audit);
        Assert.Contains("ToAwsSenderId", audit);
        Assert.Contains("FromIsEnabled", audit);
        Assert.Contains("ToIsEnabled", audit);
        Assert.Contains("Revision", audit);
        Assert.Contains("ActorDirectoryTenantId", audit);
        Assert.Contains("ActorObjectId", audit);
        Assert.Contains("OccurredAtUtc", audit);
    }

    /// <summary>
    /// <c>Down</c> removes both tables, so this migration is reversible: a release that has to be
    /// rolled back leaves the schema it started from.
    /// </summary>
    [Fact]
    public async Task Down_removes_both_tables()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await db.Database.MigrateAsync();

        Assert.Contains(SenderTable, await MigrationSchemaProbe.TableNamesAsync(connection));

        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);

        var tables = await MigrationSchemaProbe.TableNamesAsync(connection);
        Assert.DoesNotContain(SenderTable, tables);
        Assert.DoesNotContain(AuditTable, tables);
    }

    /// <summary>
    /// SQLite's own <c>CREATE INDEX</c> statements for a table, which is the only place a partial
    /// index's <c>WHERE</c> clause is visible.
    /// </summary>
    private static async Task<List<string>> IndexDefinitionsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT sql FROM sqlite_master WHERE type = 'index' AND tbl_name = $table AND sql IS NOT NULL;";
        command.Parameters.AddWithValue("$table", table);

        var definitions = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            definitions.Add(reader.GetString(0));
        }

        return definitions;
    }
}
