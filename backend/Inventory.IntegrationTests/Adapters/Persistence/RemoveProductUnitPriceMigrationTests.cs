using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>RemoveProductUnitPrice</c> (issue #792, A2 of #786). The migration is
/// <strong>destructive by approval</strong>: it drops <c>Products.UnitPrice</c>, the catalogue
/// default selling price, and the stored prices go with it. The owner approved that deletion on
/// 21 October 2026, so what this test has to prove is the boundary of it.
///
/// <para><strong>Every cost stays a cost.</strong> A product's own <c>AverageUnitCost</c>,
/// <c>CostingQuantity</c> and <c>InventoryValue</c>, a purchase item's <c>UnitCost</c>, a supplier
/// order line's <c>UnitPrice</c> (the agreed order price, a different column on a different table)
/// and a recorded sale's amounts are untouched, and <c>PRAGMA foreign_key_check</c> is asserted
/// empty afterwards so the table rebuild cannot have left a dangling reference behind.</para>
///
/// <para><strong>The catalogue review's own column is deliberately kept.</strong>
/// <c>ProviderCatalogueReviewItems.ProviderUnitPrice</c> holds what a provider said at the moment a
/// review was read; it is historical evidence rather than a product field, nothing consumes it any
/// more, and this migration neither drops nor backfills it.</para>
///
/// <para><strong><c>Down</c> recreates an empty column.</strong> It restores the column so the
/// schema is the one the previous release's code compiles against, and every row comes back at the
/// column's <c>0</c> default: a dropped price is not recoverable, and the migration deliberately
/// invents nothing in its place.</para>
/// </summary>
public class RemoveProductUnitPriceMigrationTests
{
    /// <summary>The migration immediately before this one: the state <c>Down</c> returns to.</summary>
    private const string PreviousMigration = "20261021024051_RemoveProductSupplier";

    private const string TargetMigration = "RemoveProductUnitPrice";

    /// <summary>
    /// A priced product with dependent purchase, supplier-order, stock and sale rows, so the drop is
    /// exercised over a table rebuild that has real references pointing at it. Seeded on the
    /// pre-migration schema with SQL, because the EF model no longer has the column the upgrade is
    /// about.
    /// </summary>
    private const string Seed = """
        INSERT INTO Suppliers (Id, Name, ContactName, Phone, Email, Address, BusinessId,
                               ProductLineGstDefault, DeliveryGstDefault, PackageGstDefault)
            VALUES (1, 'Wholesale Co', 'Pat', '555', 'pat@wholesale.invalid', '1 Road', 1, 0, 0, 0);
        INSERT INTO Categories (Id, Name, Description, BusinessId) VALUES (3, 'Snacks', 'Seeded', 1);
        INSERT INTO Products (Id, Name, Sku, Description, UnitPrice, AverageUnitCost, CostingQuantity,
                              InventoryValue, QuantityInStock, LowStockThreshold, RestockTo, Unit,
                              IsActive, GstRule, ExternalProductId, CreatedAt, UpdatedAt, BusinessId,
                              CategoryId)
            VALUES (7, 'Chips', 'CHIP-1', 'A bag', 3.50, 1.10, 12, 13.20, 12, 2, 20, 'unit',
                    1, 0, '4321', '2026-01-01 00:00:00', '2026-01-01 00:00:00', 1, 3);
        INSERT INTO Receipts (Id, Title, Notes, TotalAmount, DeliveryCost, PackageCost, PurchaseDate,
                              FileName, StoredFileName, ContentType, FileSizeBytes, CreatedAt,
                              BusinessId, SupplierId, DeliveryGstClassification,
                              DeliveryGstClassificationSource, PackageGstClassification,
                              PackageGstClassificationSource)
            VALUES (10, 'Weekly restock', 'note', 20.90, 5.00, 2.70, '2026-01-02 00:00:00',
                    'scan.jpg', 'abc.jpg', 'image/jpeg', 2048, '2026-01-02 00:00:00', 1, 1, 1, 1, 2, 1);
        INSERT INTO ReceiptItems (Id, ReceiptId, ProductId, Quantity, UnitCost, BusinessId,
                                  GstClassification, GstClassificationSource)
            VALUES (11, 10, 7, 12, 1.10, 1, 1, 1);
        INSERT INTO SupplierOrders (Id, SupplierId, OrderDate, ExpectedDate, Reference, Notes, Status,
                                    CreatedAt, UpdatedAt, BusinessId)
            VALUES (20, 1, '2026-01-03 00:00:00', '2026-01-10 00:00:00', 'PO-1', 'order note', 0,
                    '2026-01-03 00:00:00', '2026-01-03 00:00:00', 1);
        INSERT INTO SupplierOrderLines (Id, SupplierOrderId, ProductId, QuantityOrdered,
                                        QuantityReceived, UnitPrice, Notes, BusinessId)
            VALUES (21, 20, 7, 24, 6, 1.25, 'line note', 1);
        INSERT INTO StockAdjustments (Id, ProductId, QuantityChange, QuantityAfter, Reason, Source,
                                      UnitCost, TotalCost, ReceiptItemId, CreatedAt, EffectiveAt,
                                      BusinessId)
            VALUES (30, 7, 12, 12, 0, 0, 1.10, 13.20, 11, '2026-01-02 00:00:00',
                    '2026-01-02 00:00:00', 1);
        INSERT INTO NayaxSales (Id, TransactionID, MachineID, MachineName, ProductName, SettlementValue,
                                PaymentMethod, MachineAuthorizationTime, TransactionStatusId,
                                NayaxProductId, Provider, ProviderProductIdIsRaw, ProductId,
                                UnitCostAtSale, CostOfGoodsSold, CostingStatus, CostSource, BusinessId)
            VALUES (40, 900001, 55, 'Machine 55', 'Chips', 3.50, 'Credit', '2026-01-04 00:00:00',
                    12, 4321, 1, 1, 7, 1.10, 1.10, 1, 1, 1);
        INSERT INTO ProviderCatalogueReviews (Id, Provider, StartedAtUtc, BusinessId)
            VALUES (50, 1, '2026-01-05 00:00:00', 1);
        INSERT INTO ProviderCatalogueReviewItems (Id, ReviewId, Kind, ExternalId, ProviderName,
                                                  ProviderUnitPrice, LinkedProductId,
                                                  UnresolvedSaleCount, BusinessId)
            VALUES (51, 50, 1, '4321', 'Chips', '4.25', 7, 0, 1);
        """;

    [Fact]
    public async Task Migration_drops_the_product_selling_price_and_preserves_every_cost_and_dependent_row()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeededUpToAsync(connection, PreviousMigration);

        Assert.Contains("UnitPrice", await MigrationSchemaProbe.ColumnNamesAsync(connection, "Products"));

        await MigrateAsync(options, TargetMigration);

        // The column is gone, and only from Products.
        Assert.DoesNotContain("UnitPrice", await MigrationSchemaProbe.ColumnNamesAsync(connection, "Products"));
        Assert.Contains(
            "UnitPrice", await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierOrderLines"));

        // The category foreign key and every index survived the rebuild, including the filtered
        // unique index on the provider's external id (issue #759).
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
            (product.Id, product.Name, product.Sku, product.Description, product.AverageUnitCost,
                product.CostingQuantity, product.InventoryValue));
        Assert.Equal(
            (12, 2, 20, "unit", true, 3L, "4321"),
            (product.QuantityInStock, product.LowStockThreshold, product.RestockTo, product.Unit,
                product.IsActive, product.CategoryId, product.ExternalProductId));

        // The purchase's own costs, the supplier order's agreed price and the stock movement.
        var purchase = await after.Receipts.Include(receipt => receipt.Items).SingleAsync();
        Assert.Equal((1, 20.90m, 5.00m, 2.70m), (purchase.SupplierId, purchase.TotalAmount,
            purchase.DeliveryCost, purchase.PackageCost));
        var item = Assert.Single(purchase.Items);
        Assert.Equal((7L, 12m, 1.10m), (item.ProductId, item.Quantity, item.UnitCost));

        // The supplier order's agreed price is read with SQL rather than through the EF model: this
        // test stops at an older schema, and the model this project compiles against has moved on
        // since (issue #974 added three nullable columns to SupplierOrders).
        Assert.Equal(
            "7|24|6|1.25",
            (await ScalarAsync(connection, """
                SELECT ProductId || '|' || QuantityOrdered || '|' || QuantityReceived || '|' || UnitPrice
                FROM SupplierOrderLines;
                """))?.ToString());

        var movement = await after.StockAdjustments.SingleAsync();
        Assert.Equal((7L, 12, 1.10m, 13.20m), (movement.ProductId, movement.QuantityChange,
            movement.UnitCost, movement.TotalCost));

        // The recorded sale keeps its amount, its product and its historical cost.
        var sale = await after.NayaxSales.SingleAsync();
        Assert.Equal((3.50m, 7L, 1.10m, 1.10m),
            (sale.SettlementValue, sale.ProductId, sale.UnitCostAtSale, sale.CostOfGoodsSold));

        // The review's own provider-price column and its stored value are deliberately kept
        // (issue #792's 21 October scope clarification): historical evidence, not a product field.
        Assert.Contains(
            "ProviderUnitPrice",
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "ProviderCatalogueReviewItems"));
        Assert.Equal("4.25", await ScalarAsync(
            connection, "SELECT ProviderUnitPrice FROM ProviderCatalogueReviewItems WHERE Id = 51;"));

        // The table rebuild left no dangling reference anywhere in the database.
        Assert.Empty(await ForeignKeyViolationsAsync(connection));
    }

    /// <summary>
    /// <c>Down</c> restores the schema the previous release's code compiles against and nothing
    /// more: the column comes back at its <c>0</c> default on every row, because a dropped price is
    /// not recoverable and the migration invents nothing in its place.
    /// </summary>
    [Fact]
    public async Task Down_restores_an_empty_column_and_cannot_recover_the_deleted_prices()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeededUpToAsync(connection, PreviousMigration);
        await MigrateAsync(options, TargetMigration);

        await MigrateAsync(options, PreviousMigration);

        Assert.Contains("UnitPrice", await MigrationSchemaProbe.ColumnNamesAsync(connection, "Products"));
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(
            connection, "SELECT COUNT(*) FROM Products WHERE UnitPrice <> 0;")));

        await using var after = TestAppDbContext.Unrestricted(options);
        var product = await after.Products.SingleAsync();
        Assert.Equal((7L, "Chips", 1.10m, 3L),
            (product.Id, product.Name, product.AverageUnitCost, product.CategoryId));
        Assert.Equal("1.25", (await ScalarAsync(connection, "SELECT UnitPrice FROM SupplierOrderLines;"))?.ToString());
        Assert.Equal(1.10m, (await after.Receipts.Include(r => r.Items).SingleAsync()).Items.Single().UnitCost);
        Assert.Empty(await ForeignKeyViolationsAsync(connection));
    }

    /// <summary>
    /// Opens <paramref name="connection"/>, migrates to <paramref name="target"/> and seeds the
    /// product and its dependent rows on that schema.
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
