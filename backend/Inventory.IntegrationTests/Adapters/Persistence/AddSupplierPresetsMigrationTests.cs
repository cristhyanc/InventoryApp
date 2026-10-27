using Inventory.Domain.Suppliers;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddSupplierPresets</c> (issue #848).
///
/// <para>The acceptance criterion this file exists for is the one a reviewer cannot take on trust:
/// the four shipped suggestions are inserted <em>once</em>, by this migration, so a later migration
/// and a restart can never reset an edit or re-enable a preset a Super Admin deliberately disabled.
/// That is a claim about what happens the <em>second</em> time the schema is brought up to date, so
/// it is proved by editing a seeded row and then migrating and starting again.</para>
///
/// <para>Relational SQLite rather than InMemory, because a one-off <c>InsertData</c>, a unique index
/// over a stored normalised name, and "nothing was re-inserted" are relational facts.</para>
/// </summary>
public class AddSupplierPresetsMigrationTests
{
    private const string PreviousMigration = "20261021063528_RemoveProductUnitPrice";
    private const string SupplierPresetsMigration = "AddSupplierPresets";

    private static readonly string[] ShippedNames = ["Big W", "Coles", "Costco", "Woolworths"];

    private static async Task MigrateToAsync(AppDbContext db, string targetMigration) =>
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);

    private static DbContextOptions<AppDbContext> OptionsFor(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

    [Fact]
    public async Task The_migration_creates_the_table_and_inserts_the_four_shipped_suggestions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);

        Assert.DoesNotContain("SupplierPresets", await MigrationSchemaProbe.TableNamesAsync(connection));

        await MigrateToAsync(db, SupplierPresetsMigration);

        Assert.Contains("SupplierPresets", await MigrationSchemaProbe.TableNamesAsync(connection));

        // Read with SQL rather than through the entity: at this point in the history the
        // supplier-master columns issue #931 added do not exist yet, and the model that maps them
        // would ask the database for them.
        var presets = await ReadRowsAsync(connection);

        Assert.Equal(ShippedNames, presets.Select(preset => preset.Name).ToArray());
        Assert.All(presets, preset =>
        {
            Assert.True(preset.IsEnabled);
            // No website is recorded: the issue lists the names and nothing else, and a URL nobody
            // verified is not something a migration may invent.
            Assert.Null(preset.Url);
            Assert.Equal(SupplierPresetPolicy.NormalisedName(preset.Name), preset.NormalisedName);
            Assert.Equal(preset.CreatedAtUtc, preset.UpdatedAtUtc);
        });
    }

    /// <summary>
    /// The acceptance criterion in full: a Super Admin renames and disables a shipped preset, the
    /// schema is brought up to date again and the application starts again, and the edit is still
    /// there. Nothing is re-inserted, nothing is re-enabled, and no extra row appears.
    ///
    /// <para>Bringing the schema fully up to date now also applies issue #931's
    /// <c>AddSupplierPresetMasterFields</c>, which inserts five more records - so the list this test
    /// reads is nine rows, and the four it is about are the four #848 shipped. That migration's own
    /// behaviour over edited rows is <c>AddSupplierPresetMasterFieldsMigrationTests</c>'s subject;
    /// what matters here is unchanged, and is in fact strengthened: an edit made before a <em>later</em>
    /// migration existed still survives it.</para>
    /// </summary>
    [Fact]
    public async Task An_edited_and_disabled_seeded_preset_survives_a_second_migrate_and_restart()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = OptionsFor(connection);

        await using (var migrated = TestAppDbContext.Unrestricted(options))
        {
            await migrated.Database.MigrateAsync();

            var coles = await migrated.SupplierPresets.SingleAsync(preset => preset.Name == "Coles");
            coles.Name = "Coles Supermarkets";
            coles.NormalisedName = SupplierPresetPolicy.NormalisedName(coles.Name);
            coles.Url = "https://www.coles.com.au";
            coles.IsEnabled = false;

            var costco = await migrated.SupplierPresets.SingleAsync(preset => preset.Name == "Costco");
            costco.IsEnabled = false;

            await migrated.SaveChangesAsync();
        }

        // Up to date again - what a later deployment does - and then a fresh context, which is what
        // a restart is as far as this table is concerned.
        await using (var again = TestAppDbContext.Unrestricted(options))
        {
            await again.Database.MigrateAsync();
            Assert.Empty(await again.Database.GetPendingMigrationsAsync());
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        var presets = await verify.SupplierPresets.AsNoTracking().OrderBy(p => p.Name).ToListAsync();

        // The four #848 shipped, one of them renamed, beside the five #931 inserted.
        Assert.Equal(
            [
                "ALDI",
                "Big W",
                "Coles Supermarkets",
                "Costco",
                "JB Metro NSW",
                "JB Metro SA & NT",
                "JB Metro South Coast & ACT",
                "Kmart",
                "Woolworths",
            ],
            presets.Select(preset => preset.Name).ToArray());

        var edited = presets.Single(preset => preset.Name == "Coles Supermarkets");
        Assert.False(edited.IsEnabled);
        Assert.Equal("https://www.coles.com.au", edited.Url);
        Assert.Equal("COLES SUPERMARKETS", edited.NormalisedName);

        // The one that was only disabled is still disabled: a re-seed would have switched it back on
        // without renaming it, which is the failure mode HasData and startup seeding both have.
        Assert.False(presets.Single(preset => preset.Name == "Costco").IsEnabled);
        Assert.True(presets.Single(preset => preset.Name == "Woolworths").IsEnabled);
    }

    /// <summary>
    /// A deleted seeded preset stays deleted too. There is no delete endpoint, but the stronger
    /// claim is the one worth proving: nothing re-creates a row by name on a later migration.
    /// </summary>
    [Fact]
    public async Task A_removed_seeded_preset_is_not_re_inserted_by_a_later_migrate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = OptionsFor(connection);

        await using (var migrated = TestAppDbContext.Unrestricted(options))
        {
            await migrated.Database.MigrateAsync();
            migrated.SupplierPresets.Remove(
                await migrated.SupplierPresets.SingleAsync(preset => preset.Name == "Big W"));
            await migrated.SaveChangesAsync();
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        await verify.Database.MigrateAsync();

        // Everything the migration history inserts, minus the one row that was removed: the eight
        // that remain, and no re-created "Big W".
        Assert.Equal(
            [
                "ALDI",
                "Coles",
                "Costco",
                "JB Metro NSW",
                "JB Metro SA & NT",
                "JB Metro South Coast & ACT",
                "Kmart",
                "Woolworths",
            ],
            await verify.SupplierPresets
                .AsNoTracking()
                .OrderBy(preset => preset.Name)
                .Select(preset => preset.Name)
                .ToArrayAsync());
    }

    /// <summary>
    /// The uniqueness rule is the database's, over the stored normalised name: two rows whose names
    /// differ only in case cannot both exist, whatever a pre-check did or did not see.
    /// </summary>
    [Fact]
    public async Task The_unique_index_refuses_a_second_preset_with_the_same_normalised_name()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await db.Database.MigrateAsync();

        Assert.Contains(
            "IX_SupplierPresets_NormalisedName",
            await MigrationSchemaProbe.IndexNamesAsync(connection, "SupplierPresets"));

        db.SupplierPresets.Add(new Inventory.Infrastructure.Models.SupplierPreset
        {
            Name = "coles",
            NormalisedName = SupplierPresetPolicy.NormalisedName("coles"),
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.Contains(
            "SupplierPresets.NormalisedName",
            error.InnerException!.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The table is about no business, which is the half of the tenancy decision a schema can carry:
    /// there is no <c>BusinessId</c> column for anything to filter or stamp - including after issue
    /// #931 added the five supplier-master columns, which say what a supplier serves and never whose
    /// supplier it is.
    /// </summary>
    [Fact]
    public async Task The_table_carries_no_business_column()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await db.Database.MigrateAsync();

        var columns = await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierPresets");

        Assert.DoesNotContain("BusinessId", columns);
        Assert.Equal(
            [
                "CreatedAtUtc",
                "Id",
                "IsEnabled",
                "Name",
                "NormalisedName",
                "PriceConnector",
                "PriceConnectorKey",
                "PricesIncludeGst",
                "ServedStates",
                "ServesNationwide",
                "UpdatedAtUtc",
                "Url",
            ],
            columns.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Reversing the migration drops the table, which is what "the table did not exist before this"
    /// means - and it is the other half of why the rows are inserted by the migration rather than
    /// treated as recoverable configuration.
    /// </summary>
    [Fact]
    public async Task Reversing_the_migration_drops_the_table()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, SupplierPresetsMigration);
        Assert.Contains("SupplierPresets", await MigrationSchemaProbe.TableNamesAsync(connection));

        await MigrateToAsync(db, PreviousMigration);

        Assert.DoesNotContain("SupplierPresets", await MigrationSchemaProbe.TableNamesAsync(connection));
    }

    /// <summary>
    /// The four shipped rows as this migration left them, read with SQL because the test that uses it
    /// stands on a schema that predates the supplier-master columns of issue #931.
    /// </summary>
    private static async Task<List<SeededRow>> ReadRowsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Name, NormalisedName, Url, IsEnabled, CreatedAtUtc, UpdatedAtUtc "
                + "FROM SupplierPresets ORDER BY Name;";
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<SeededRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new SeededRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return rows;
    }

    private sealed record SeededRow(
        string Name,
        string NormalisedName,
        string? Url,
        bool IsEnabled,
        string CreatedAtUtc,
        string UpdatedAtUtc);
}
