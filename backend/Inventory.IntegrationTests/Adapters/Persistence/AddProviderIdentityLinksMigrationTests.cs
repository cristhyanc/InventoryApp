using Inventory.Domain.Providers;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddProductAndCategoryBusinessKeys</c> and <c>AddProviderIdentityLinks</c>
/// (issue #732), run as one step because that is how a deployment applies them.
///
/// Two things have to hold at once. The link tables must arrive backfilled - one Nayax link per
/// existing product and category, carrying that row's own business and an <c>ExternalId</c> that
/// reads back as the id it came from - and the rebuild of <c>Products</c> and <c>Categories</c> the
/// alternate keys force must leave every id, relationship and costing value exactly where it was.
/// The second is the risk in this change: a table rebuild copies rows, and a copy that lost a key
/// would silently detach purchase lines, stock movements and sale costs from their product.
/// </summary>
public class AddProviderIdentityLinksMigrationTests
{
    private const string PreviousMigration = "20261015050929_RemoveBusinessMembershipRoleOwnerDatabaseDefault";
    private const string TargetMigration = "AddProviderIdentityLinks";

    [Fact]
    public async Task Migration_backfills_one_nayax_link_per_row_and_preserves_every_id()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            Assert.DoesNotContain("ProductExternalIds", await MigrationSchemaProbe.TableNamesAsync(connection));
            Assert.DoesNotContain("CategoryExternalIds", await MigrationSchemaProbe.TableNamesAsync(connection));

            // Two businesses, because a backfilled link takes its business from the row it was read
            // from and nothing else: a single-business fixture cannot tell a copied business id from
            // a hard-coded one. Business 2's product also carries the costing and purchase history
            // the rebuild has to bring across intact.
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO Businesses (Id, Name, CreatedAtUtc, TimeZoneId, Status, GstRegistered)
                    VALUES (1, 'Vending A', '2026-01-01 00:00:00', 'Australia/Sydney', 1, NULL),
                           (2, 'Vending B', '2026-01-01 00:00:00', 'Australia/Sydney', 1, NULL);
                INSERT INTO Categories (Id, Name, Description, BusinessId)
                    VALUES (10, 'Snacks', 'Snacks', 1),
                           (11, 'Drinks', NULL, 2);
                INSERT INTO Products (Id, Name, Sku, Description, UnitPrice, AverageUnitCost, QuantityInStock,
                                      LowStockThreshold, RestockTo, IsActive, Unit, CreatedAt, UpdatedAt,
                                      BusinessId, CategoryId, CostingQuantity, InventoryValue, GstRule)
                    VALUES (100, 'Chips', 'CHIP-1', 'Salted', 3.50, 1.10, 12, 2, 20, 1, 'unit',
                            '2026-01-01 00:00:00', '2026-01-02 00:00:00', 1, 10, 12, 13.20, 0),
                           (200, 'Cola', 'COLA-1', NULL, 4.00, 2.00, 5, 1, 10, 1, 'unit',
                            '2026-01-01 00:00:00', '2026-01-02 00:00:00', 2, 11, 5, 10.00, 0);
                INSERT INTO Suppliers (Id, Name, BusinessId, ProductLineGstDefault, DeliveryGstDefault, PackageGstDefault)
                    VALUES (1, 'Wholesaler', 1, 0, 0, 0);
                INSERT INTO Receipts (Id, Title, TotalAmount, PurchaseDate, FileName, StoredFileName, ContentType,
                                      FileSizeBytes, CreatedAt, BusinessId, SupplierId,
                                      DeliveryGstClassification, DeliveryGstClassificationSource,
                                      PackageGstClassification, PackageGstClassificationSource)
                    VALUES (1, 'Weekly restock', 13.20, '2026-01-02 00:00:00', 'scan.jpg', 'abc.jpg',
                            'image/jpeg', 2048, '2026-01-02 00:00:00', 1, 1, 0, 0, 0, 0);
                INSERT INTO ReceiptItems (Id, ReceiptId, ProductId, Quantity, UnitCost, BusinessId,
                                          GstClassification, GstClassificationSource)
                    VALUES (1, 1, 100, 12, 1.10, 1, 0, 0);
                INSERT INTO StockAdjustments (Id, ProductId, ReceiptItemId, QuantityChange, QuantityAfter, Reason,
                                              Source, UnitCost, TotalCost, CostingQuantityAfter, InventoryValueAfter,
                                              AverageUnitCostAfter, CreatedAt, EffectiveAt, BusinessId)
                    VALUES (1, 100, 1, 12, 12, 0, 0, 1.10, 13.20, 12, 13.20, 1.10,
                            '2026-01-02 00:00:00', '2026-01-02 00:00:00', 1);
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            // One link per row, with the row's own business, and nothing else linked.
            var productLinks = await after.ProductExternalIds.OrderBy(link => link.ProductId).ToListAsync();
            Assert.Equal(
                [(1, ExternalProvider.Nayax, "100", 100L), (2, ExternalProvider.Nayax, "200", 200L)],
                productLinks.Select(link => (link.BusinessId, link.Provider, link.ExternalId, link.ProductId)));

            var categoryLinks = await after.CategoryExternalIds.OrderBy(link => link.CategoryId).ToListAsync();
            Assert.Equal(
                [(1, ExternalProvider.Nayax, "10", 10L), (2, ExternalProvider.Nayax, "11", 11L)],
                categoryLinks.Select(link => (link.BusinessId, link.Provider, link.ExternalId, link.CategoryId)));

            // The round trip the later lookups depend on: what the migration wrote with SQLite's
            // CAST is what the shared helper reads back as the Nayax id it came from.
            Assert.All(
                productLinks,
                link =>
                {
                    Assert.True(NayaxExternalId.TryParse(link.ExternalId, out var nayaxId));
                    Assert.Equal(link.ProductId, nayaxId);
                });
            Assert.All(
                categoryLinks,
                link =>
                {
                    Assert.True(NayaxExternalId.TryParse(link.ExternalId, out var nayaxId));
                    Assert.Equal(link.CategoryId, nayaxId);
                });

            // The rebuild moved every row across unchanged: ids, ownership, the catalogue fields and
            // the costing state.
            var products = await after.Products.OrderBy(product => product.Id).ToListAsync();
            Assert.Equal(
                [(100L, 1, "Chips", "CHIP-1", 10L, 12, 13.20m, 1.10m), (200L, 2, "Cola", "COLA-1", 11L, 5, 10.00m, 2.00m)],
                products.Select(product => (
                    product.Id,
                    product.BusinessId,
                    product.Name,
                    product.Sku,
                    product.CategoryId,
                    product.CostingQuantity,
                    product.InventoryValue,
                    product.AverageUnitCost)));

            var categories = await after.Categories.OrderBy(category => category.Id).ToListAsync();
            Assert.Equal(
                [(10L, 1, "Snacks"), (11L, 2, "Drinks")],
                categories.Select(category => (category.Id, category.BusinessId, category.Name)));

            // And every foreign key into the rebuilt table still resolves to the same product.
            var item = await after.ReceiptItems.SingleAsync();
            Assert.Equal((100L, 12m, 1.10m), (item.ProductId, item.Quantity, item.UnitCost));
            var movement = await after.StockAdjustments.SingleAsync();
            Assert.Equal((100L, 12, 1.10m, 13.20m), (movement.ProductId, movement.QuantityChange, movement.UnitCost, movement.TotalCost));
        }
    }

    /// <summary>
    /// The composite foreign key the migration creates is the acceptance criterion that application
    /// validation is explicitly not enough for, so it is proved against the migrated schema with raw
    /// SQL - no <c>AppDbContext</c>, no ownership enforcer, no query filter, nothing that could be
    /// doing the refusing instead of SQLite.
    /// </summary>
    [Fact]
    public async Task The_migrated_schema_refuses_a_link_to_another_businesss_product()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var migrate = TestAppDbContext.Unrestricted(options))
        {
            await migrate.GetService<IMigrator>().MigrateAsync(TargetMigration);
        }

        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO Businesses (Id, Name, CreatedAtUtc, TimeZoneId, Status, GstRegistered)
                    VALUES (1, 'Vending A', '2026-01-01 00:00:00', 'Australia/Sydney', 1, NULL),
                           (2, 'Vending B', '2026-01-01 00:00:00', 'Australia/Sydney', 1, NULL);
                INSERT INTO Categories (Id, Name, Description, BusinessId) VALUES (10, 'Snacks', NULL, 1);
                INSERT INTO Products (Id, Name, UnitPrice, AverageUnitCost, QuantityInStock, LowStockThreshold,
                                      RestockTo, IsActive, CreatedAt, UpdatedAt, BusinessId, GstRule)
                    VALUES (100, 'Chips', 3.50, 1.10, 12, 2, 20, 1,
                            '2026-01-01 00:00:00', '2026-01-01 00:00:00', 1, 0);
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using var crossBusiness = connection.CreateCommand();
        crossBusiness.CommandText = """
            INSERT INTO ProductExternalIds (BusinessId, Provider, ExternalId, ProductId)
                VALUES (2, 1, '100', 100);
            """;

        var refused = await Assert.ThrowsAsync<SqliteException>(() => crossBusiness.ExecuteNonQueryAsync());
        Assert.Equal(SQLitePCL.raw.SQLITE_CONSTRAINT, refused.SqliteErrorCode);

        await using var crossBusinessCategory = connection.CreateCommand();
        crossBusinessCategory.CommandText = """
            INSERT INTO CategoryExternalIds (BusinessId, Provider, ExternalId, CategoryId)
                VALUES (2, 1, '10', 10);
            """;

        var categoryRefused =
            await Assert.ThrowsAsync<SqliteException>(() => crossBusinessCategory.ExecuteNonQueryAsync());
        Assert.Equal(SQLitePCL.raw.SQLITE_CONSTRAINT, categoryRefused.SqliteErrorCode);
    }
}
