using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddMasterProductSupplierSearchTerms</c> (issue #1138). The migration is purely
/// additive: one nullable <c>SupplierSearchTerms</c> column on <c>MasterProducts</c>, no default, no
/// backfill and no other table touched - so every existing master product starts with no supplier
/// search terms and searches exactly as it did, and no Smith's rule is seeded.
/// </summary>
public class AddMasterProductSupplierSearchTermsMigrationTests
{
    /// <summary>The migration immediately before this one: the state <c>Down</c> returns to.</summary>
    private const string PreviousMigration = "20261107100349_AddNayaxSaleOwnerDecisions";

    private const string TargetMigration = "AddMasterProductSupplierSearchTerms";

    private const string Seed = """
        INSERT INTO MasterProducts (Id, Brand, Name, SizeValue, SizeUnit, NormalisedKey, CreatedAtUtc, UpdatedAtUtc)
            VALUES (1, 'Smiths', 'Original', 45, 3, 'smiths|original|45|g', '2026-11-01 00:00:00', '2026-11-02 00:00:00');
        INSERT INTO MasterProducts (Id, Brand, Name, SizeValue, SizeUnit, NormalisedKey, CreatedAtUtc, UpdatedAtUtc)
            VALUES (2, 'Coca-Cola', 'Classic', 375, 1, 'coca-cola|classic|375|ml', '2026-11-01 00:00:00', '2026-11-01 00:00:00');
        """;

    [Fact]
    public async Task Migration_adds_only_the_nullable_search_terms_column_and_leaves_every_product_alone()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeededUpToAsync(connection, PreviousMigration);

        var columnsBefore = await MigrationSchemaProbe.ColumnNamesAsync(connection, "MasterProducts");
        Assert.DoesNotContain("SupplierSearchTerms", columnsBefore);
        var indexesBefore = await MigrationSchemaProbe.IndexNamesAsync(connection, "MasterProducts");
        var tablesBefore = await MigrationSchemaProbe.TableNamesAsync(connection);

        await MigrateAsync(options, TargetMigration);

        Assert.Equal(
            columnsBefore.Append("SupplierSearchTerms"),
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "MasterProducts"));
        Assert.Equal(indexesBefore, await MigrationSchemaProbe.IndexNamesAsync(connection, "MasterProducts"));
        Assert.Equal(tablesBefore, await MigrationSchemaProbe.TableNamesAsync(connection));
        Assert.Null(await MigrationSchemaProbe.ColumnDefaultAsync(connection, "MasterProducts", "SupplierSearchTerms"));

        // No product gains a configuration: nothing is seeded, the Smith's example included.
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(
            connection, "SELECT COUNT(*) FROM MasterProducts WHERE SupplierSearchTerms IS NOT NULL;")));

        await using var after = TestAppDbContext.Unrestricted(options);
        var products = await after.MasterProducts
            .OrderBy(master => master.Id)
            .Select(master => new { master.Brand, master.Name, master.SizeValue, master.NormalisedKey, master.UpdatedAtUtc })
            .ToListAsync();

        Assert.Equal(2, products.Count);
        Assert.Equal(("Smiths", "Original", 45m, "smiths|original|45|g"),
            (products[0].Brand, products[0].Name, products[0].SizeValue, products[0].NormalisedKey));
        Assert.Equal(new DateTime(2026, 11, 2), products[0].UpdatedAtUtc.Date);
        Assert.Equal("Coca-Cola", products[1].Brand);
    }

    [Fact]
    public async Task Down_drops_only_the_search_terms_column()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeededUpToAsync(connection, PreviousMigration);
        var columnsBefore = await MigrationSchemaProbe.ColumnNamesAsync(connection, "MasterProducts");
        var indexesBefore = await MigrationSchemaProbe.IndexNamesAsync(connection, "MasterProducts");
        await MigrateAsync(options, TargetMigration);

        await using (var write = connection.CreateCommand())
        {
            write.CommandText =
                """UPDATE MasterProducts SET SupplierSearchTerms = '[{"supplierPresetId":null,"priceConnector":4,"searchPhrases":["Smiths Plain","Smiths"],"flavourAliases":["Plain"]}]' WHERE Id = 1;""";
            await write.ExecuteNonQueryAsync();
        }

        await MigrateAsync(options, PreviousMigration);

        Assert.Equal(
            columnsBefore.Order(StringComparer.Ordinal),
            (await MigrationSchemaProbe.ColumnNamesAsync(connection, "MasterProducts")).Order(StringComparer.Ordinal));
        Assert.Equal(indexesBefore, await MigrationSchemaProbe.IndexNamesAsync(connection, "MasterProducts"));
        Assert.Equal(2L, Convert.ToInt64(await ScalarAsync(connection, "SELECT COUNT(*) FROM MasterProducts;")));
        Assert.Equal("Original", await ScalarAsync(connection, "SELECT Name FROM MasterProducts WHERE Id = 1;"));
    }

    private static async Task<DbContextOptions<AppDbContext>> SeededUpToAsync(
        SqliteConnection connection, string target)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await MigrateAsync(options, target);
        await using var command = connection.CreateCommand();
        command.CommandText = Seed;
        await command.ExecuteNonQueryAsync();
        return options;
    }

    private static async Task MigrateAsync(DbContextOptions<AppDbContext> options, string target)
    {
        await using var db = TestAppDbContext.Unrestricted(options);
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
