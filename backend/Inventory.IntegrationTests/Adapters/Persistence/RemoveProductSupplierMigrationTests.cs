using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>RemoveProductSupplier</c> (issue #787, A1 of #786). The migration is
/// <strong>destructive by approval</strong>: it drops <c>Products.SupplierId</c> with its foreign
/// key and its index, and the stored product → supplier associations go with it. The owner approved
/// that deletion on 21 October 2026, so what this test has to prove is the boundary of it.
///
/// <para><strong>Nothing else about a product moves.</strong> Every other column keeps its value,
/// and the table keeps its own key, its category foreign key and the rest of its indexes, including
/// the filtered unique index on the provider's external id. The selling price this test originally
/// asserted alongside them is no longer readable through the EF model: issue #792 dropped
/// <c>Products.UnitPrice</c> in the next migration, and the surviving costing fields stand for the
/// same point.</para>
///
/// <para><strong>Every independent supplier survives.</strong> A purchase's supplier, a supplier
/// order's supplier, the suppliers themselves and the unit costs, quantities and totals recorded
/// against them are a different relationship that this change does not touch, and
/// <c>PRAGMA foreign_key_check</c> is asserted empty afterwards so the rebuild cannot have left a
/// dangling reference behind.</para>
///
/// <para><strong><c>Down</c> recreates an empty column.</strong> It restores the column, its foreign
/// key and its index so the schema is the one the previous release's code compiles against, and
/// every row comes back with <c>NULL</c>: a dropped value is not recoverable, and the migration
/// deliberately invents nothing in its place.</para>
/// </summary>
public class RemoveProductSupplierMigrationTests
{
    /// <summary>The migration immediately before this one: the state <c>Down</c> returns to.</summary>
    private const string PreviousMigration = "20261020095104_AddPickListRefills";

    private const string TargetMigration = "RemoveProductSupplier";

    /// <summary>
    /// A product associated with a supplier, plus the independent purchase/supplier-order data that
    /// records its own supplier and its own costs. Seeded on the pre-migration schema with SQL,
    /// because the EF model no longer has the column the upgrade is about.
    /// </summary>
    private const string Seed = """
        INSERT INTO Suppliers (Id, Name, ContactName, Phone, Email, Address, BusinessId,
                               ProductLineGstDefault, DeliveryGstDefault, PackageGstDefault)
            VALUES (1, 'Wholesale Co', 'Pat', '555', 'pat@wholesale.invalid', '1 Road', 1, 0, 0, 0),
                   (2, 'Second Co', 'Sam', '556', 'sam@second.invalid', '2 Road', 1, 0, 0, 0);
        INSERT INTO Categories (Id, Name, Description, BusinessId) VALUES (3, 'Snacks', 'Seeded', 1);
        INSERT INTO Products (Id, Name, Sku, Description, UnitPrice, AverageUnitCost, CostingQuantity,
                              InventoryValue, QuantityInStock, LowStockThreshold, RestockTo, Unit,
                              IsActive, GstRule, ExternalProductId, CreatedAt, UpdatedAt, BusinessId,
                              CategoryId, SupplierId)
            VALUES (7, 'Chips', 'CHIP-1', 'A bag', 3.50, 1.10, 12, 13.20, 12, 2, 20, 'unit',
                    1, 0, '4321', '2026-01-01 00:00:00', '2026-01-01 00:00:00', 1, 3, 1);
        INSERT INTO Receipts (Id, Title, Notes, TotalAmount, DeliveryCost, PackageCost, PurchaseDate,
                              FileName, StoredFileName, ContentType, FileSizeBytes, CreatedAt,
                              BusinessId, SupplierId, DeliveryGstClassification,
                              DeliveryGstClassificationSource, PackageGstClassification,
                              PackageGstClassificationSource)
            VALUES (10, 'Weekly restock', 'note', 20.90, 5.00, 2.70, '2026-01-02 00:00:00',
                    'scan.jpg', 'abc.jpg', 'image/jpeg', 2048, '2026-01-02 00:00:00', 1, 2, 1, 1, 2, 1);
        INSERT INTO ReceiptItems (Id, ReceiptId, ProductId, Quantity, UnitCost, BusinessId,
                                  GstClassification, GstClassificationSource)
            VALUES (11, 10, 7, 12, 1.10, 1, 1, 1);
        INSERT INTO SupplierOrders (Id, SupplierId, OrderDate, ExpectedDate, Reference, Notes, Status,
                                    CreatedAt, UpdatedAt, BusinessId)
            VALUES (20, 2, '2026-01-03 00:00:00', '2026-01-10 00:00:00', 'PO-1', 'order note', 0,
                    '2026-01-03 00:00:00', '2026-01-03 00:00:00', 1);
        INSERT INTO SupplierOrderLines (Id, SupplierOrderId, ProductId, QuantityOrdered,
                                        QuantityReceived, UnitPrice, Notes, BusinessId)
            VALUES (21, 20, 7, 24, 6, 1.25, 'line note', 1);
        """;

    [Fact]
    public async Task Migration_drops_the_product_supplier_link_and_preserves_every_other_product_field()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeededUpToAsync(connection, PreviousMigration);

        Assert.Contains("SupplierId", await MigrationSchemaProbe.ColumnNamesAsync(connection, "Products"));
        Assert.Contains(
            "IX_Products_SupplierId", await MigrationSchemaProbe.IndexNamesAsync(connection, "Products"));

        await MigrateAsync(options, TargetMigration);

        // The column, its foreign key and its index are gone.
        Assert.DoesNotContain("SupplierId", await MigrationSchemaProbe.ColumnNamesAsync(connection, "Products"));
        Assert.DoesNotContain(
            "IX_Products_SupplierId", await MigrationSchemaProbe.IndexNamesAsync(connection, "Products"));
        Assert.DoesNotContain(
            "Suppliers", await MigrationSchemaProbe.CreateTableSqlAsync(connection, "Products"));

        // The category foreign key and the rest of the table's indexes stayed, including the
        // filtered unique index on the provider's external id (issue #759).
        Assert.Contains(
            "FK_Products_Categories_CategoryId",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "Products"));
        var indexes = await MigrationSchemaProbe.IndexNamesAsync(connection, "Products");
        Assert.Contains("IX_Products_CategoryId", indexes);
        Assert.Contains("IX_Products_Sku", indexes);
        Assert.Contains("IX_Products_BusinessId", indexes);
        Assert.Contains("IX_Products_BusinessId_ExternalProductId", indexes);

        await using var after = TestAppDbContext.Unrestricted(options);

        // Every other product field, the costing ones above all.
        var product = await after.Products.SingleAsync();
        Assert.Equal(
            (7L, "Chips", "CHIP-1", "A bag", 1.10m, 12, 13.20m),
            (product.Id, product.Name, product.Sku, product.Description,
                product.AverageUnitCost, product.CostingQuantity, product.InventoryValue));
        Assert.Equal(
            (12, 2, 20, "unit", true, 3L, "4321"),
            (product.QuantityInStock, product.LowStockThreshold, product.RestockTo, product.Unit,
                product.IsActive, product.CategoryId, product.ExternalProductId));

        // Every supplier the business recorded independently of a product is untouched.
        Assert.Equal(["Second Co", "Wholesale Co"], (await after.Suppliers.ToListAsync())
            .Select(supplier => supplier.Name).Order(StringComparer.Ordinal));

        var purchase = await after.Receipts.Include(receipt => receipt.Items).SingleAsync();
        Assert.Equal((2, 20.90m, 5.00m, 2.70m), (purchase.SupplierId, purchase.TotalAmount,
            purchase.DeliveryCost, purchase.PackageCost));
        var item = Assert.Single(purchase.Items);
        Assert.Equal((7L, 12m, 1.10m), (item.ProductId, item.Quantity, item.UnitCost));

        var order = await after.SupplierOrders.Include(o => o.Lines).SingleAsync();
        Assert.Equal((2, "PO-1"), (order.SupplierId, order.Reference));
        var line = Assert.Single(order.Lines);
        Assert.Equal((7L, 24m, 6m, 1.25m), (line.ProductId, line.QuantityOrdered,
            line.QuantityReceived, line.UnitPrice));

        // The table rebuild left no dangling reference anywhere in the database.
        Assert.Empty(await ForeignKeyViolationsAsync(connection));
    }

    /// <summary>
    /// <c>Down</c> restores the schema the previous release's code compiles against and nothing
    /// more: the column, its foreign key and its index come back, every row's value is <c>NULL</c>,
    /// and the product's own fields and every independent supplier reference are still there.
    /// </summary>
    [Fact]
    public async Task Down_restores_an_empty_column_and_cannot_recover_the_deleted_links()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeededUpToAsync(connection, PreviousMigration);
        await MigrateAsync(options, TargetMigration);

        await MigrateAsync(options, PreviousMigration);

        Assert.Contains("SupplierId", await MigrationSchemaProbe.ColumnNamesAsync(connection, "Products"));
        Assert.Contains(
            "IX_Products_SupplierId", await MigrationSchemaProbe.IndexNamesAsync(connection, "Products"));
        Assert.Contains(
            "FK_Products_Suppliers_SupplierId",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "Products"));

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Products WHERE SupplierId IS NOT NULL;";
        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));

        await using var after = TestAppDbContext.Unrestricted(options);
        var product = await after.Products.SingleAsync();
        Assert.Equal((7L, "Chips", 1.10m, 3L), (product.Id, product.Name, product.AverageUnitCost, product.CategoryId));
        Assert.Equal(2, (await after.Receipts.SingleAsync()).SupplierId);
        Assert.Equal(2, (await after.SupplierOrders.SingleAsync()).SupplierId);
        Assert.Empty(await ForeignKeyViolationsAsync(connection));
    }

    /// <summary>
    /// Opens <paramref name="connection"/>, migrates to <paramref name="target"/> and seeds the
    /// product/purchase/supplier-order rows on that schema.
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
