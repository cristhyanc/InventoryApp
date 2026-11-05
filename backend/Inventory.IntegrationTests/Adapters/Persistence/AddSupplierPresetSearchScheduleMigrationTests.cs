using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddSupplierPresetSearchSchedule</c> (issue #1108): four additive columns on
/// <c>SupplierPresets</c>, an explicit mapping from the freshness limit each row already stored, and
/// the freshness then recalculated from the mapped schedule.
///
/// <para>Two claims cannot be taken on trust. The first is the mapping itself - a preset at 102 hours
/// must come out searched on Thursday and Sunday, one at 174 on Thursday, and one at any other value
/// daily - and the second is that <strong>no row ends with a schedule and a freshness that
/// disagree</strong>, which is the criterion and is asserted by deriving the limit from the stored
/// schedule with the Domain's own rule and comparing it to the stored limit, for every row.</para>
///
/// <para>Production applies migrations at startup (AGENTS.md § Database and migrations), so this one
/// runs over presets a Super Admin may have added or edited. Every test here therefore edits the
/// database on the <em>previous</em> schema first, which is also the only way to produce a row the
/// fallback rule reaches at all: no seeded row does.</para>
///
/// <para>The rows are read with SQL rather than through the entity, exactly as
/// <c>AddSupplierPresetSearchPricesMigrationTests</c> does: what is under test is the schema and the
/// data the migration wrote, and a test that read them through the model would pass on a model that
/// had drifted from the columns the migration created - and could not read the table at all at the
/// point where the columns do not exist yet.</para>
/// </summary>
public class AddSupplierPresetSearchScheduleMigrationTests
{
    private const string PreviousMigration = "20261104090000_DropProviderIdentityLinkTables";
    private const string TargetMigration = "20261105035740_AddSupplierPresetSearchSchedule";

    private static readonly string[] NewColumns =
        ["SearchFrequency", "SearchDaysOfWeek", "FortnightStartDate", "ScheduleEffectiveFromUtc"];

    /// <summary>
    /// When the presets a Super Admin created themselves were created: the clock, to the tick, which
    /// is what the create path records - so these rows are visibly not migration-seeded ones.
    /// </summary>
    private const string PreExistingInstant = "2026-11-04 09:15:42.1234567";

    #region The columns

    /// <summary>
    /// Four columns arrive and nothing else changes about the table: no column is removed or renamed,
    /// no index is added or dropped, and - because this is a platform table - no business key appears
    /// (docs/architecture.md § Tenant ownership).
    /// </summary>
    [Fact]
    public async Task The_migration_adds_four_columns_and_changes_nothing_else_about_the_table()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);

        var before = await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierPresets");
        var indexesBefore = await MigrationSchemaProbe.IndexNamesAsync(connection, "SupplierPresets");
        Assert.DoesNotContain(NewColumns[0], before);

        await MigrateToAsync(db, TargetMigration);

        var after = await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierPresets");
        Assert.Equal(
            before.Concat(NewColumns).Order(StringComparer.Ordinal),
            after.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("BusinessId", after);
        Assert.Equal(indexesBefore, await MigrationSchemaProbe.IndexNamesAsync(connection, "SupplierPresets"));

        // The table's existing check constraint names a different column and survives: the upgrade is
        // four ALTER TABLE ADD COLUMNs rather than a rebuild.
        Assert.Contains(
            "CK_SupplierPresets_PriceCheckFreshnessHoursInRange",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "SupplierPresets"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The frequency column defaults to the daily frequency and the days column to no days, which is
    /// about <em>new</em> rows: a preset inserted without a schedule is a daily one, because daily is
    /// what the collector did before this issue. The rows that already existed get theirs from the
    /// mapping below instead, so the defaults and the mapping are not in tension.
    /// </summary>
    [Fact]
    public async Task The_new_columns_default_to_the_daily_schedule()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, TargetMigration);

        Assert.Equal(
            "1",
            await MigrationSchemaProbe.ColumnDefaultAsync(
                connection,
                "SupplierPresets",
                "SearchFrequency"));

        // A row inserted with no schedule at all is daily with no days, which the Domain reads back
        // as a storable schedule rather than refusing.
        await ExecuteAsync(
            connection,
            "INSERT INTO SupplierPresets "
                + "(Name, NormalisedName, Url, IsEnabled, SearchPrices, CreatedAtUtc, UpdatedAtUtc, "
                + " ServesNationwide, ServedStates, PricesIncludeGst, PriceConnector, "
                + " PriceConnectorKey, PriceCheckFreshnessHours) "
                + "VALUES ('Later Grocer', 'LATER GROCER', NULL, 1, 1, "
                + $"'{PreExistingInstant}', '{PreExistingInstant}', 1, '', 1, NULL, NULL, 30);");

        var row = (await ReadSchedulesAsync(connection))["LATER GROCER"];

        Assert.Equal((int)SupplierSearchFrequency.Daily, row.SearchFrequency);
        Assert.Equal(string.Empty, row.SearchDaysOfWeek);
        Assert.Null(row.FortnightStartDate);
    }

    #endregion

    #region The explicit mapping

    /// <summary>
    /// The criterion, over the seeded database plus the rows only a deployment can have: 30 becomes
    /// daily, 102 becomes Thursday and Sunday, 174 becomes Thursday, and every other value becomes
    /// daily.
    /// </summary>
    [Fact]
    public async Task The_upgrade_maps_every_stored_freshness_limit_to_its_recorded_schedule()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);
        await InsertPreExistingPresetsAsync(connection);

        await MigrateToAsync(db, TargetMigration);

        var rows = await ReadSchedulesAsync(connection);

        // The seeded rows: Coles, Woolworths and Costco hold the daily 30, and Big W and Kmart hold
        // the weekly 174 that SetWeeklySupplierPriceFreshness wrote. Both 174-hour rows come out on
        // Thursday, because the rule reads the stored value and no supplier name appears in it - the
        // owner's 1 November decision that one of them is searched on Mondays is a schedule they set
        // on the page (#1109), not something a mapping can know.
        AssertDaily(rows["COLES"]);
        AssertDaily(rows["WOOLWORTHS"]);
        AssertDaily(rows["COSTCO"]);
        AssertWeekly(rows["BIG W"], "THU", 174);
        AssertWeekly(rows["KMART"], "THU", 174);

        // A row a deployment put at the twice-weekly limit.
        AssertWeekly(rows["TWICE WEEKLY GROCER"], "THU,SUN", 102);

        // And the two the fallback rule reaches: an unrecognised limit, high or low, becomes daily.
        AssertDaily(rows["LONG LIMIT WHOLESALER"]);
        AssertDaily(rows["SHORT LIMIT WHOLESALER"]);
    }

    /// <summary>
    /// <strong>No row ends with a schedule and a freshness that disagree.</strong> Every stored
    /// schedule is read back with the Domain's own rule and its derived limit compared to the stored
    /// one - which is the whole claim the mapping exists to make, stated once over every row rather
    /// than four times over four expected values.
    /// </summary>
    [Fact]
    public async Task No_row_ends_with_a_schedule_and_a_freshness_that_disagree()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);
        await InsertPreExistingPresetsAsync(connection);

        await MigrateToAsync(db, TargetMigration);

        var rows = await ReadSchedulesAsync(connection);

        // The nine shipped presets plus the three this test added, so the loop below is over a table
        // that actually holds rows in every mapped state rather than over an empty one.
        Assert.Equal(12, rows.Count);

        foreach (var (name, row) in rows)
        {
            var schedule = SupplierSearchSchedulePolicy.Read(
                (SupplierSearchFrequency)row.SearchFrequency,
                row.SearchDaysOfWeek,
                row.FortnightStartDate is { } date
                    ? DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture)
                    : null);

            Assert.Equal(
                SupplierSearchSchedulePolicy.FreshnessHoursFor(schedule),
                row.PriceCheckFreshnessHours);
            Assert.NotNull(name);
        }
    }

    /// <summary>
    /// Every row's schedule becomes effective at the instant the migration ran, so no answer recorded
    /// before the schedules existed can cover a pair. It is read from the database's own clock, which
    /// is why this asserts a window rather than a literal.
    /// </summary>
    [Fact]
    public async Task Every_row_becomes_effective_at_the_instant_the_migration_ran()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);
        await InsertPreExistingPresetsAsync(connection);

        var before = DateTime.UtcNow.AddSeconds(-5);
        await MigrateToAsync(db, TargetMigration);
        var after = DateTime.UtcNow.AddSeconds(5);

        var rows = await ReadSchedulesAsync(connection);

        Assert.All(rows.Values, row =>
        {
            var effectiveFrom = DateTime.Parse(
                row.ScheduleEffectiveFromUtc,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None);

            Assert.InRange(effectiveFrom, before, after);
        });
    }

    /// <summary>
    /// And nothing else about any row moves: no name, website, enabled state, searching state,
    /// market, GST basis, connector or instant is read or rewritten. <c>UpdatedAtUtc</c> in particular
    /// stays as it was - this migration gave the presets columns they never had rather than making an
    /// edit on a Super Admin's behalf.
    /// </summary>
    [Fact]
    public async Task The_upgrade_leaves_every_other_column_of_every_row_exactly_as_it_was()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);
        await InsertPreExistingPresetsAsync(connection);

        var before = await ReadPresetsAsync(connection);

        await MigrateToAsync(db, TargetMigration);

        Assert.Equal(before, await ReadPresetsAsync(connection));
    }

    /// <summary>
    /// No row is inserted or removed, which is the other half of "nothing else is touched".
    /// </summary>
    [Fact]
    public async Task The_upgrade_inserts_and_deletes_no_row()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);
        await InsertPreExistingPresetsAsync(connection);

        var before = await QueryAsync(
            connection,
            "SELECT NormalisedName FROM SupplierPresets ORDER BY NormalisedName;",
            reader => reader.GetString(0));

        await MigrateToAsync(db, TargetMigration);

        Assert.Equal(
            before,
            await QueryAsync(
                connection,
                "SELECT NormalisedName FROM SupplierPresets ORDER BY NormalisedName;",
                reader => reader.GetString(0)));
    }

    #endregion

    #region Reversing it

    /// <summary>
    /// <c>Down</c> drops the four columns and nothing else. What it does <em>not</em> do is restore
    /// the freshness limit of a row the fallback rule moved: the value it held before is recorded
    /// nowhere, so inventing one on the way back would be a different guess rather than a
    /// restoration. The three recognised limits were written back unchanged and come back unchanged.
    /// </summary>
    [Fact]
    public async Task Reversing_the_migration_drops_the_four_columns()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);
        await InsertPreExistingPresetsAsync(connection);

        var columnsBefore = await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierPresets");
        var indexesBefore = await MigrationSchemaProbe.IndexNamesAsync(connection, "SupplierPresets");

        await MigrateToAsync(db, TargetMigration);
        await MigrateToAsync(db, PreviousMigration);

        var columnsAfter = await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierPresets");
        Assert.Equal(columnsBefore, columnsAfter);
        Assert.Equal(indexesBefore, await MigrationSchemaProbe.IndexNamesAsync(connection, "SupplierPresets"));

        // The three recognised limits are exactly as they were; the fallback rows keep the 30 the
        // upgrade gave them, which this assertion states rather than hides.
        var limits = await QueryAsync(
            connection,
            "SELECT NormalisedName, PriceCheckFreshnessHours FROM SupplierPresets "
                + "ORDER BY NormalisedName;",
            reader => (Name: reader.GetString(0), Limit: reader.GetInt32(1)));

        Assert.Equal(174, limits.Single(row => row.Name == "BIG W").Limit);
        Assert.Equal(102, limits.Single(row => row.Name == "TWICE WEEKLY GROCER").Limit);
        Assert.Equal(30, limits.Single(row => row.Name == "COLES").Limit);
        Assert.Equal(30, limits.Single(row => row.Name == "LONG LIMIT WHOLESALER").Limit);
        Assert.Equal(30, limits.Single(row => row.Name == "SHORT LIMIT WHOLESALER").Limit);
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

        var rows = await ReadSchedulesAsync(connection);
        AssertWeekly(rows["BIG W"], "THU", 174);
        AssertWeekly(rows["TWICE WEEKLY GROCER"], "THU,SUN", 102);
        AssertDaily(rows["LONG LIMIT WHOLESALER"]);
    }

    #endregion

    #region The literals this migration writes

    /// <summary>
    /// The limits the migration writes as literals are the ones the Domain derives from the schedules
    /// it writes beside them. This is what keeps the two in step without the migration calling into
    /// application code that may change - the same arrangement
    /// <c>SetWeeklySupplierPriceFreshnessMigrationTests</c> uses.
    /// </summary>
    [Fact]
    public void The_migrations_literals_are_the_limits_the_domain_derives()
    {
        Assert.Equal(
            30,
            SupplierSearchSchedulePolicy.FreshnessHoursFor(SupplierSearchSchedulePolicy.Daily));

        Assert.Equal(
            102,
            SupplierSearchSchedulePolicy.FreshnessHoursFor(SupplierSearchSchedulePolicy.Read(
                SupplierSearchFrequency.Weekly,
                "THU,SUN",
                null)));

        Assert.Equal(
            174,
            SupplierSearchSchedulePolicy.FreshnessHoursFor(SupplierSearchSchedulePolicy.Read(
                SupplierSearchFrequency.Weekly,
                "THU",
                null)));

        // And the stored day lists the migration writes are the canonical spelling the Domain would
        // have written, so a later save can tell the schedule did not change.
        Assert.Equal(
            "THU,SUN",
            SupplierSearchSchedulePolicy.StoredDaysOfWeek([DayOfWeek.Thursday, DayOfWeek.Sunday]));
        Assert.Equal(
            "THU",
            SupplierSearchSchedulePolicy.StoredDaysOfWeek([DayOfWeek.Thursday]));
    }

    #endregion

    private static void AssertDaily(ScheduleRow row)
    {
        Assert.Equal((int)SupplierSearchFrequency.Daily, row.SearchFrequency);
        Assert.Equal(string.Empty, row.SearchDaysOfWeek);
        Assert.Null(row.FortnightStartDate);
        Assert.Equal(30, row.PriceCheckFreshnessHours);
    }

    private static void AssertWeekly(ScheduleRow row, string expectedDays, int expectedLimit)
    {
        Assert.Equal((int)SupplierSearchFrequency.Weekly, row.SearchFrequency);
        Assert.Equal(expectedDays, row.SearchDaysOfWeek);
        Assert.Null(row.FortnightStartDate);
        Assert.Equal(expectedLimit, row.PriceCheckFreshnessHours);
    }

    /// <summary>
    /// Three presets a deployment can have and the seed cannot: one at the twice-weekly limit the
    /// owner recorded, and two at limits nothing recognises - one above every recorded value and one
    /// below them - which are the only rows the fallback rule reaches. They are written with SQL on
    /// the <em>previous</em> schema, which is the point: this is a database the schedule columns have
    /// not reached yet.
    /// </summary>
    private static Task InsertPreExistingPresetsAsync(SqliteConnection connection) =>
        ExecuteAsync(
            connection,
            "INSERT INTO SupplierPresets "
                + "(Name, NormalisedName, Url, IsEnabled, SearchPrices, CreatedAtUtc, UpdatedAtUtc, "
                + " ServesNationwide, ServedStates, PricesIncludeGst, PriceConnector, "
                + " PriceConnectorKey, PriceCheckFreshnessHours) "
                + "VALUES "
                + "('Twice Weekly Grocer', 'TWICE WEEKLY GROCER', NULL, 1, 1, "
                + $"'{PreExistingInstant}', '{PreExistingInstant}', 1, '', 1, NULL, NULL, 102), "
                + "('Long Limit Wholesaler', 'LONG LIMIT WHOLESALER', NULL, 1, 1, "
                + $"'{PreExistingInstant}', '{PreExistingInstant}', 1, '', 1, NULL, NULL, 336), "
                + "('Short Limit Wholesaler', 'SHORT LIMIT WHOLESALER', NULL, 0, 0, "
                + $"'{PreExistingInstant}', '{PreExistingInstant}', 1, '', 0, NULL, NULL, 12);");

    /// <summary>One preset's schedule columns, as SQL holds them, after the upgrade.</summary>
    private sealed record ScheduleRow(
        int SearchFrequency,
        string SearchDaysOfWeek,
        string? FortnightStartDate,
        string ScheduleEffectiveFromUtc,
        int PriceCheckFreshnessHours);

    private static async Task<Dictionary<string, ScheduleRow>> ReadSchedulesAsync(
        SqliteConnection connection)
    {
        var rows = await QueryAsync(
            connection,
            "SELECT NormalisedName, SearchFrequency, SearchDaysOfWeek, FortnightStartDate, "
                + "ScheduleEffectiveFromUtc, PriceCheckFreshnessHours FROM SupplierPresets;",
            reader => (
                Key: reader.GetString(0),
                Row: new ScheduleRow(
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt32(5))));

        return rows.ToDictionary(row => row.Key, row => row.Row, StringComparer.Ordinal);
    }

    /// <summary>
    /// One preset as the columns that existed <em>before</em> this migration hold it, so the same
    /// projection can be read on either side of the upgrade and compared. The freshness limit is
    /// deliberately absent: this migration rewrites it, which the mapping tests above assert.
    /// </summary>
    private sealed record PresetRow(
        string Name,
        string NormalisedName,
        string? Url,
        bool IsEnabled,
        bool SearchPrices,
        bool ServesNationwide,
        string ServedStates,
        bool PricesIncludeGst,
        int? PriceConnector,
        string? PriceConnectorKey,
        string CreatedAtUtc,
        string UpdatedAtUtc);

    private static Task<List<PresetRow>> ReadPresetsAsync(SqliteConnection connection) =>
        QueryAsync(
            connection,
            "SELECT Name, NormalisedName, Url, IsEnabled, SearchPrices, ServesNationwide, "
                + "ServedStates, PricesIncludeGst, PriceConnector, PriceConnectorKey, "
                + "CreatedAtUtc, UpdatedAtUtc FROM SupplierPresets ORDER BY NormalisedName;",
            reader => new PresetRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4),
                reader.GetBoolean(5),
                reader.GetString(6),
                reader.GetBoolean(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11)));

    private static Task MigrateToAsync(AppDbContext db, string targetMigration) =>
        db.GetService<IMigrator>().MigrateAsync(targetMigration);

    private static DbContextOptions<AppDbContext> OptionsFor(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

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
