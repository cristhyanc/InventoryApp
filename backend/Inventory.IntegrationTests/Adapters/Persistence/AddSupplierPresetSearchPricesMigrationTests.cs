using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddSupplierPresetSearchPrices</c> (issue #1077): one additive
/// <c>SearchPrices</c> column on <c>SupplierPresets</c>, backfilled from <c>IsEnabled</c> so the
/// collector searches exactly the same suppliers after the upgrade as before it.
///
/// <para>The claim that cannot be taken on trust is the backfill, over a database that is
/// <em>not</em> in its shipped state. Production applies migrations at startup (AGENTS.md § Database
/// and migrations), so this one runs over presets a Super Admin may have disabled or added, and each
/// of them must come out searched exactly as often as it was. Every test here therefore edits the
/// database first.</para>
///
/// <para>The rows are read with SQL rather than through the entity on purpose, exactly as
/// <c>AddSupplierPresetMasterFieldsMigrationTests</c> does: what is under test is the schema and the
/// data the migration wrote, and a test that read them through the model would pass on a model that
/// had drifted from the columns the migration created - and could not read the table at all at the
/// point where the column does not exist yet.</para>
/// </summary>
public class AddSupplierPresetSearchPricesMigrationTests
{
    private const string PreviousMigration = "20261101090000_SetWeeklySupplierPriceFreshness";
    private const string TargetMigration = "20261103090000_AddSupplierPresetSearchPrices";

    private const string NewColumn = "SearchPrices";

    /// <summary>
    /// When the presets a Super Admin created themselves were created: the clock, to the tick, which
    /// is what the create path records - so these rows are visibly not migration-seeded ones.
    /// </summary>
    private const string PreExistingInstant = "2026-11-02 09:15:42.1234567";

    private static Task MigrateToAsync(AppDbContext db, string targetMigration) =>
        db.GetService<IMigrator>().MigrateAsync(targetMigration);

    private static DbContextOptions<AppDbContext> OptionsFor(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

    #region The column

    /// <summary>
    /// One column arrives and nothing else changes about the table: no column is removed or renamed,
    /// no index is added or dropped, and - because this is a platform table - no business key appears
    /// (docs/architecture.md § Tenant ownership).
    /// </summary>
    [Fact]
    public async Task The_migration_adds_one_column_and_changes_nothing_else_about_the_table()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);

        var before = await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierPresets");
        var indexesBefore = await MigrationSchemaProbe.IndexNamesAsync(connection, "SupplierPresets");
        Assert.DoesNotContain(NewColumn, before);

        await MigrateToAsync(db, TargetMigration);

        var after = await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierPresets");
        Assert.Contains(NewColumn, after);
        Assert.Equal(
            before.Append(NewColumn).Order(StringComparer.Ordinal),
            after.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("BusinessId", after);
        Assert.Equal(indexesBefore, await MigrationSchemaProbe.IndexNamesAsync(connection, "SupplierPresets"));

        // The table's existing check constraint names a different column and survives: the upgrade is
        // an ALTER TABLE ADD COLUMN rather than a rebuild.
        Assert.Contains(
            "CK_SupplierPresets_PriceCheckFreshnessHoursInRange",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "SupplierPresets"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The column is not nullable and its default is <c>1</c>, which is about <em>new</em> rows: a
    /// preset a Super Admin adds from now on is created searchable. The rows that already existed get
    /// their own <c>IsEnabled</c> value from the backfill below instead, which is why the default and
    /// the backfill are not in tension.
    /// </summary>
    [Fact]
    public async Task The_new_column_is_not_nullable_and_defaults_to_searching()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, TargetMigration);

        Assert.Equal(
            "1",
            await MigrationSchemaProbe.ColumnDefaultAsync(connection, "SupplierPresets", NewColumn));

        // A row inserted with no value for it is searched, which is the default actually taking
        // effect rather than only being declared.
        await ExecuteAsync(
            connection,
            "INSERT INTO SupplierPresets "
                + "(Name, NormalisedName, Url, IsEnabled, CreatedAtUtc, UpdatedAtUtc, "
                + " ServesNationwide, ServedStates, PricesIncludeGst, PriceConnector, "
                + " PriceConnectorKey, PriceCheckFreshnessHours) "
                + "VALUES ('Later Grocer', 'LATER GROCER', NULL, 1, "
                + $"'{PreExistingInstant}', '{PreExistingInstant}', 1, '', 1, NULL, NULL, 30);");

        var stored = await ReadSearchingByNameAsync(connection);
        Assert.True(stored["LATER GROCER"].SearchPrices);
    }

    #endregion

    #region The backfill

    /// <summary>
    /// The criterion: every row that already existed gets <c>SearchPrices = IsEnabled</c>, so the
    /// collector's behaviour is unchanged on deploy. The database is edited first - one shipped record
    /// disabled, two created by hand in opposite states - so the assertion is about rows in both
    /// states rather than about a table where every row happens to be enabled.
    /// </summary>
    [Fact]
    public async Task The_upgrade_gives_every_existing_row_its_own_enabled_state()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);
        await InsertPreExistingPresetsAsync(connection);
        await ExecuteAsync(
            connection,
            "UPDATE SupplierPresets SET IsEnabled = 0 WHERE NormalisedName = 'COSTCO';");

        await MigrateToAsync(db, TargetMigration);

        var rows = await ReadSearchingByNameAsync(connection);

        // Every row, not only the ones this test created: the backfill has no WHERE clause because
        // it is about the whole table.
        Assert.All(rows.Values, row => Assert.Equal(row.IsEnabled, row.SearchPrices));

        // And spelled out in both directions, so "they are equal" is not satisfied by a table in
        // which every row is enabled.
        Assert.False(rows["COSTCO"].SearchPrices);
        Assert.False(rows["RETIRED WHOLESALER"].SearchPrices);
        Assert.True(rows["COLES"].SearchPrices);
        Assert.True(rows["HARRIS FARM MARKETS"].SearchPrices);
    }

    /// <summary>
    /// And nothing else about any row moves: no name, website, enabled state, market, GST basis,
    /// connector, freshness limit or instant is read or rewritten. <c>UpdatedAtUtc</c> in particular
    /// stays as it was - this migration gave the presets a column they never had rather than making
    /// an edit on a Super Admin's behalf, and moving that instant would have the page's own "last
    /// changed" column report a change nobody made.
    /// </summary>
    [Fact]
    public async Task The_upgrade_leaves_every_other_column_of_every_row_exactly_as_it_was()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);
        await InsertPreExistingPresetsAsync(connection);
        await ExecuteAsync(
            connection,
            "UPDATE SupplierPresets SET IsEnabled = 0 WHERE NormalisedName = 'COSTCO';");

        var before = await ReadPresetsAsync(connection);

        await MigrateToAsync(db, TargetMigration);

        Assert.Equal(before, await ReadPresetsAsync(connection));
    }

    #endregion

    #region Reversing it

    /// <summary>
    /// <c>Down</c> drops the column and nothing else. What it does <em>not</em> do is restore
    /// anything: the preset's own enabled state, which is what the collector read before this issue,
    /// was never written here and comes back untouched - including the row somebody had disabled.
    /// </summary>
    [Fact]
    public async Task Reversing_the_migration_drops_the_column_and_leaves_every_row_alone()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);
        await InsertPreExistingPresetsAsync(connection);
        await ExecuteAsync(
            connection,
            "UPDATE SupplierPresets SET IsEnabled = 0 WHERE NormalisedName = 'COSTCO';");

        var before = await ReadPresetsAsync(connection);
        var columnsBefore = await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierPresets");
        var indexesBefore = await MigrationSchemaProbe.IndexNamesAsync(connection, "SupplierPresets");

        await MigrateToAsync(db, TargetMigration);

        // Somebody switches one supplier's searches off while the column exists; reversing the
        // migration takes that distinction with it and leaves the suggestion flag as it is.
        await ExecuteAsync(
            connection,
            "UPDATE SupplierPresets SET SearchPrices = 0 WHERE NormalisedName = 'COLES';");

        await MigrateToAsync(db, PreviousMigration);

        var columnsAfter = await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierPresets");
        Assert.DoesNotContain(NewColumn, columnsAfter);
        Assert.Equal(columnsBefore, columnsAfter);
        Assert.Equal(indexesBefore, await MigrationSchemaProbe.IndexNamesAsync(connection, "SupplierPresets"));
        Assert.Equal(before, await ReadPresetsAsync(connection));

        // Which includes the enabled state: Coles is still suggested, and Costco is still not.
        var enabled = await QueryAsync(
            connection,
            "SELECT NormalisedName FROM SupplierPresets WHERE IsEnabled = 0 ORDER BY NormalisedName;",
            reader => reader.GetString(0));
        Assert.Equal(["COSTCO", "RETIRED WHOLESALER"], enabled);
    }

    /// <summary>
    /// And the upgrade is applicable again afterwards, from whatever state the rows were left in -
    /// which is what makes the rollback a rollback rather than a one-way door.
    /// </summary>
    [Fact]
    public async Task The_upgrade_can_be_applied_again_after_being_reversed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);
        await InsertPreExistingPresetsAsync(connection);

        await MigrateToAsync(db, TargetMigration);
        await MigrateToAsync(db, PreviousMigration);
        await MigrateToAsync(db, TargetMigration);

        var rows = await ReadSearchingByNameAsync(connection);
        Assert.All(rows.Values, row => Assert.Equal(row.IsEnabled, row.SearchPrices));
        Assert.False(rows["RETIRED WHOLESALER"].SearchPrices);
    }

    #endregion

    /// <summary>
    /// Two presets a Super Admin created themselves, in opposite states: one suggested and one
    /// retired. They are written with SQL on the <em>previous</em> schema, which is the point - this
    /// is a database the searching column has not reached yet.
    /// </summary>
    private static Task InsertPreExistingPresetsAsync(SqliteConnection connection) =>
        ExecuteAsync(
            connection,
            "INSERT INTO SupplierPresets "
                + "(Name, NormalisedName, Url, IsEnabled, CreatedAtUtc, UpdatedAtUtc, "
                + " ServesNationwide, ServedStates, PricesIncludeGst, PriceConnector, "
                + " PriceConnectorKey, PriceCheckFreshnessHours) "
                + "VALUES "
                + "('Harris Farm Markets', 'HARRIS FARM MARKETS', 'https://www.harrisfarm.test', 1, "
                + $"'{PreExistingInstant}', '{PreExistingInstant}', 0, 'NSW,ACT', 1, 1, 'harris', 30), "
                + "('Retired Wholesaler', 'RETIRED WHOLESALER', NULL, 0, "
                + $"'{PreExistingInstant}', '{PreExistingInstant}', 1, '', 0, NULL, NULL, 174);");

    /// <summary>
    /// One preset as the columns that existed <em>before</em> this migration hold it, so the same
    /// projection can be read on either side of the upgrade and compared.
    /// </summary>
    private sealed record PresetRow(
        string Name,
        string NormalisedName,
        string? Url,
        bool IsEnabled,
        bool ServesNationwide,
        string ServedStates,
        bool PricesIncludeGst,
        int? PriceConnector,
        string? PriceConnectorKey,
        int PriceCheckFreshnessHours,
        string CreatedAtUtc,
        string UpdatedAtUtc);

    private static Task<List<PresetRow>> ReadPresetsAsync(SqliteConnection connection) =>
        QueryAsync(
            connection,
            "SELECT Name, NormalisedName, Url, IsEnabled, ServesNationwide, ServedStates, "
                + "PricesIncludeGst, PriceConnector, PriceConnectorKey, PriceCheckFreshnessHours, "
                + "CreatedAtUtc, UpdatedAtUtc FROM SupplierPresets ORDER BY NormalisedName;",
            reader => new PresetRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4),
                reader.GetString(5),
                reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetInt32(9),
                reader.GetString(10),
                reader.GetString(11)));

    /// <summary>The two flags of issue #1077, by normalised name, after the upgrade.</summary>
    private static async Task<Dictionary<string, (bool IsEnabled, bool SearchPrices)>>
        ReadSearchingByNameAsync(SqliteConnection connection)
    {
        var rows = await QueryAsync(
            connection,
            "SELECT NormalisedName, IsEnabled, SearchPrices FROM SupplierPresets;",
            reader => (Key: reader.GetString(0), Flags: (reader.GetBoolean(1), reader.GetBoolean(2))));

        return rows.ToDictionary(row => row.Key, row => row.Flags, StringComparer.Ordinal);
    }

    private static async Task<List<T>> QueryAsync<T>(
        SqliteConnection connection,
        string sql,
        Func<SqliteDataReader, T> read)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<T>();
        while (await reader.ReadAsync())
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
