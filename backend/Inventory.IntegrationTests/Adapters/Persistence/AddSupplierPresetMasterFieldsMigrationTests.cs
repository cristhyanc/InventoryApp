using Inventory.Domain.Suppliers;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddSupplierPresetMasterFields</c> (issue #931, first task of #817): the five
/// supplier-master columns, the five records the first price-check rollout needs, and the values the
/// four rows that already existed are given.
///
/// <para>The claims that cannot be taken on trust are the ones about a database that is <em>not</em>
/// empty. Production applies migrations at startup (AGENTS.md § Database and migrations), so this one
/// runs over rows a Super Admin may already have renamed, disabled or given a website to - and it
/// must set the new columns on the rows it can still recognise, insert the missing records exactly
/// once, and leave everything else alone. Each of those is asserted here against a database that was
/// edited first.</para>
///
/// <para>The rows are read with SQL rather than through the entity on purpose. What is under test is
/// the schema and the data the migration wrote, and a test that read them through the model would
/// pass on a model that had drifted from the columns the migration created.</para>
/// </summary>
public class AddSupplierPresetMasterFieldsMigrationTests
{
    private const string PreviousMigration = "20261025121047_AddPickListRefillLineMachine";
    private const string MasterFieldsMigration = "AddSupplierPresetMasterFields";

    /// <summary>
    /// When the presets a Super Admin created themselves were created: the clock, to the tick, which
    /// is what the create path records and what tells those rows apart from the ones the migration
    /// inserts under the fixed literal <c>2026-10-27 00:00:00</c>.
    /// </summary>
    private const string PreExistingInstant = "2026-10-26 09:15:42.1234567";

    /// <summary>
    /// The five new columns, and nothing else: the migration is additive, so the columns that were
    /// there before stay exactly as they were.
    /// </summary>
    private static readonly string[] NewColumns =
        ["PriceConnector", "PriceConnectorKey", "PricesIncludeGst", "ServedStates", "ServesNationwide"];

    private static readonly string[] ShippedNames =
    [
        "ALDI",
        "Big W",
        "Coles",
        "Costco",
        "JB Metro NSW",
        "JB Metro SA & NT",
        "JB Metro South Coast & ACT",
        "Kmart",
        "Woolworths",
    ];

    private static async Task MigrateToAsync(AppDbContext db, string targetMigration) =>
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);

    private static DbContextOptions<AppDbContext> OptionsFor(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

    #region The columns

    [Fact]
    public async Task The_migration_adds_the_five_supplier_master_columns_and_no_business_column()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);

        var before = await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierPresets");
        Assert.All(NewColumns, column => Assert.DoesNotContain(column, before));

        await MigrateToAsync(db, MasterFieldsMigration);

        var after = await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierPresets");
        Assert.All(NewColumns, column => Assert.Contains(column, after));

        // Still a platform table: about no business, so there is no business key for anything to
        // filter or stamp (docs/architecture.md § Tenant ownership).
        Assert.DoesNotContain("BusinessId", after);

        // Additive: every column the table already had is still there, and nothing else arrived.
        Assert.Equal(
            before.Concat(NewColumns).Order(StringComparer.Ordinal),
            after.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The two flags and the state list are not nullable, so an existing row the migration does not
    /// recognise still has a value: the column defaults decide what that value is. "Not nationwide
    /// and no states" is deliberate - it is an unconfigured market a Super Admin must set, which is a
    /// visible gap rather than an invented claim that the supplier serves the whole country.
    /// </summary>
    [Fact]
    public async Task The_new_non_nullable_columns_carry_fail_closed_defaults()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, MasterFieldsMigration);

        Assert.Equal(
            "0",
            await MigrationSchemaProbe.ColumnDefaultAsync(connection, "SupplierPresets", "ServesNationwide"));
        Assert.Equal(
            "0",
            await MigrationSchemaProbe.ColumnDefaultAsync(connection, "SupplierPresets", "PricesIncludeGst"));
        Assert.Equal(
            "''",
            await MigrationSchemaProbe.ColumnDefaultAsync(connection, "SupplierPresets", "ServedStates"));

        // The connector and its key are nullable, because "no connector" and "no key" are the real
        // states Costco is in; a default would make them look chosen.
        Assert.Null(
            await MigrationSchemaProbe.ColumnDefaultAsync(connection, "SupplierPresets", "PriceConnector"));
        Assert.Null(
            await MigrationSchemaProbe.ColumnDefaultAsync(connection, "SupplierPresets", "PriceConnectorKey"));
    }

    #endregion

    #region The records

    /// <summary>
    /// The whole shipped master list after the upgrade: the four rows that existed with their new
    /// values, and the five records the first rollout checks, inserted here and nowhere else.
    /// </summary>
    [Fact]
    public async Task The_migration_leaves_the_nine_records_the_first_rollout_checks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await db.Database.MigrateAsync();

        var presets = await ReadPresetsAsync(connection);

        Assert.Equal(ShippedNames, presets.Select(preset => preset.Name).ToArray());

        // The four that already existed, by normalised name, with the values issue #931 states.
        AssertPreset(presets, "Coles", nationwide: true, states: "", gst: true, connector: 1, key: "coles");
        AssertPreset(presets, "Woolworths", nationwide: true, states: "", gst: true, connector: 1, key: "woolworths");
        AssertPreset(presets, "Big W", nationwide: true, states: "", gst: true, connector: 3, key: null);
        AssertPreset(presets, "Costco", nationwide: true, states: "", gst: true, connector: null, key: null);

        // The five inserted here. ALDI takes the Varnmill route like the other two supermarkets,
        // Kmart its own, and each JB Metro site is one record whose GraphQL route reads the site at
        // its own Url - which is why a new JB Metro site is a new record and not new code.
        AssertPreset(presets, "ALDI", nationwide: true, states: "", gst: true, connector: 1, key: "aldi");
        AssertPreset(presets, "Kmart", nationwide: true, states: "", gst: true, connector: 2, key: null);
        AssertPreset(presets, "JB Metro NSW", nationwide: false, states: "NSW", gst: false, connector: 4, key: null);
        AssertPreset(
            presets,
            "JB Metro South Coast & ACT",
            nationwide: false,
            states: "NSW,ACT",
            gst: false,
            connector: 4,
            key: null);
        AssertPreset(
            presets,
            "JB Metro SA & NT",
            nationwide: false,
            states: "SA,NT",
            gst: false,
            connector: 4,
            key: null);
    }

    /// <summary>
    /// JB Metro prices are ex-GST and the supermarkets' are not, and that is a stored fact rather
    /// than something a name or a connector implies. It is asserted on its own because it is the one
    /// new field AGENTS.md treats as financial data.
    /// </summary>
    [Fact]
    public async Task Only_the_jb_metro_records_are_seeded_as_ex_gst()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await db.Database.MigrateAsync();

        var presets = await ReadPresetsAsync(connection);

        Assert.Equal(
            ["JB Metro NSW", "JB Metro SA & NT", "JB Metro South Coast & ACT"],
            presets.Where(preset => !preset.PricesIncludeGst).Select(preset => preset.Name).ToArray());
    }

    /// <summary>
    /// The migration writes the connector numbers as literals, because a migration records the rows
    /// it inserted and must not change meaning when application code does. This is the guard that
    /// makes that safe: renumbering <see cref="SupplierPriceConnector"/> fails here rather than
    /// silently re-pointing nine seeded records at the wrong collection routes.
    /// </summary>
    [Fact]
    public async Task The_seeded_connector_numbers_are_the_declared_routes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await db.Database.MigrateAsync();

        var presets = await ReadPresetsAsync(connection);

        Assert.Equal((int)SupplierPriceConnector.Varnmill, Single(presets, "Coles").PriceConnector);
        Assert.Equal((int)SupplierPriceConnector.Varnmill, Single(presets, "Woolworths").PriceConnector);
        Assert.Equal((int)SupplierPriceConnector.Varnmill, Single(presets, "ALDI").PriceConnector);
        Assert.Equal((int)SupplierPriceConnector.Kmart, Single(presets, "Kmart").PriceConnector);
        Assert.Equal((int)SupplierPriceConnector.BigW, Single(presets, "Big W").PriceConnector);
        Assert.Equal((int)SupplierPriceConnector.JbMetro, Single(presets, "JB Metro NSW").PriceConnector);
        Assert.Equal(
            (int)SupplierPriceConnector.JbMetro,
            Single(presets, "JB Metro South Coast & ACT").PriceConnector);
        Assert.Equal((int)SupplierPriceConnector.JbMetro, Single(presets, "JB Metro SA & NT").PriceConnector);
    }

    /// <summary>
    /// Every seeded state list is one the application's own rule would have written, in its canonical
    /// order: a migration that hand-wrote "ACT,NSW" would store a market the policy could never
    /// produce and that nothing else would compare equal to.
    /// </summary>
    [Fact]
    public async Task Every_seeded_state_list_is_the_canonical_one_the_policy_writes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await db.Database.MigrateAsync();

        Assert.All(
            await ReadPresetsAsync(connection),
            preset => Assert.Equal(
                preset.ServesNationwide
                    ? string.Empty
                    : SupplierPresetMasterPolicy.NormaliseServedStates(
                        servesNationwide: false,
                        SupplierPresetMasterPolicy.ReadServedStates(preset.ServedStates)),
                preset.ServedStates));
    }

    /// <summary>
    /// Each inserted record's normalised name is the one the application would compute, so the unique
    /// index keeps doing its job for the seeded rows and a Super Admin typing "aldi" is told the name
    /// is taken rather than creating a second ALDI.
    /// </summary>
    [Fact]
    public async Task Every_inserted_record_carries_the_normalised_name_the_application_computes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await db.Database.MigrateAsync();

        Assert.All(
            await ReadPresetsAsync(connection),
            preset => Assert.Equal(
                SupplierPresetPolicy.NormalisedName(preset.Name),
                preset.NormalisedName));
    }

    /// <summary>
    /// The three JB Metro records are told apart by their site, which is the record's existing
    /// website column: the connector reads it, so a record with no site would be a route with nowhere
    /// to go.
    /// </summary>
    [Fact]
    public async Task Each_jb_metro_record_carries_its_own_site_address()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await db.Database.MigrateAsync();

        var presets = await ReadPresetsAsync(connection);

        Assert.Equal("https://www.jbmetro.com.au", Single(presets, "JB Metro NSW").Url);
        Assert.Equal(
            "https://www.jbmetro-sc-act.com.au",
            Single(presets, "JB Metro South Coast & ACT").Url);
        Assert.Equal("https://www.jbmetro-sa-nt.com.au", Single(presets, "JB Metro SA & NT").Url);

        // And the records that already existed are not given one: a website nobody verified is not
        // something a migration may invent, which is why #848 inserted them without one.
        Assert.All(
            presets.Where(preset => !preset.Name.StartsWith("JB Metro", StringComparison.Ordinal)),
            preset => Assert.Null(preset.Url));
    }

    #endregion

    #region What an edited database keeps

    /// <summary>
    /// The acceptance criterion in full, over a database a Super Admin has already worked in: the
    /// renamed row is not recognised and is therefore left entirely alone, the disabled row stays
    /// disabled while still getting its new values, the row given a website keeps it, and the five
    /// records arrive exactly once however many times the schema is brought up to date afterwards.
    /// </summary>
    [Fact]
    public async Task An_edited_renamed_and_disabled_list_survives_the_upgrade_and_a_second_migrate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = OptionsFor(connection);

        await using (var db = TestAppDbContext.Unrestricted(options))
        {
            await MigrateToAsync(db, PreviousMigration);

            // A Super Admin's own edits, made before this migration existed: one preset renamed, one
            // disabled, one given a website.
            await ExecuteAsync(
                connection,
                "UPDATE SupplierPresets SET Name = 'Coles Group', NormalisedName = 'COLES GROUP' "
                    + "WHERE NormalisedName = 'COLES';");
            await ExecuteAsync(
                connection,
                "UPDATE SupplierPresets SET IsEnabled = 0 WHERE NormalisedName = 'COSTCO';");
            await ExecuteAsync(
                connection,
                "UPDATE SupplierPresets SET Url = 'https://www.woolworths.com.au' "
                    + "WHERE NormalisedName = 'WOOLWORTHS';");
        }

        await using (var db = TestAppDbContext.Unrestricted(options))
        {
            await db.Database.MigrateAsync();
        }

        // Up to date again, which is what the next deployment does, and then once more: the records
        // are inserted by this migration and by nothing else, so a restart adds none of them back.
        await using (var again = TestAppDbContext.Unrestricted(options))
        {
            await again.Database.MigrateAsync();
            Assert.Empty(await again.Database.GetPendingMigrationsAsync());
        }

        var presets = await ReadPresetsAsync(connection);

        // Nine rows: the four that existed - one of them renamed - and the five inserted once.
        Assert.Equal(
            [
                "ALDI",
                "Big W",
                "Coles Group",
                "Costco",
                "JB Metro NSW",
                "JB Metro SA & NT",
                "JB Metro South Coast & ACT",
                "Kmart",
                "Woolworths",
            ],
            presets.Select(preset => preset.Name).ToArray());

        // The renamed row is left alone entirely. The migration recognises a row by its normalised
        // name, and this is not a row it knows: guessing that "Coles Group" is still Coles would be
        // the migration overwriting a decision somebody made deliberately.
        var renamed = Single(presets, "Coles Group");
        Assert.False(renamed.ServesNationwide);
        Assert.Equal(string.Empty, renamed.ServedStates);
        Assert.False(renamed.PricesIncludeGst);
        Assert.Null(renamed.PriceConnector);
        Assert.Null(renamed.PriceConnectorKey);

        // The disabled row stays disabled and still gets its values: what it claims and whether it is
        // suggested are different facts, and the migration decides only the first.
        var costco = Single(presets, "Costco");
        Assert.False(costco.IsEnabled);
        Assert.True(costco.ServesNationwide);
        Assert.True(costco.PricesIncludeGst);
        Assert.Null(costco.PriceConnector);

        // The website somebody recorded is still there, beside the values the migration set.
        var woolworths = Single(presets, "Woolworths");
        Assert.Equal("https://www.woolworths.com.au", woolworths.Url);
        Assert.Equal(1, woolworths.PriceConnector);
        Assert.Equal("woolworths", woolworths.PriceConnectorKey);

        // Every seeded row is enabled except the one that was deliberately switched off: nothing was
        // re-enabled.
        Assert.Equal(
            ["Costco"],
            presets.Where(preset => !preset.IsEnabled).Select(preset => preset.Name).ToArray());
    }

    /// <summary>
    /// A seeded row that is gone stays gone, and an inserted record a Super Admin then edits is not
    /// reset: the migration runs once, when it is applied, so every later deployment leaves the list
    /// exactly as the people who keep it left it.
    /// </summary>
    [Fact]
    public async Task A_removed_record_is_not_re_inserted_and_an_edited_new_record_is_not_reset()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = OptionsFor(connection);

        await using (var db = TestAppDbContext.Unrestricted(options))
        {
            await db.Database.MigrateAsync();
        }

        await ExecuteAsync(connection, "DELETE FROM SupplierPresets WHERE NormalisedName = 'KMART';");
        await ExecuteAsync(
            connection,
            "UPDATE SupplierPresets SET ServesNationwide = 0, ServedStates = 'NSW,VIC', "
                + "IsEnabled = 0 WHERE NormalisedName = 'ALDI';");

        await using (var again = TestAppDbContext.Unrestricted(options))
        {
            await again.Database.MigrateAsync();
        }

        var presets = await ReadPresetsAsync(connection);

        Assert.DoesNotContain("Kmart", presets.Select(preset => preset.Name));

        var aldi = Single(presets, "ALDI");
        Assert.False(aldi.ServesNationwide);
        Assert.Equal("NSW,VIC", aldi.ServedStates);
        Assert.False(aldi.IsEnabled);
    }

    /// <summary>
    /// The instants the Super Admin reads as "last changed" are not touched on the rows that already
    /// existed. The migration set columns those rows never had; it did not make an edit on anybody's
    /// behalf, and reporting one would be this migration claiming a change a person did not make.
    /// </summary>
    [Fact]
    public async Task The_existing_rows_keep_their_created_and_updated_instants()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = OptionsFor(connection);

        await using (var db = TestAppDbContext.Unrestricted(options))
        {
            await MigrateToAsync(db, PreviousMigration);
        }

        var before = await ReadInstantsAsync(connection);

        await using (var db = TestAppDbContext.Unrestricted(options))
        {
            await db.Database.MigrateAsync();
        }

        var after = await ReadInstantsAsync(connection);

        Assert.All(before, instants => Assert.Equal(instants.Value, after[instants.Key]));
    }

    #endregion

    #region A name a Super Admin already used

    /// <summary>
    /// Why skipping a taken name is load-bearing and not tidiness: the database refuses a second row
    /// under a seeded normalised name, with exactly this message. An unconditional insert over a
    /// database that already holds an <c>ALDI</c> therefore fails the <em>migration</em>, and
    /// production applies migrations at startup and fails closed (AGENTS.md § Database and
    /// migrations) - so the consequence is a deployment that does not start, not a row that does not
    /// arrive.
    /// </summary>
    [Fact]
    public async Task The_database_refuses_a_second_row_under_a_seeded_normalised_name()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await db.Database.MigrateAsync();

        var error = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "INSERT INTO SupplierPresets "
                + "(Name, NormalisedName, Url, IsEnabled, CreatedAtUtc, UpdatedAtUtc, "
                + "ServesNationwide, ServedStates, PricesIncludeGst) "
                + $"VALUES ('ALDI', 'ALDI', NULL, 1, '{PreExistingInstant}', '{PreExistingInstant}', "
                + "0, '', 0);"));

        Assert.Contains(
            "UNIQUE constraint failed: SupplierPresets.NormalisedName",
            error.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The upgrade state no other test here covers, and the one that stopped the application from
    /// starting: a database where somebody has <em>already</em> created a preset under one of the five
    /// names this migration inserts. The Super Admin surface of #848 has accepted any name since
    /// before this migration existed, <c>NormalisedName</c> is unique, and production applies
    /// migrations at startup and fails closed - so an unconditional insert here would not merely skip
    /// a row, it would fail the whole upgrade with
    /// <c>UNIQUE constraint failed: SupplierPresets.NormalisedName</c> and stop the deployment from
    /// serving requests.
    ///
    /// <para>Three pre-existing records, each in a state somebody put it in: one enabled with a
    /// website, one typed in lower case and then deliberately disabled, and one JB Metro site pointed
    /// at a different address. The upgrade must succeed, insert only the records whose names are free,
    /// and leave all three exactly as they are.</para>
    /// </summary>
    [Fact]
    public async Task A_preset_already_holding_a_seeded_name_survives_the_upgrade_and_is_not_duplicated()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = OptionsFor(connection);

        await using (var db = TestAppDbContext.Unrestricted(options))
        {
            await MigrateToAsync(db, PreviousMigration);
            await InsertPreExistingPresetsAsync(connection);
        }

        // The upgrade itself: before this repair it threw a UNIQUE constraint violation here.
        await using (var db = TestAppDbContext.Unrestricted(options))
        {
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }

        var presets = await ReadPresetsAsync(connection);

        // Nine rows and no duplicate: the four #848 shipped, the three that were already there under
        // a name this migration would have used, and the two whose names were still free.
        Assert.Equal(
            [
                "ALDI",
                "BIG W",
                "COLES",
                "COSTCO",
                "JB METRO NSW",
                "JB METRO SA & NT",
                "JB METRO SOUTH COAST & ACT",
                "KMART",
                "WOOLWORTHS",
            ],
            presets.Select(preset => preset.NormalisedName).Order(StringComparer.Ordinal).ToArray());

        // Each of the three is untouched: its name, its website, whether it is suggested, and both
        // instants. The migration skipped the insert; it did not merge into the row instead.
        var aldi = Single(presets, "ALDI");
        Assert.Equal("https://www.aldi.com.au", aldi.Url);
        Assert.True(aldi.IsEnabled);

        var kmart = Single(presets, "kmart");
        Assert.Equal("KMART", kmart.NormalisedName);
        Assert.False(kmart.IsEnabled);
        Assert.Null(kmart.Url);

        var jbMetro = Single(presets, "JB Metro NSW");
        Assert.Equal("https://orders.jbmetro.com.au", jbMetro.Url);
        Assert.True(jbMetro.IsEnabled);

        var instants = await ReadInstantsAsync(connection);
        Assert.Equal($"{PreExistingInstant}|{PreExistingInstant}", instants["ALDI"]);
        Assert.Equal($"{PreExistingInstant}|{PreExistingInstant}", instants["KMART"]);
        Assert.Equal($"{PreExistingInstant}|{PreExistingInstant}", instants["JB METRO NSW"]);

        // And none of them is given the market, tax basis or collection route this migration would
        // have inserted. They take the fail-closed defaults instead: a record somebody else created
        // is not one this migration may declare GST-inclusive or point at a collector on their
        // behalf, so it reads as "no market recorded" until a Super Admin completes it.
        Assert.All(
            new[] { aldi, kmart, jbMetro },
            preset =>
            {
                Assert.False(preset.ServesNationwide);
                Assert.Equal(string.Empty, preset.ServedStates);
                Assert.False(preset.PricesIncludeGst);
                Assert.Null(preset.PriceConnector);
                Assert.Null(preset.PriceConnectorKey);
            });

        // The two records whose names were free arrive in full, so the skip is a skip of one row and
        // not of the insert.
        AssertPreset(
            presets,
            "JB Metro South Coast & ACT",
            nationwide: false,
            states: "NSW,ACT",
            gst: false,
            connector: 4,
            key: null);
        AssertPreset(
            presets,
            "JB Metro SA & NT",
            nationwide: false,
            states: "SA,NT",
            gst: false,
            connector: 4,
            key: null);
    }

    /// <summary>
    /// Reversing the upgrade over that same database removes only the records this migration created
    /// and keeps the three that were already there. A row is recognised by its normalised name
    /// <em>and</em> the fixed instant <c>Up</c> stamps on what it inserts; deleting by name alone
    /// would destroy a preset somebody created themselves, which no rollback can give back.
    /// </summary>
    [Fact]
    public async Task Reversing_the_migration_keeps_the_records_that_already_held_a_seeded_name()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);
        await InsertPreExistingPresetsAsync(connection);
        await MigrateToAsync(db, MasterFieldsMigration);

        await MigrateToAsync(db, PreviousMigration);

        var rows = await QueryAsync(
            connection,
            "SELECT NormalisedName FROM SupplierPresets ORDER BY NormalisedName;",
            reader => reader.GetString(0));

        // The four #848 shipped and the three that were already there. The two records this
        // migration inserted are gone with it.
        Assert.Equal(
            ["ALDI", "BIG W", "COLES", "COSTCO", "JB METRO NSW", "KMART", "WOOLWORTHS"],
            rows);

        // Still in the state somebody put them in, website and enabled flag included.
        var kept = await QueryAsync(
            connection,
            "SELECT NormalisedName || '|' || Name || '|' || COALESCE(Url, '-') || '|' || IsEnabled "
                + "FROM SupplierPresets WHERE NormalisedName IN ('ALDI', 'KMART', 'JB METRO NSW') "
                + "ORDER BY NormalisedName;",
            reader => reader.GetString(0));

        Assert.Equal(
            [
                "ALDI|ALDI|https://www.aldi.com.au|1",
                "JB METRO NSW|JB Metro NSW|https://orders.jbmetro.com.au|1",
                "KMART|kmart|-|0",
            ],
            kept);
    }

    /// <summary>
    /// The instants the five inserted records carry are the fixed literal, stored in the same text
    /// form as the rows <c>AddSupplierPresets</c> inserted before them. That is what makes
    /// <c>Down</c>'s comparison against that literal a comparison between like values, and what keeps
    /// a reader unable to tell a migration-inserted row of one release from the other's.
    /// </summary>
    [Fact]
    public async Task The_inserted_records_carry_the_fixed_literal_instant_in_the_stored_form()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await db.Database.MigrateAsync();

        var instants = await ReadInstantsAsync(connection);

        // What #848's InsertData wrote, unchanged by this migration.
        Assert.Equal("2026-10-23 00:00:00|2026-10-23 00:00:00", instants["COLES"]);

        // What this migration writes, in that same form.
        Assert.All(
            new[] { "ALDI", "KMART", "JB METRO NSW", "JB METRO SOUTH COAST & ACT", "JB METRO SA & NT" },
            normalisedName =>
                Assert.Equal("2026-10-27 00:00:00|2026-10-27 00:00:00", instants[normalisedName]));
    }

    #endregion

    /// <summary>
    /// Reversing the migration takes the columns and the five inserted records with it, and leaves the
    /// four rows that were there before - edits included. The values it set on those four are gone
    /// with the columns they were in, which is what reversing an additive migration means.
    /// </summary>
    [Fact]
    public async Task Reversing_the_migration_drops_the_columns_and_the_inserted_records()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, MasterFieldsMigration);
        await ExecuteAsync(
            connection,
            "UPDATE SupplierPresets SET IsEnabled = 0 WHERE NormalisedName = 'BIG W';");

        await MigrateToAsync(db, PreviousMigration);

        var columns = await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierPresets");
        Assert.All(NewColumns, column => Assert.DoesNotContain(column, columns));

        var names = await QueryAsync(
            connection,
            "SELECT Name FROM SupplierPresets ORDER BY Name;",
            reader => reader.GetString(0));

        Assert.Equal(["Big W", "Coles", "Costco", "Woolworths"], names);

        var disabled = await QueryAsync(
            connection,
            "SELECT Name FROM SupplierPresets WHERE IsEnabled = 0;",
            reader => reader.GetString(0));
        Assert.Equal(["Big W"], disabled);
    }

    /// <summary>
    /// The three presets a Super Admin had already created, in the states they were left in: ALDI
    /// enabled with a website, "kmart" typed in lower case and then disabled, and a JB Metro NSW
    /// record pointed at a different address. They are written with SQL on the <em>previous</em>
    /// schema, which is the point - this is a database the supplier-master columns have not reached
    /// yet, and these rows were created by the #848 surface rather than by any migration.
    /// </summary>
    private static async Task InsertPreExistingPresetsAsync(SqliteConnection connection) =>
        await ExecuteAsync(
            connection,
            "INSERT INTO SupplierPresets (Name, NormalisedName, Url, IsEnabled, CreatedAtUtc, UpdatedAtUtc) "
                + "VALUES "
                + $"('ALDI', 'ALDI', 'https://www.aldi.com.au', 1, '{PreExistingInstant}', '{PreExistingInstant}'), "
                + $"('kmart', 'KMART', NULL, 0, '{PreExistingInstant}', '{PreExistingInstant}'), "
                + "('JB Metro NSW', 'JB METRO NSW', 'https://orders.jbmetro.com.au', 1, "
                + $"'{PreExistingInstant}', '{PreExistingInstant}');");

    private static SeededPreset Single(IReadOnlyList<SeededPreset> presets, string name) =>
        presets.Single(preset => preset.Name == name);

    private static void AssertPreset(
        IReadOnlyList<SeededPreset> presets,
        string name,
        bool nationwide,
        string states,
        bool gst,
        int? connector,
        string? key)
    {
        var preset = Single(presets, name);

        Assert.Equal(nationwide, preset.ServesNationwide);
        Assert.Equal(states, preset.ServedStates);
        Assert.Equal(gst, preset.PricesIncludeGst);
        Assert.Equal(connector, preset.PriceConnector);
        Assert.Equal(key, preset.PriceConnectorKey);
        Assert.True(preset.IsEnabled);
    }

    /// <summary>
    /// Every preset as the schema holds it, ordered by name, read with SQL so the assertions are about
    /// the columns the migration created rather than about the model that maps them.
    /// </summary>
    private static Task<List<SeededPreset>> ReadPresetsAsync(SqliteConnection connection) =>
        QueryAsync(
            connection,
            "SELECT Name, NormalisedName, Url, IsEnabled, ServesNationwide, ServedStates, "
                + "PricesIncludeGst, PriceConnector, PriceConnectorKey FROM SupplierPresets "
                + "ORDER BY Name;",
            reader => new SeededPreset(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4),
                reader.GetString(5),
                reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));

    private static async Task<Dictionary<string, string>> ReadInstantsAsync(SqliteConnection connection)
    {
        var rows = await QueryAsync(
            connection,
            "SELECT NormalisedName, CreatedAtUtc, UpdatedAtUtc FROM SupplierPresets;",
            reader => (Key: reader.GetString(0), Instants: $"{reader.GetString(1)}|{reader.GetString(2)}"));

        return rows.ToDictionary(row => row.Key, row => row.Instants, StringComparer.Ordinal);
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
            rows.Add(read((SqliteDataReader)reader));
        }

        return rows;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed record SeededPreset(
        string Name,
        string NormalisedName,
        string? Url,
        bool IsEnabled,
        bool ServesNationwide,
        string ServedStates,
        bool PricesIncludeGst,
        int? PriceConnector,
        string? PriceConnectorKey);
}
