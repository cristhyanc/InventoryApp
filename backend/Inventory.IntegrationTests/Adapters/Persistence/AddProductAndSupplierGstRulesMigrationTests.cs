using Inventory.Domain.Gst;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddProductAndSupplierGstRules</c> (issue #430). The migration is additive: it
/// adds one GST rule column to <c>Products</c> and three default columns to <c>Suppliers</c>, and
/// does nothing else.
///
/// Every existing product and supplier therefore arrives with no rule configured
/// (<see cref="GstRules.None"/>). That is deliberate and is the whole point of the design: a
/// supplier being GST-registered, or GST appearing on an invoice, is not a configured rule, so the
/// migration must not infer one (parent issue #62). It must also leave the purchase classifications
/// issue #429 persisted exactly where they were - configuring a rule is not classifying anything.
/// </summary>
public class AddProductAndSupplierGstRulesMigrationTests
{
    private const string PreviousMigration = "20261006022402_AddPurchaseGstClassification";
    private const string TargetMigration = "AddProductAndSupplierGstRules";

    [Fact]
    public async Task Migration_adds_unconfigured_rules_and_preserves_products_suppliers_and_purchases()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            Assert.DoesNotContain(
                "GstRule", await MigrationSchemaProbe.ColumnNamesAsync(connection, "Products"));
            Assert.DoesNotContain(
                "ProductLineGstDefault", await MigrationSchemaProbe.ColumnNamesAsync(connection, "Suppliers"));

            // A purchase whose line and charges are already classified, so the upgrade can be shown
            // to leave an existing classification and its provenance untouched.
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO Suppliers (Name, ContactName, Phone, Email, Address, BusinessId)
                    VALUES ('Wholesale Co', 'Pat', '555', 'pat@wholesale.invalid', '1 Road', 1);
                INSERT INTO Categories (Name, Description, BusinessId)
                    VALUES ('Snacks', 'Seeded', 1);
                INSERT INTO Products (Name, Sku, UnitPrice, AverageUnitCost, QuantityInStock, LowStockThreshold,
                                      RestockTo, IsActive, Unit, CreatedAt, UpdatedAt, BusinessId,
                                      CostingQuantity, InventoryValue, SupplierId, CategoryId)
                    VALUES ('Chips', 'CHIP-1', 3.50, 1.10, 12, 2, 20, 1, 'unit',
                            '2026-01-01 00:00:00', '2026-01-01 00:00:00', 1, 12, 13.20, 1, 1);
                INSERT INTO Receipts (Title, Notes, TotalAmount, DeliveryCost, PackageCost, PurchaseDate,
                                      FileName, StoredFileName, ContentType, FileSizeBytes, CreatedAt, BusinessId,
                                      DeliveryGstClassification, DeliveryGstClassificationSource,
                                      PackageGstClassification, PackageGstClassificationSource)
                    VALUES ('Weekly restock', 'note', 20.90, 5.00, 2.70, '2026-01-02 00:00:00',
                            'scan.jpg', 'abc.jpg', 'image/jpeg', 2048, '2026-01-02 00:00:00', 1,
                            1, 1, 2, 1);
                INSERT INTO ReceiptItems (ReceiptId, ProductId, Quantity, UnitCost, BusinessId,
                                          GstClassification, GstClassificationSource)
                    VALUES (1, 1, 12, 1.10, 1, 1, 1);
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            Assert.Contains(
                "GstRule", await MigrationSchemaProbe.ColumnNamesAsync(connection, "Products"));
            Assert.Equal(
                ["DeliveryGstDefault", "PackageGstDefault", "ProductLineGstDefault"],
                (await MigrationSchemaProbe.ColumnNamesAsync(connection, "Suppliers"))
                    .Where(name => name.Contains("Gst", StringComparison.Ordinal)).Order(StringComparer.Ordinal));

            // No rule is inferred for any existing row, and nothing is backfilled.
            var product = await after.Products.SingleAsync();
            Assert.Equal(GstRules.None, product.GstRule);
            var supplier = await after.Suppliers.SingleAsync();
            Assert.Equal(GstRules.None, supplier.ProductLineGstDefault);
            Assert.Equal(GstRules.None, supplier.DeliveryGstDefault);
            Assert.Equal(GstRules.None, supplier.PackageGstDefault);

            // The product's and the supplier's own data survived unchanged.
            Assert.Equal(
                ("Chips", "CHIP-1", 3.50m, 1.10m, 12, 12, 13.20m),
                (product.Name, product.Sku, product.UnitPrice, product.AverageUnitCost,
                    product.QuantityInStock, product.CostingQuantity, product.InventoryValue));
            Assert.Equal(("Wholesale Co", "Pat", "1 Road"), (supplier.Name, supplier.ContactName, supplier.Address));

            // The purchase classifications issue #429 persisted are exactly where they were: the
            // rule columns are configuration and reclassify nothing.
            var purchase = await after.Receipts.Include(r => r.Items).SingleAsync();
            Assert.Equal(GstClassification.Taxable, purchase.DeliveryGstClassification);
            Assert.Equal(GstClassificationSource.Manual, purchase.DeliveryGstClassificationSource);
            Assert.Equal(GstClassification.GstFree, purchase.PackageGstClassification);
            Assert.Equal(GstClassificationSource.Manual, purchase.PackageGstClassificationSource);
            var item = Assert.Single(purchase.Items);
            Assert.Equal(GstClassification.Taxable, item.GstClassification);
            Assert.Equal(GstClassificationSource.Manual, item.GstClassificationSource);
            Assert.Equal((12m, 1.10m), (item.Quantity, item.UnitCost));
        }
    }
}
