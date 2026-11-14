using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddProductCostingVersion</c> (issue #1245). The migration is additive: it adds
/// one non-nullable <c>CostingVersion</c> column to <c>Products</c>, defaulting to <c>0</c>, and does
/// nothing else.
///
/// <para>There is no backfill and nothing to backfill from. The column counts the rebuilds of a
/// product's costing position rather than recording a fact about one, so every existing product
/// arriving at <c>0</c> is exactly right: what a drain compares is whether the value changed, never
/// what it is, and the first rebuild after the upgrade moves it off zero.</para>
///
/// <para>Nothing financial may move with it: the column records no quantity, cost or value, so the
/// product's own costing position, its stock movement, the purchase line that produced it and the
/// stored cost of its sale all have to come through the upgrade untouched - and the rest of
/// <c>Products</c>' columns and indexes have to be exactly as they were, because this is the table
/// every movement, purchase line, sale association and costing row hangs off.</para>
/// </summary>
public class AddProductCostingVersionMigrationTests
{
    private const string PreviousMigration = "20261111001106_AddReceiptScanUsage";
    private const string TargetMigration = "AddProductCostingVersion";
    private const string Column = "CostingVersion";

    [Fact]
    public async Task Migration_starts_every_existing_product_at_zero_and_moves_nothing_else()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        List<string> columnsBefore;
        List<string> indexesBefore;
        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            columnsBefore = await MigrationSchemaProbe.ColumnNamesAsync(connection, "Products");
            indexesBefore = await MigrationSchemaProbe.IndexNamesAsync(connection, "Products");
            Assert.DoesNotContain(Column, columnsBefore);

            // One costed product as a trading database holds it: a purchase line, the restock movement
            // it produced, the costing position that movement left, and a sale costed from it.
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO Products (Name, Sku, AverageUnitCost, QuantityInStock, LowStockThreshold,
                                      RestockTo, IsActive, Unit, GstRule, CreatedAt, UpdatedAt, BusinessId,
                                      CostingQuantity, InventoryValue)
                    VALUES ('Chips', 'CHIP-1', 1.10, 12, 2, 20, 1, 'unit', 0,
                            '2026-01-01 00:00:00', '2026-01-01 00:00:00', 1, 12, 13.20);
                INSERT INTO Receipts (Title, TotalAmount, PurchaseDate, CreatedAt, BusinessId)
                    VALUES ('Weekly restock', 13.20, '2026-01-02 00:00:00', '2026-01-02 00:00:00', 1);
                INSERT INTO ReceiptItems (ReceiptId, ProductId, Quantity, UnitCost, BusinessId)
                    VALUES (1, 1, 12, 1.10, 1);
                INSERT INTO StockAdjustments (ProductId, ReceiptItemId, QuantityChange, QuantityAfter, Reason, Source,
                                              UnitCost, TotalCost, CostingQuantityAfter, InventoryValueAfter,
                                              AverageUnitCostAfter, CreatedAt, EffectiveAt, BusinessId)
                    VALUES (1, 1, 12, 12, 0, 0, 1.10, 13.20, 12, 13.20, 1.10,
                            '2026-01-02 00:00:00', '2026-01-02 00:00:00', 1);
                INSERT INTO NayaxSales (BusinessId, TransactionID, TransactionStatusId, MachineID,
                                        ProductId, SettlementValue, MachineAuthorizationTime, Provider,
                                        ProviderProductIdIsRaw, CostingStatus, CostSource,
                                        UnitCostAtSale, CostOfGoodsSold)
                    VALUES (1, 9100000001, 12, 4200001, 1, 2.50, '2026-01-03 00:00:00', 1,
                            1, 1, 1, 1.10, 1.10);
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            // The one column that is new, and every other column still in its place: the upgrade
            // appends rather than rebuilding the table every costing row hangs off.
            Assert.Equal(
                columnsBefore.Append(Column),
                await MigrationSchemaProbe.ColumnNamesAsync(connection, "Products"));
            Assert.Equal(indexesBefore, await MigrationSchemaProbe.IndexNamesAsync(connection, "Products"));

            // The default is the safe one the issue asks for, declared in the schema, so an existing
            // row and an insert that states no version both start at zero.
            Assert.Equal("0", await MigrationSchemaProbe.ColumnDefaultAsync(connection, "Products", Column));

            var product = await after.Products.AsNoTracking().SingleAsync();
            Assert.Equal(0, product.CostingVersion);

            // Nothing financial moved with it.
            Assert.Equal(
                (12, 12, 13.20m, 1.10m),
                (product.QuantityInStock, product.CostingQuantity, product.InventoryValue, product.AverageUnitCost));
            var movement = await after.StockAdjustments.AsNoTracking().SingleAsync();
            Assert.Equal(
                (12, 1.10m, 13.20m, 12),
                (movement.QuantityChange, movement.UnitCost, movement.TotalCost, movement.CostingQuantityAfter));
            var line = await after.ReceiptItems.AsNoTracking().SingleAsync();
            Assert.Equal((12m, 1.10m), (line.Quantity, line.UnitCost));
            var sale = await after.NayaxSales.AsNoTracking().SingleAsync();
            Assert.Equal((1.10m, 1.10m), (sale.UnitCostAtSale, sale.CostOfGoodsSold));
        }
    }

    /// <summary>
    /// The column is usable rather than merely present: a version can be recorded and read back, and a
    /// product inserted without one still arrives at zero.
    /// </summary>
    [Fact]
    public async Task A_costing_version_can_be_recorded_and_left_unset_after_the_upgrade()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await db.GetService<IMigrator>().MigrateAsync(TargetMigration);

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO Products (Name, AverageUnitCost, QuantityInStock, LowStockThreshold, RestockTo,
                                  IsActive, GstRule, CreatedAt, UpdatedAt, BusinessId, CostingVersion)
                VALUES ('Rebuilt twice', 1.00, 1, 0, 0, 1, 0,
                        '2026-01-01 00:00:00', '2026-01-01 00:00:00', 1, 2);
            INSERT INTO Products (Name, AverageUnitCost, QuantityInStock, LowStockThreshold, RestockTo,
                                  IsActive, GstRule, CreatedAt, UpdatedAt, BusinessId)
                VALUES ('Never rebuilt', 1.00, 1, 0, 0, 1, 0,
                        '2026-01-01 00:00:00', '2026-01-01 00:00:00', 1);
            """;
        await insert.ExecuteNonQueryAsync();

        var versions = await db.Products.AsNoTracking()
            .OrderBy(product => product.Id)
            .Select(product => product.CostingVersion)
            .ToListAsync();

        Assert.Equal([2L, 0L], versions);
    }
}
