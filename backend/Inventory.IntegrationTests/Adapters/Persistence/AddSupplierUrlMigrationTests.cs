using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddSupplierUrl</c> (issue #847). The migration is purely additive: one
/// nullable <c>Url</c> column on <c>Suppliers</c>, no backfill, no table rebuild, and no other
/// table touched. What this test proves is that boundary in both directions.
///
/// <para><strong>Every existing supplier keeps every value and gets <c>NULL</c>.</strong> A
/// supplier recorded before the upgrade has no website, and the upgrade must say so rather than
/// invent one from the contact email or anything else.</para>
///
/// <para><strong><c>Down</c> drops only that column.</strong> The suppliers, their GST defaults,
/// the purchase and the supplier order that name them all survive it.</para>
/// </summary>
public class AddSupplierUrlMigrationTests
{
    /// <summary>The migration immediately before this one: the state <c>Down</c> returns to.</summary>
    private const string PreviousMigration = "20261021063528_RemoveProductUnitPrice";

    private const string TargetMigration = "AddSupplierUrl";

    /// <summary>
    /// Two suppliers with dependent purchase and supplier-order rows, seeded on the pre-migration
    /// schema with SQL because the EF model now carries a column that schema does not have.
    /// </summary>
    private const string Seed = """
        INSERT INTO Suppliers (Id, Name, ContactName, Phone, Email, Address, BusinessId,
                               ProductLineGstDefault, DeliveryGstDefault, PackageGstDefault)
            VALUES (1, 'Wholesale Co', 'Pat', '555', 'pat@wholesale.invalid', '1 Road', 1, 1, 2, 1);
        INSERT INTO Suppliers (Id, Name, ContactName, Phone, Email, Address, BusinessId,
                               ProductLineGstDefault, DeliveryGstDefault, PackageGstDefault)
            VALUES (2, 'Other Business Co', NULL, NULL, NULL, NULL, 2, 0, 0, 0);
        INSERT INTO Receipts (Id, Title, Notes, TotalAmount, DeliveryCost, PackageCost, PurchaseDate,
                              FileName, StoredFileName, ContentType, FileSizeBytes, CreatedAt,
                              BusinessId, SupplierId, DeliveryGstClassification,
                              DeliveryGstClassificationSource, PackageGstClassification,
                              PackageGstClassificationSource)
            VALUES (10, 'Weekly restock', 'note', 20.90, 5.00, 2.70, '2026-01-02 00:00:00',
                    'scan.jpg', 'abc.jpg', 'image/jpeg', 2048, '2026-01-02 00:00:00', 1, 1, 1, 1, 2, 1);
        INSERT INTO SupplierOrders (Id, SupplierId, OrderDate, ExpectedDate, Reference, Notes, Status,
                                    CreatedAt, UpdatedAt, BusinessId)
            VALUES (20, 1, '2026-01-03 00:00:00', '2026-01-10 00:00:00', 'PO-1', 'order note', 0,
                    '2026-01-03 00:00:00', '2026-01-03 00:00:00', 1);
        """;

    [Fact]
    public async Task Migration_adds_only_the_nullable_url_column_and_leaves_every_stored_value_alone()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeededUpToAsync(connection, PreviousMigration);

        var columnsBefore = await MigrationSchemaProbe.ColumnNamesAsync(connection, "Suppliers");
        Assert.DoesNotContain("Url", columnsBefore);
        var indexesBefore = await MigrationSchemaProbe.IndexNamesAsync(connection, "Suppliers");
        var tablesBefore = await MigrationSchemaProbe.TableNamesAsync(connection);

        await MigrateAsync(options, TargetMigration);

        // Exactly one column appeared, on exactly one table, and the indexes and the table list
        // are untouched.
        Assert.Equal(
            columnsBefore.Append("Url"),
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "Suppliers"));
        Assert.Equal(indexesBefore, await MigrationSchemaProbe.IndexNamesAsync(connection, "Suppliers"));
        Assert.Equal(tablesBefore, await MigrationSchemaProbe.TableNamesAsync(connection));

        // It is nullable with no default, so an existing supplier reads as "no website" rather
        // than as an empty string.
        Assert.Null(await MigrationSchemaProbe.ColumnDefaultAsync(connection, "Suppliers", "Url"));
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(
            connection, "SELECT COUNT(*) FROM Suppliers WHERE Url IS NOT NULL;")));

        await using var after = TestAppDbContext.Unrestricted(options);

        var suppliers = await after.Suppliers.OrderBy(supplier => supplier.Id).ToListAsync();
        Assert.Equal(2, suppliers.Count);
        Assert.Equal(
            ("Wholesale Co", "Pat", "555", "pat@wholesale.invalid", "1 Road", (string?)null),
            (suppliers[0].Name, suppliers[0].ContactName, suppliers[0].Phone, suppliers[0].Email,
                suppliers[0].Address, suppliers[0].Url));
        Assert.Equal(
            (1, Inventory.Domain.Gst.GstClassification.Taxable,
                Inventory.Domain.Gst.GstClassification.GstFree,
                Inventory.Domain.Gst.GstClassification.Taxable),
            (suppliers[0].BusinessId, suppliers[0].ProductLineGstDefault,
                suppliers[0].DeliveryGstDefault, suppliers[0].PackageGstDefault));
        Assert.Equal((2, "Other Business Co", (string?)null), (suppliers[1].BusinessId, suppliers[1].Name, suppliers[1].Url));

        // The rows that name a supplier still do.
        Assert.Equal(1, (await after.Receipts.SingleAsync()).SupplierId);
        Assert.Equal(1, (await after.SupplierOrders.SingleAsync()).SupplierId);
        Assert.Empty(await ForeignKeyViolationsAsync(connection));
    }

    /// <summary>
    /// A website recorded after the upgrade is stored as given, and <c>Down</c> drops that column
    /// and nothing else - every supplier, GST default and dependent row survives it.
    /// </summary>
    [Fact]
    public async Task Down_drops_only_the_url_column()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeededUpToAsync(connection, PreviousMigration);
        var columnsBefore = await MigrationSchemaProbe.ColumnNamesAsync(connection, "Suppliers");
        var indexesBefore = await MigrationSchemaProbe.IndexNamesAsync(connection, "Suppliers");
        await MigrateAsync(options, TargetMigration);

        await using (var write = TestAppDbContext.Unrestricted(options))
        {
            (await write.Suppliers.SingleAsync(supplier => supplier.Id == 1)).Url = "https://wholesale.invalid";
            await write.SaveChangesAsync();
        }

        Assert.Equal("https://wholesale.invalid", await ScalarAsync(
            connection, "SELECT Url FROM Suppliers WHERE Id = 1;"));

        await MigrateAsync(options, PreviousMigration);

        // Compared as a set: SQLite cannot drop a column in place, so EF rebuilds the table and
        // the columns come back in model order. The same columns are there, and only Url is gone.
        Assert.Equal(
            columnsBefore.Order(StringComparer.Ordinal),
            (await MigrationSchemaProbe.ColumnNamesAsync(connection, "Suppliers")).Order(StringComparer.Ordinal));
        Assert.Equal(indexesBefore, await MigrationSchemaProbe.IndexNamesAsync(connection, "Suppliers"));

        // Read with SQL rather than through the context: the EF model carries Url, which the
        // schema Down returns to does not have. Everything the table rebuild carried over is here.
        Assert.Equal("Wholesale Co", await ScalarAsync(connection, "SELECT Name FROM Suppliers WHERE Id = 1;"));
        Assert.Equal("Pat", await ScalarAsync(connection, "SELECT ContactName FROM Suppliers WHERE Id = 1;"));
        Assert.Equal("pat@wholesale.invalid", await ScalarAsync(connection, "SELECT Email FROM Suppliers WHERE Id = 1;"));
        Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(
            connection, "SELECT ProductLineGstDefault FROM Suppliers WHERE Id = 1;")));
        Assert.Equal(2L, Convert.ToInt64(await ScalarAsync(connection, "SELECT COUNT(*) FROM Suppliers;")));
        Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(connection, "SELECT SupplierId FROM Receipts WHERE Id = 10;")));
        Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(
            connection, "SELECT SupplierId FROM SupplierOrders WHERE Id = 20;")));
        Assert.Empty(await ForeignKeyViolationsAsync(connection));
    }

    /// <summary>
    /// Opens <paramref name="connection"/>, migrates to <paramref name="target"/> and seeds the
    /// suppliers and their dependent rows on that schema.
    /// </summary>
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
