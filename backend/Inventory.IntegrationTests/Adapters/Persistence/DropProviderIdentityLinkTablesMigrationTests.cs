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
/// Upgrade test for <c>DropProviderIdentityLinkTables</c> (issue #760, parent #329 child A1c): the
/// migration that removes the <c>ProductExternalIds</c>/<c>CategoryExternalIds</c> link tables of
/// issue #732, which issue #759 copied onto the local row and stopped reading.
///
/// <para><strong>The drop is destructive, so what this test proves is the decision, not the
/// <c>DROP</c>.</strong> Every remaining link row is classified first - matched, orphaned or
/// unexplained - and the migration drops the tables only when nothing is unexplained. Each case
/// below is one of those classes and the decision it must produce, because a dropped row is not
/// recoverable and a link that disagrees with the column every lookup now trusts is the one thing
/// that must stop the drop rather than disappear with it.</para>
///
/// <para><strong>The prerequisite is checked too.</strong> #759's migration must be in the migration
/// history and the post-copy verification it records must be there; without that evidence the
/// migration refuses, because "the copy happened" is the whole reason dropping these rows is safe.</para>
///
/// <para>The orphan fixture is seeded with <c>PRAGMA foreign_keys = OFF</c>, because the link tables'
/// cascading foreign keys make an orphan impossible while they are enforced - which is exactly why
/// the expected orphan count is zero and why the migration counts them instead of assuming.</para>
/// </summary>
public class DropProviderIdentityLinkTablesMigrationTests
{
    /// <summary>The migration immediately before this one: the state <c>Down</c> returns to.</summary>
    private const string PreviousMigration = "20261103090000_AddSupplierPresetSearchPrices";

    private const string TargetMigration = "DropProviderIdentityLinkTables";

    /// <summary>The table the migration records its classification in, for a human to read.</summary>
    private const string ClassificationTable = "ProviderLinkTableDropClassifications";

    private const int BusinessA = 1;
    private const int BusinessB = 2;

    /// <summary>
    /// The link rows two businesses hold after #759's copy, each matching its row's column. Business
    /// B deliberately holds the same external ids as business A, so a classification that compared
    /// ids without their business would show up as a mismatch.
    /// </summary>
    private const string MatchingLinks = """
        INSERT INTO ProductExternalIds (BusinessId, Provider, ExternalId, ProductId)
            VALUES (1, 1, '100', 5001),
                   (2, 1, '100', 200);
        INSERT INTO CategoryExternalIds (BusinessId, Provider, ExternalId, CategoryId)
            VALUES (1, 1, '10', 55),
                   (2, 1, '10', 11);
        """;

    [Fact]
    public async Task Migration_drops_both_link_tables_when_every_remaining_link_matches_its_column()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeededUpToAsync(connection, PreviousMigration);

        await ExecuteAsync(connection, MatchingLinks);
        await MigrateAsync(options, TargetMigration);

        // The tables, their indexes and their foreign keys are gone with them.
        var tables = await MigrationSchemaProbe.TableNamesAsync(connection);
        Assert.DoesNotContain("ProductExternalIds", tables);
        Assert.DoesNotContain("CategoryExternalIds", tables);
        Assert.DoesNotContain(
            "ProductExternalIds",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "Products"));

        // The classification the migration decided from, recorded for a human: every remaining link
        // matched its row's column, and no row was orphaned or unexplained.
        Assert.Equal(
            [
                ("CategoryExternalIds", "Matched", 2L, null),
                ("CategoryExternalIds", "Orphaned", 0L, null),
                ("CategoryExternalIds", "Unexplained: column differs", 0L, null),
                ("CategoryExternalIds", "Unexplained: external id on another row", 0L, null),
                ("ProductExternalIds", "Matched", 2L, null),
                ("ProductExternalIds", "Orphaned", 0L, null),
                ("ProductExternalIds", "Unexplained: column differs", 0L, null),
                ("ProductExternalIds", "Unexplained: external id on another row", 0L, null),
            ],
            await ClassificationsAsync(connection));

        // The external id columns the copy wrote are untouched, in both businesses.
        await using var after = TestAppDbContext.Unrestricted(options);
        Assert.Equal(
            [(1, 5001L, "100"), (1, 5002L, null), (2, 200L, "100")],
            await after.Products
                .OrderBy(product => product.BusinessId).ThenBy(product => product.Id)
                .Select(product => ValueTuple.Create(product.BusinessId, product.Id, product.ExternalProductId))
                .ToListAsync());
        Assert.Equal(
            [(1, 55L, "10"), (1, 56L, null), (2, 11L, "10")],
            await after.Categories
                .OrderBy(category => category.BusinessId).ThenBy(category => category.Id)
                .Select(category => ValueTuple.Create(category.BusinessId, category.Id, category.ExternalCategoryId))
                .ToListAsync());

        // The business keys stay: the one on Products is the principal of NayaxSales.ProductId's
        // foreign key since issue #758, so only the link tables went away.
        var products = await MigrationSchemaProbe.CreateTableSqlAsync(connection, "Products");
        Assert.Contains("AK_Products_BusinessId_Id", products);
        Assert.Contains(
            "AK_Categories_BusinessId_Id",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "Categories"));
        Assert.Contains(
            "FK_NayaxSales_Products_BusinessId_ProductId",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "NayaxSales"));

        // And the sale still names its product, with nothing dangling anywhere in the database.
        Assert.Equal("5001", await ScalarTextAsync(connection, "SELECT ProductId FROM NayaxSales;"));
        Assert.Empty(await ForeignKeyViolationsAsync(connection));
    }

    /// <summary>
    /// A link whose local row no longer exists cannot be checked against a column, and the cascade on
    /// the link tables' foreign keys should make it impossible. It is dropped with the table and
    /// counted, so a database that had one says so afterwards instead of silently losing the row.
    /// </summary>
    [Fact]
    public async Task Orphaned_links_are_dropped_with_the_tables_and_counted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeededUpToAsync(connection, PreviousMigration);

        await ExecuteAsync(connection, MatchingLinks);
        await ExecuteAsync(
            connection,
            """
            PRAGMA foreign_keys = OFF;
            INSERT INTO ProductExternalIds (BusinessId, Provider, ExternalId, ProductId)
                VALUES (1, 1, '777', 99999);
            INSERT INTO CategoryExternalIds (BusinessId, Provider, ExternalId, CategoryId)
                VALUES (1, 1, '77', 88888);
            """);

        await MigrateAsync(options, TargetMigration);

        var tables = await MigrationSchemaProbe.TableNamesAsync(connection);
        Assert.DoesNotContain("ProductExternalIds", tables);
        Assert.DoesNotContain("CategoryExternalIds", tables);

        // The count, and the orphan's own ids - the link row and the local row it named, and nothing
        // about the business.
        Assert.Equal(
            [
                ("CategoryExternalIds", "Matched", 2L, null),
                ("CategoryExternalIds", "Orphaned", 1L, "3/88888"),
                ("CategoryExternalIds", "Unexplained: column differs", 0L, null),
                ("CategoryExternalIds", "Unexplained: external id on another row", 0L, null),
                ("ProductExternalIds", "Matched", 2L, null),
                ("ProductExternalIds", "Orphaned", 1L, "3/99999"),
                ("ProductExternalIds", "Unexplained: column differs", 0L, null),
                ("ProductExternalIds", "Unexplained: external id on another row", 0L, null),
            ],
            await ClassificationsAsync(connection));
    }

    /// <summary>
    /// The case the whole classification exists for: the local row is still there and its column does
    /// not say what the link says. One of the two is wrong, the migration cannot tell which, and the
    /// column is what every lookup now reads - so it refuses and drops nothing.
    /// </summary>
    [Fact]
    public async Task A_column_that_no_longer_matches_its_link_fails_the_migration()
    {
        var classification = await AssertRefusedAsync(
            "UPDATE Products SET ExternalProductId = '999' WHERE Id = 5001;",
            "unexplained");

        Assert.Equal(
            ("ProductExternalIds", "Unexplained: column differs", 1L, "1/5001"),
            Assert.Single(
                classification,
                row => row.Classification == "Unexplained: column differs" && row.RowCount > 0));
    }

    /// <summary>
    /// The same disagreement, in the shape that would do the most damage if it were dropped: the
    /// link's external id is now on a different product of the same business, so dropping the link
    /// would quietly bless a catalogue where a provider id moved between rows.
    /// </summary>
    [Fact]
    public async Task An_external_id_now_held_by_another_row_fails_the_migration()
    {
        var classification = await AssertRefusedAsync(
            """
            UPDATE Products SET ExternalProductId = NULL WHERE Id = 5001;
            UPDATE Products SET ExternalProductId = '100' WHERE Id = 5002;
            """,
            "unexplained");

        Assert.Equal(
            ("ProductExternalIds", "Unexplained: external id on another row", 1L, "1/5001"),
            Assert.Single(
                classification,
                row => row.Classification == "Unexplained: external id on another row" && row.RowCount > 0));
    }

    /// <summary>
    /// The prerequisite: #759 records that it verified the copy, and this migration confirms that
    /// record before it drops anything. Without it there is no evidence the external ids ever reached
    /// the columns, and dropping the only other copy of them would be unrecoverable.
    /// </summary>
    [Theory]
    [InlineData("DELETE FROM ProviderIdentityColumnVerifications;")]
    [InlineData("DELETE FROM ProviderIdentityColumnVerifications WHERE LinkTable = 'CategoryExternalIds';")]
    [InlineData("DROP TABLE ProviderIdentityColumnVerifications;")]
    public async Task A_database_without_the_recorded_759_verification_fails_the_migration(string damage)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeededUpToAsync(connection, PreviousMigration);

        await ExecuteAsync(connection, MatchingLinks);
        await ExecuteAsync(connection, damage);

        var refused = await Assert.ThrowsAsync<SqliteException>(() => MigrateAsync(options, TargetMigration));
        Assert.Contains("issue #759", refused.Message, StringComparison.Ordinal);

        // Nothing was dropped, and every link row is still there.
        var tables = await MigrationSchemaProbe.TableNamesAsync(connection);
        Assert.Contains("ProductExternalIds", tables);
        Assert.Contains("CategoryExternalIds", tables);
        Assert.Equal(2L, await MigrationSchemaProbe.RowCountAsync(connection, "ProductExternalIds"));
        Assert.Equal(2L, await MigrationSchemaProbe.RowCountAsync(connection, "CategoryExternalIds"));
    }

    /// <summary>
    /// <c>Down</c> restores the schema the previous release's code compiles against, and nothing
    /// more: both tables come back with their indexes and their composite foreign keys, and empty.
    /// The dropped rows are not recoverable, and the migration invents nothing in their place.
    /// </summary>
    [Fact]
    public async Task Down_recreates_both_link_tables_empty()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeededUpToAsync(connection, PreviousMigration);

        await ExecuteAsync(connection, MatchingLinks);
        await MigrateAsync(options, TargetMigration);

        await MigrateAsync(options, PreviousMigration);

        var tables = await MigrationSchemaProbe.TableNamesAsync(connection);
        Assert.Contains("ProductExternalIds", tables);
        Assert.Contains("CategoryExternalIds", tables);
        Assert.DoesNotContain(ClassificationTable, tables);

        Assert.Equal(0L, await MigrationSchemaProbe.RowCountAsync(connection, "ProductExternalIds"));
        Assert.Equal(0L, await MigrationSchemaProbe.RowCountAsync(connection, "CategoryExternalIds"));

        Assert.Contains(
            "FK_ProductExternalIds_Products_BusinessId_ProductId",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "ProductExternalIds"));
        Assert.Contains(
            "FK_CategoryExternalIds_Categories_BusinessId_CategoryId",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "CategoryExternalIds"));
        Assert.Equal(
            [
                "IX_ProductExternalIds_BusinessId",
                "IX_ProductExternalIds_BusinessId_ProductId",
                "IX_ProductExternalIds_BusinessId_Provider_ExternalId",
                "IX_ProductExternalIds_BusinessId_Provider_ProductId",
            ],
            (await MigrationSchemaProbe.IndexNamesAsync(connection, "ProductExternalIds"))
                .Where(name => name.StartsWith("IX_", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal));
        Assert.Empty(await ForeignKeyViolationsAsync(connection));
    }

    /// <summary>
    /// Seeds the matching links plus <paramref name="damage"/>, runs the upgrade, and asserts it
    /// failed for <paramref name="expectedReason"/> having dropped nothing at all. Returns the
    /// classification the migration recorded, which survives the refusal precisely so a human can
    /// read which rows stopped it.
    /// </summary>
    private static async Task<List<(string LinkTable, string Classification, long RowCount, string? RowIds)>>
        AssertRefusedAsync(string damage, string expectedReason)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeededUpToAsync(connection, PreviousMigration);

        await ExecuteAsync(connection, MatchingLinks);
        await ExecuteAsync(connection, damage);

        var refused = await Assert.ThrowsAsync<SqliteException>(() => MigrateAsync(options, TargetMigration));
        Assert.Contains(expectedReason, refused.Message, StringComparison.Ordinal);

        // Both tables and every row they held are still there.
        var tables = await MigrationSchemaProbe.TableNamesAsync(connection);
        Assert.Contains("ProductExternalIds", tables);
        Assert.Contains("CategoryExternalIds", tables);
        Assert.Equal(2L, await MigrationSchemaProbe.RowCountAsync(connection, "ProductExternalIds"));
        Assert.Equal(2L, await MigrationSchemaProbe.RowCountAsync(connection, "CategoryExternalIds"));

        // And the migration is not recorded as applied, so the next startup runs it again.
        await using var db = TestAppDbContext.Unrestricted(options);
        Assert.Contains(
            TargetMigration,
            (await db.Database.GetPendingMigrationsAsync()).Select(id => id.Split('_')[^1]));

        return await ClassificationsAsync(connection);
    }

    /// <summary>
    /// Opens <paramref name="connection"/>, migrates to <paramref name="target"/> and seeds two
    /// businesses, their catalogues with the external ids #759 copied onto them, and one stored sale
    /// that names a product through the business key the link tables shared.
    /// </summary>
    private static async Task<DbContextOptions<AppDbContext>> SeededUpToAsync(
        SqliteConnection connection, string target)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await MigrateAsync(options, target);

        await using (var setup = TestAppDbContext.Unrestricted(options))
        {
            setup.Businesses.AddRange(
                new Business
                {
                    Id = BusinessA,
                    Name = "Vending A",
                    Status = BusinessStatus.Active,
                    CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                },
                new Business
                {
                    Id = BusinessB,
                    Name = "Vending B",
                    Status = BusinessStatus.Active,
                    CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                });
            await setup.SaveChangesAsync();
        }

        await SaveAsAsync(options, BusinessA, db =>
        {
            db.Categories.Add(new Category { Id = 55, Name = "Snacks", ExternalCategoryId = "10" });
            db.Categories.Add(new Category { Id = 56, Name = "Local only" });
            db.Products.Add(new Product
            {
                Id = 5001,
                Name = "Chips",
                CategoryId = 55,
                ExternalProductId = "100",
            });
            db.Products.Add(new Product { Id = 5002, Name = "Locally created", CategoryId = 56 });
        });

        await SaveAsAsync(options, BusinessB, db =>
        {
            db.Categories.Add(new Category { Id = 11, Name = "Drinks", ExternalCategoryId = "10" });
            db.Products.Add(new Product
            {
                Id = 200,
                Name = "Cola",
                CategoryId = 11,
                ExternalProductId = "100",
            });
        });

        await ExecuteAsync(
            connection,
            """
            INSERT INTO NayaxSales (BusinessId, TransactionID, MachineID, SettlementValue,
                                    MachineAuthorizationTime, CostSource, CostingStatus, Provider,
                                    ProviderProductIdIsRaw, ProductId)
                VALUES (1, 900, 7, 3.50, '2026-01-03 00:00:00', 0, 0, 1, 1, 5001);
            """);

        return options;
    }

    private static async Task SaveAsAsync(
        DbContextOptions<AppDbContext> options, int businessId, Action<AppDbContext> write)
    {
        await using var db = TestAppDbContext.For(options, businessId);
        write(db);
        await db.SaveChangesAsync();
    }

    private static async Task MigrateAsync(DbContextOptions<AppDbContext> options, string target)
    {
        await using var db = TestAppDbContext.Unrestricted(options);
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ScalarTextAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    /// <summary>
    /// Everything the migration recorded about the rows it classified, ordered as the assertions
    /// read it: by link table, then by classification.
    /// </summary>
    private static async Task<List<(string LinkTable, string Classification, long RowCount, string? RowIds)>>
        ClassificationsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT "LinkTable", "Classification", "RowCount", "RowIds" FROM "{ClassificationTable}"
            ORDER BY "LinkTable", "Classification";
            """;
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<(string, string, long, string?)>();
        while (await reader.ReadAsync())
        {
            rows.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                await reader.IsDBNullAsync(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    /// <summary>Every row <c>PRAGMA foreign_key_check</c> reports, as "table:rowid" labels.</summary>
    private static async Task<List<string>> ForeignKeyViolationsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync();

        var violations = new List<string>();
        while (await reader.ReadAsync())
        {
            violations.Add($"{reader.GetString(0)}:{reader.GetValue(1)}");
        }

        return violations;
    }
}
