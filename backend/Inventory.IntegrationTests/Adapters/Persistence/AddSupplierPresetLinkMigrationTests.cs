using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddSupplierPresetLink</c> (issue #934): the two additive columns, the
/// one-time name-based link backfill, and the filtered unique index the backfill makes safe.
///
/// <para><strong>The case the duplicate rule exists for is seeded here deliberately.</strong> One
/// business holds two suppliers that both normalise to <c>COLES</c> - "Coles" and " COLES " - which
/// is exactly the data a unique index over <c>(BusinessId, SupplierPresetId)</c> could not accept if
/// the backfill linked both. The rule leaves both unlinked, so the index is created on a database
/// that holds the duplicates and nothing has to be deleted, unlinked or merged to make it fit. The
/// other business's single "Coles" is linked as normal: the rule is per business, as the index
/// is.</para>
///
/// <para><strong>Nothing else moves.</strong> Every supplier keeps its name, website, contact
/// details and GST defaults, nobody is hidden, no supplier is created or removed, the presets are
/// untouched, and the rows that name a supplier still name it.</para>
/// </summary>
public class AddSupplierPresetLinkMigrationTests
{
    private const string PreviousMigration = "20261027083607_AddSupplierPresetMasterFields";
    private const string TargetMigration = "AddSupplierPresetLink";

    /// <summary>
    /// The state of a database at the moment this migration is applied to it: two businesses, the
    /// platform's own suggestions, and seven suppliers covering every case the backfill decides.
    ///
    /// <para>Written as SQL rather than through the EF model because the model carries the two
    /// columns this schema does not have yet. The preset ids are stated explicitly so the expected
    /// links below are readable; the four <c>AddSupplierPresets</c> seeded are deleted first so this
    /// test states the whole list it is about.</para>
    /// </summary>
    private const string Seed = """
        DELETE FROM SupplierPresets;
        INSERT INTO SupplierPresets (Id, Name, NormalisedName, Url, IsEnabled, CreatedAtUtc, UpdatedAtUtc,
                                     ServesNationwide, ServedStates, PricesIncludeGst, PriceConnector,
                                     PriceConnectorKey)
            VALUES (1, 'Coles', 'COLES', 'https://www.coles.com.au', 1,
                    '2026-10-01 00:00:00', '2026-10-01 00:00:00', 1, '', 1, 1, 'coles');
        INSERT INTO SupplierPresets (Id, Name, NormalisedName, Url, IsEnabled, CreatedAtUtc, UpdatedAtUtc,
                                     ServesNationwide, ServedStates, PricesIncludeGst, PriceConnector,
                                     PriceConnectorKey)
            VALUES (2, 'Woolworths', 'WOOLWORTHS', NULL, 1,
                    '2026-10-01 00:00:00', '2026-10-01 00:00:00', 1, '', 1, 1, 'woolworths');
        -- A disabled preset is still a preset: the backfill links by name and says nothing about
        -- whether the suggestion is still offered.
        INSERT INTO SupplierPresets (Id, Name, NormalisedName, Url, IsEnabled, CreatedAtUtc, UpdatedAtUtc,
                                     ServesNationwide, ServedStates, PricesIncludeGst, PriceConnector,
                                     PriceConnectorKey)
            VALUES (3, 'Costco', 'COSTCO', NULL, 0,
                    '2026-10-01 00:00:00', '2026-10-01 00:00:00', 1, '', 1, NULL, NULL);

        INSERT INTO Businesses (Id, Name, TimeZoneId, Status, ContactEmail, Abn, AppliedAtUtc,
                                DecidedAtUtc, RejectionReason, CreatedAtUtc, SupplierPresetsCopiedAtUtc)
            VALUES (1, 'Vending Co', 'Australia/Sydney', 20, NULL, NULL, NULL, NULL, NULL,
                    '2026-02-10 04:00:00', '2026-02-10 04:00:00');
        INSERT INTO Businesses (Id, Name, TimeZoneId, Status, ContactEmail, Abn, AppliedAtUtc,
                                DecidedAtUtc, RejectionReason, CreatedAtUtc, SupplierPresetsCopiedAtUtc)
            VALUES (2, 'Coastal Vending', 'Australia/Brisbane', 20, NULL, NULL, NULL, NULL, NULL,
                    '2026-09-01 06:00:00', '2026-09-01 06:00:00');

        -- Business 1: two rows that both normalise to COLES, so neither may be linked.
        INSERT INTO Suppliers (Id, Name, ContactName, Phone, Email, Address, Url, BusinessId,
                               ProductLineGstDefault, DeliveryGstDefault, PackageGstDefault)
            VALUES (1, 'Coles', 'Pat', '555', 'pat@coles.invalid', '1 Road', 'https://www.coles.com.au',
                    1, 1, 2, 1);
        INSERT INTO Suppliers (Id, Name, ContactName, Phone, Email, Address, Url, BusinessId,
                               ProductLineGstDefault, DeliveryGstDefault, PackageGstDefault)
            VALUES (2, ' COLES ', NULL, NULL, NULL, NULL, NULL, 1, 0, 0, 0);
        -- An exact match on a different preset: linked.
        INSERT INTO Suppliers (Id, Name, ContactName, Phone, Email, Address, Url, BusinessId,
                               ProductLineGstDefault, DeliveryGstDefault, PackageGstDefault)
            VALUES (3, 'woolworths', NULL, NULL, NULL, NULL, NULL, 1, 0, 0, 0);
        -- No preset of that name at all: left unlinked.
        INSERT INTO Suppliers (Id, Name, ContactName, Phone, Email, Address, Url, BusinessId,
                               ProductLineGstDefault, DeliveryGstDefault, PackageGstDefault)
            VALUES (4, 'Wholesale Co', 'Sam', NULL, NULL, NULL, NULL, 1, 0, 0, 0);
        -- A name that is nearly a preset's, which is not a match.
        INSERT INTO Suppliers (Id, Name, ContactName, Phone, Email, Address, Url, BusinessId,
                               ProductLineGstDefault, DeliveryGstDefault, PackageGstDefault)
            VALUES (5, 'Coles Express', NULL, NULL, NULL, NULL, NULL, 1, 0, 0, 0);

        -- Business 2: one Coles, which is linked - the duplicate rule is per business.
        INSERT INTO Suppliers (Id, Name, ContactName, Phone, Email, Address, Url, BusinessId,
                               ProductLineGstDefault, DeliveryGstDefault, PackageGstDefault)
            VALUES (6, 'Coles', NULL, NULL, NULL, NULL, NULL, 2, 0, 0, 0);
        -- And a match on the disabled preset, which is linked like any other.
        INSERT INTO Suppliers (Id, Name, ContactName, Phone, Email, Address, Url, BusinessId,
                               ProductLineGstDefault, DeliveryGstDefault, PackageGstDefault)
            VALUES (7, 'COSTCO', NULL, NULL, NULL, NULL, NULL, 2, 0, 0, 0);

        INSERT INTO Receipts (Id, Title, Notes, TotalAmount, DeliveryCost, PackageCost, PurchaseDate,
                              FileName, StoredFileName, ContentType, FileSizeBytes, CreatedAt,
                              BusinessId, SupplierId, DeliveryGstClassification,
                              DeliveryGstClassificationSource, PackageGstClassification,
                              PackageGstClassificationSource)
            VALUES (10, 'Weekly restock', 'note', 20.90, 5.00, 2.70, '2026-01-02 00:00:00',
                    'scan.jpg', 'abc.jpg', 'image/jpeg', 2048, '2026-01-02 00:00:00', 1, 1, 1, 1, 2, 1);
        """;

    [Fact]
    public async Task The_backfill_links_an_exact_single_match_and_leaves_every_other_supplier_unlinked()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeededUpToAsync(connection, PreviousMigration);

        var columnsBefore = await MigrationSchemaProbe.ColumnNamesAsync(connection, "Suppliers");
        Assert.DoesNotContain("SupplierPresetId", columnsBefore);
        Assert.DoesNotContain("IsHidden", columnsBefore);

        await MigrateAsync(options, TargetMigration);

        // Exactly the two columns arrived, and nothing else about the table's column list moved.
        Assert.Equal(
            columnsBefore.Append("IsHidden").Append("SupplierPresetId").Order(StringComparer.Ordinal),
            (await MigrationSchemaProbe.ColumnNamesAsync(connection, "Suppliers")).Order(StringComparer.Ordinal));

        // The links, decided by name exactly once. Business 1's two COLES rows are both unlinked,
        // which is the duplicate rule; business 2's single one is linked, which is the same rule
        // applied per business.
        Assert.Equal(
            [
                (1, "Coles", (long?)null),
                (2, " COLES ", null),
                (3, "woolworths", 2L),
                (4, "Wholesale Co", null),
                (5, "Coles Express", null),
                (6, "Coles", 1L),
                (7, "COSTCO", 3L),
            ],
            await LinksAsync(connection));

        // Nobody was hidden: the column arrives false for every row, which is "in use".
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(
            connection, "SELECT COUNT(*) FROM Suppliers WHERE IsHidden <> 0;")));

        // No supplier was created or removed, and every other column came through untouched.
        Assert.Equal(7L, Convert.ToInt64(await ScalarAsync(connection, "SELECT COUNT(*) FROM Suppliers;")));
        Assert.Equal(
            [
                "Coles|Pat|555|pat@coles.invalid|1 Road|https://www.coles.com.au|1|1|2|1",
                " COLES ||||||1|0|0|0",
                "woolworths||||||1|0|0|0",
                "Wholesale Co|Sam|||||1|0|0|0",
                "Coles Express||||||1|0|0|0",
                "Coles||||||2|0|0|0",
                "COSTCO||||||2|0|0|0",
            ],
            await SupplierRowsAsync(connection));

        // The platform's own list is not a thing this migration writes to.
        Assert.Equal(
            ["1|Coles|COLES|https://www.coles.com.au|1", "2|Woolworths|WOOLWORTHS||1", "3|Costco|COSTCO||0"],
            await PresetRowsAsync(connection));

        // And the purchase that names a supplier still names it, with no broken key anywhere.
        Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(
            connection, "SELECT SupplierId FROM Receipts WHERE Id = 10;")));
        Assert.Empty(await ForeignKeyViolationsAsync(connection));
    }

    /// <summary>
    /// The index the backfill exists to make safe: it is created on this very database - the one
    /// holding two same-name suppliers in one business - so the upgrade proves its own
    /// precondition rather than asserting it.
    /// </summary>
    [Fact]
    public async Task The_unique_index_and_the_restricted_key_are_created_on_a_database_holding_duplicates()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeededUpToAsync(connection, PreviousMigration);

        await MigrateAsync(options, TargetMigration);

        Assert.Contains(
            "IX_Suppliers_BusinessId_SupplierPresetId",
            await MigrationSchemaProbe.IndexNamesAsync(connection, "Suppliers"));

        var indexSql = (string)(await ScalarAsync(
            connection,
            "SELECT sql FROM sqlite_master WHERE type = 'index' "
                + "AND name = 'IX_Suppliers_BusinessId_SupplierPresetId';"))!;
        Assert.Contains("UNIQUE", indexSql, StringComparison.Ordinal);
        Assert.Contains("\"SupplierPresetId\" IS NOT NULL", indexSql, StringComparison.Ordinal);

        var tableSql = await MigrationSchemaProbe.CreateTableSqlAsync(connection, "Suppliers");
        Assert.Contains(
            "FOREIGN KEY (\"SupplierPresetId\") REFERENCES \"SupplierPresets\" (\"Id\") ON DELETE RESTRICT",
            tableSql,
            StringComparison.Ordinal);

        // The index is real: a second copy of preset 1 in business 2, which already holds one, is
        // refused by the database rather than by any code.
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO Suppliers (Name, BusinessId, SupplierPresetId, IsHidden,
                                   ProductLineGstDefault, DeliveryGstDefault, PackageGstDefault)
                VALUES ('Coles again', 2, 1, 0, 0, 0, 0);
            """;
        var duplicate = await Assert.ThrowsAsync<SqliteException>(() => insert.ExecuteNonQueryAsync());
        Assert.Contains("UNIQUE", duplicate.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Down</c> takes the links with the columns and leaves every supplier otherwise as it was.
    /// Nothing is restored, because <c>Up</c> never wrote a name, a website or a hidden flag.
    /// </summary>
    [Fact]
    public async Task Down_drops_the_two_columns_and_leaves_every_supplier_alone()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeededUpToAsync(connection, PreviousMigration);
        var columnsBefore = await MigrationSchemaProbe.ColumnNamesAsync(connection, "Suppliers");
        var indexesBefore = await MigrationSchemaProbe.IndexNamesAsync(connection, "Suppliers");
        await MigrateAsync(options, TargetMigration);

        await MigrateAsync(options, PreviousMigration);

        // Compared as a set: SQLite rebuilds the table to drop a column, so the columns come back
        // in model order.
        Assert.Equal(
            columnsBefore.Order(StringComparer.Ordinal),
            (await MigrationSchemaProbe.ColumnNamesAsync(connection, "Suppliers")).Order(StringComparer.Ordinal));
        Assert.Equal(indexesBefore, await MigrationSchemaProbe.IndexNamesAsync(connection, "Suppliers"));

        Assert.Equal(
            [
                "Coles|Pat|555|pat@coles.invalid|1 Road|https://www.coles.com.au|1|1|2|1",
                " COLES ||||||1|0|0|0",
                "woolworths||||||1|0|0|0",
                "Wholesale Co|Sam|||||1|0|0|0",
                "Coles Express||||||1|0|0|0",
                "Coles||||||2|0|0|0",
                "COSTCO||||||2|0|0|0",
            ],
            await SupplierRowsAsync(connection));
        Assert.Equal(3L, Convert.ToInt64(await ScalarAsync(connection, "SELECT COUNT(*) FROM SupplierPresets;")));
        Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(
            connection, "SELECT SupplierId FROM Receipts WHERE Id = 10;")));
        Assert.Empty(await ForeignKeyViolationsAsync(connection));
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

    /// <summary>Each supplier's id, name and link, in id order.</summary>
    private static async Task<List<(long Id, string Name, long? SupplierPresetId)>> LinksAsync(
        SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, SupplierPresetId FROM Suppliers ORDER BY Id;";
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<(long, string, long?)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        }

        return rows;
    }

    /// <summary>
    /// Every column of <c>Suppliers</c> that existed before this migration, pipe-joined in id
    /// order, so "no other column changed" is one comparison rather than ten.
    /// </summary>
    private static Task<List<string>> SupplierRowsAsync(SqliteConnection connection) =>
        JoinedRowsAsync(
            connection,
            """
            SELECT Name || '|' || COALESCE(ContactName, '') || '|' || COALESCE(Phone, '')
                   || '|' || COALESCE(Email, '') || '|' || COALESCE(Address, '')
                   || '|' || COALESCE(Url, '') || '|' || BusinessId
                   || '|' || ProductLineGstDefault || '|' || DeliveryGstDefault
                   || '|' || PackageGstDefault
            FROM Suppliers ORDER BY Id;
            """);

    private static Task<List<string>> PresetRowsAsync(SqliteConnection connection) =>
        JoinedRowsAsync(
            connection,
            """
            SELECT Id || '|' || Name || '|' || NormalisedName || '|' || COALESCE(Url, '')
                   || '|' || IsEnabled
            FROM SupplierPresets ORDER BY Id;
            """);

    private static async Task<List<string>> JoinedRowsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<string>();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows;
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
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
