using Inventory.Domain.Gst;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddPurchaseGstClassification</c> (issue #429). The migration is additive: it
/// adds a GST classification and provenance column to each purchase line and to each of the
/// purchase's two charges, and does nothing else.
///
/// There is no backfill, by design - the migration must not guess GST from an amount (parent issue
/// #62). Every existing line and charge therefore arrives as <c>Unknown</c>/<c>Unknown</c>, and
/// every pre-existing amount, cost and stock movement survives the upgrade untouched.
/// </summary>
public class AddPurchaseGstClassificationMigrationTests
{
    private const string PreviousMigration = "20261004064601_AddInventoryCostRepairs";
    private const string TargetMigration = "AddPurchaseGstClassification";

    [Fact]
    public async Task Migration_adds_unknown_classifications_and_preserves_existing_purchases()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            Assert.DoesNotContain(
                "GstClassification", await MigrationSchemaProbe.ColumnNamesAsync(connection, "ReceiptItems"));
            Assert.DoesNotContain(
                "DeliveryGstClassification", await MigrationSchemaProbe.ColumnNamesAsync(connection, "Receipts"));

            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO Products (Name, Sku, UnitPrice, AverageUnitCost, QuantityInStock, LowStockThreshold,
                                      RestockTo, IsActive, Unit, CreatedAt, UpdatedAt, BusinessId,
                                      CostingQuantity, InventoryValue)
                    VALUES ('Chips', 'CHIP-1', 3.50, 1.10, 12, 2, 20, 1, 'unit',
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

            Assert.Equal(
                ["GstClassification", "GstClassificationSource"],
                (await MigrationSchemaProbe.ColumnNamesAsync(connection, "ReceiptItems"))
                    .Where(name => name.Contains("Gst", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
            Assert.Equal(
                [
                    "DeliveryGstClassification", "DeliveryGstClassificationSource",
                    "PackageGstClassification", "PackageGstClassificationSource",
                ],
                (await MigrationSchemaProbe.ColumnNamesAsync(connection, "Receipts"))
                    .Where(name => name.Contains("Gst", StringComparison.Ordinal)).Order(StringComparer.Ordinal));

            var purchase = await after.Receipts.Include(r => r.Items).SingleAsync();
            Assert.Equal(GstClassification.Unknown, purchase.DeliveryGstClassification);
            Assert.Equal(GstClassificationSource.Unknown, purchase.DeliveryGstClassificationSource);
            Assert.Equal(GstClassification.Unknown, purchase.PackageGstClassification);
            Assert.Equal(GstClassificationSource.Unknown, purchase.PackageGstClassificationSource);

            var item = Assert.Single(purchase.Items);
            Assert.Equal(GstClassification.Unknown, item.GstClassification);
            Assert.Equal(GstClassificationSource.Unknown, item.GstClassificationSource);

            // Nothing else moved: the purchase's own amounts, the line, and the costing history.
            Assert.Equal((20.90m, 5.00m, 2.70m), (purchase.TotalAmount, purchase.DeliveryCost, purchase.PackageCost));
            Assert.Equal((12m, 1.10m), (item.Quantity, item.UnitCost));
            var product = await after.Products.SingleAsync();
            Assert.Equal(
                (12, 12, 13.20m, 1.10m),
                (product.QuantityInStock, product.CostingQuantity, product.InventoryValue, product.AverageUnitCost));
            var movement = await after.StockAdjustments.SingleAsync();
            Assert.Equal((12, 1.10m, 13.20m, 12), (movement.QuantityChange, movement.UnitCost, movement.TotalCost, movement.CostingQuantityAfter));
        }
    }
}
