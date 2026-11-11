using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddPurchaseInvoiceNumber</c> (issue #1162). The migration is additive: it adds
/// one nullable <c>InvoiceNumber</c> column to <c>Receipts</c> and does nothing else.
///
/// <para>There is no backfill, and there is nothing to backfill from: a purchase recorded before this
/// migration holds no supplier document number anywhere in this database, so every existing purchase
/// arrives with <c>NULL</c> - which is exactly what "this purchase records no invoice number" means.
/// Deriving one from the title, the notes or an uploaded document's name would state a supplier's
/// invoice number nobody entered.</para>
///
/// <para>Nothing else about the purchase may move either: the invoice number is a reference to
/// paperwork, so the purchase's own amounts, its line, its restock movement and its product's costing
/// position all have to come through the upgrade untouched - and the rest of the table's columns and
/// indexes, including issue #976's all-or-nothing document check constraint, have to be exactly as
/// they were.</para>
/// </summary>
public class AddPurchaseInvoiceNumberMigrationTests
{
    private const string PreviousMigration = "20261109050848_AddReimbursementImportJobs";
    private const string TargetMigration = "AddPurchaseInvoiceNumber";
    private const string Column = "InvoiceNumber";

    [Fact]
    public async Task Migration_adds_an_empty_invoice_number_and_preserves_every_purchase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        List<string> columnsBefore;
        List<string> indexesBefore;
        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            columnsBefore = await MigrationSchemaProbe.ColumnNamesAsync(connection, "Receipts");
            indexesBefore = await MigrationSchemaProbe.IndexNamesAsync(connection, "Receipts");
            Assert.DoesNotContain(Column, columnsBefore);

            // One purchase as a trading database holds it: a line, its restock movement and the
            // product's costing position that movement produced.
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO Products (Name, Sku, AverageUnitCost, QuantityInStock, LowStockThreshold,
                                      RestockTo, IsActive, Unit, GstRule, CreatedAt, UpdatedAt, BusinessId,
                                      CostingQuantity, InventoryValue)
                    VALUES ('Chips', 'CHIP-1', 1.10, 12, 2, 20, 1, 'unit', 0,
                            '2026-01-01 00:00:00', '2026-01-01 00:00:00', 1, 12, 13.20);
                INSERT INTO Receipts (Title, Notes, TotalAmount, DeliveryCost, PackageCost, PurchaseDate,
                                      FileName, StoredFileName, ContentType, FileSizeBytes, CreatedAt, BusinessId)
                    VALUES ('Weekly restock', 'note', 20.90, 5.00, 2.70, '2026-01-02 00:00:00',
                            'scan.jpg', 'abc.jpg', 'image/jpeg', 2048, '2026-01-02 00:00:00', 1);
                INSERT INTO ReceiptItems (ReceiptId, ProductId, Quantity, UnitCost, BusinessId)
                    VALUES (1, 1, 12, 1.10, 1);
                INSERT INTO StockAdjustments (ProductId, ReceiptItemId, QuantityChange, QuantityAfter, Reason, Source,
                                              UnitCost, TotalCost, CostingQuantityAfter, InventoryValueAfter,
                                              AverageUnitCostAfter, CreatedAt, EffectiveAt, BusinessId)
                    VALUES (1, 1, 12, 12, 0, 0, 1.10, 13.20, 12, 13.20, 1.10,
                            '2026-01-02 00:00:00', '2026-01-02 00:00:00', 1);
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            // The one column that is new, and every other column still in its place: the upgrade
            // appends rather than rebuilding the table every purchase line hangs off.
            Assert.Equal(
                columnsBefore.Append(Column),
                await MigrationSchemaProbe.ColumnNamesAsync(connection, "Receipts"));
            Assert.Equal(indexesBefore, await MigrationSchemaProbe.IndexNamesAsync(connection, "Receipts"));
            Assert.Null(await MigrationSchemaProbe.ColumnDefaultAsync(connection, "Receipts", Column));

            // Issue #976's all-or-nothing document rule is untouched by this upgrade.
            Assert.Contains(
                "CK_Receipts_DocumentMetadataAllOrNothing",
                await MigrationSchemaProbe.CreateTableSqlAsync(connection, "Receipts"));

            var purchase = await after.Receipts.Include(receipt => receipt.Items).SingleAsync();
            Assert.Null(purchase.InvoiceNumber);

            // Nothing else moved: the purchase's own fields, its document, its line, and the costing
            // history that line produced.
            Assert.Equal(
                ("Weekly restock", "note", 20.90m, 5.00m, 2.70m),
                (purchase.Title, purchase.Notes, purchase.TotalAmount, purchase.DeliveryCost, purchase.PackageCost));
            Assert.Equal(
                ("scan.jpg", "abc.jpg", "image/jpeg", 2048L),
                (purchase.FileName, purchase.StoredFileName, purchase.ContentType, purchase.FileSizeBytes));
            var item = Assert.Single(purchase.Items);
            Assert.Equal((12m, 1.10m), (item.Quantity, item.UnitCost));
            var product = await after.Products
                .Select(candidate => new
                {
                    candidate.QuantityInStock,
                    candidate.CostingQuantity,
                    candidate.InventoryValue,
                    candidate.AverageUnitCost,
                })
                .SingleAsync();
            Assert.Equal(
                (12, 12, 13.20m, 1.10m),
                (product.QuantityInStock, product.CostingQuantity, product.InventoryValue, product.AverageUnitCost));
            var movement = await after.StockAdjustments.SingleAsync();
            Assert.Equal(
                (12, 1.10m, 13.20m, 12),
                (movement.QuantityChange, movement.UnitCost, movement.TotalCost, movement.CostingQuantityAfter));
        }
    }

    /// <summary>
    /// A purchase recorded after the upgrade keeps the number it was given, and one that records none
    /// stays <c>NULL</c> - so the column is usable rather than merely present.
    /// </summary>
    [Fact]
    public async Task An_invoice_number_can_be_recorded_and_left_unset_after_the_upgrade()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await db.GetService<IMigrator>().MigrateAsync(TargetMigration);

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO Receipts (Title, PurchaseDate, CreatedAt, BusinessId, InvoiceNumber)
                VALUES ('With a number', '2026-01-02 00:00:00', '2026-01-02 00:00:00', 1, 'INV-123');
            INSERT INTO Receipts (Title, PurchaseDate, CreatedAt, BusinessId)
                VALUES ('With none', '2026-01-03 00:00:00', '2026-01-03 00:00:00', 1);
            """;
        await insert.ExecuteNonQueryAsync();

        var purchases = await db.Receipts.AsNoTracking()
            .OrderBy(receipt => receipt.Id)
            .Select(receipt => receipt.InvoiceNumber)
            .ToListAsync();

        Assert.Equal(["INV-123", null], purchases);
    }
}
