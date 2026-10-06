using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddInventoryCostRepairs</c> (issue #359): the migration adds one table and
/// nothing else. There is no backfill - existing costing history, sale costs and transition
/// baselines are unchanged until an operator explicitly applies a repair - so the upgrade must
/// leave every prior row exactly as it was and start with no repairs at all.
/// </summary>
public class AddInventoryCostRepairsMigrationTests
{
    private const string PreviousMigration = "20260928053917_AddNayaxMachineStockEventDuplicateResolution";
    private const string TargetMigration = "AddInventoryCostRepairs";

    [Fact]
    public async Task Migration_adds_only_the_repair_table_and_preserves_existing_costing_history()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            Assert.DoesNotContain("InventoryCostRepairs", await MigrationSchemaProbe.TableNamesAsync(connection));

            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO Products (Name, Sku, UnitPrice, AverageUnitCost, QuantityInStock, LowStockThreshold,
                                      RestockTo, IsActive, Unit, CreatedAt, UpdatedAt, BusinessId,
                                      CostingQuantity, InventoryValue)
                    VALUES ('Chips', 'CHIP-1', 3.50, 1.10, 12, 2, 20, 1, 'unit',
                            '2026-01-01 00:00:00', '2026-01-01 00:00:00', 1, 12, 13.20);
                INSERT INTO StockAdjustments (ProductId, QuantityChange, QuantityAfter, Reason, Source, UnitCost,
                                              TotalCost, CostingQuantityAfter, InventoryValueAfter,
                                              AverageUnitCostAfter, CreatedAt, EffectiveAt, BusinessId)
                    VALUES (1, 12, 12, 0, 0, 1.10, 13.20, 12, 13.20, 1.10,
                            '2026-01-02 00:00:00', '2026-01-02 00:00:00', 1);
                INSERT INTO InventoryCostTransitionBaselines (ProductId, CutoffAt, HomeStockQuantity,
                                              MachineStockQuantity, OpeningCostingQuantity, AverageUnitCost,
                                              InventoryValue, CostSource, LegacyReplayedPhysicalQuantity,
                                              LegacyPhysicalDiscrepancy, DataQualityNote, CreatedAt, BusinessId)
                    VALUES (1, '2026-01-01 00:00:00', 12, 0, 12, 1.10, 13.20, 1, 12, 0, 'note',
                            '2026-01-01 00:00:00', 1);
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            Assert.Contains("InventoryCostRepairs", await MigrationSchemaProbe.TableNamesAsync(connection));
            Assert.Equal(
                [
                    "BusinessId", "CreatedAt", "CreatedByDirectoryTenantId", "CreatedByObjectId", "EffectiveAt",
                    "Id", "ProductId", "Quantity", "Reason", "TotalValue", "UnitCost",
                ],
                (await MigrationSchemaProbe.ColumnNamesAsync(connection, "InventoryCostRepairs"))
                    .Order(StringComparer.Ordinal));
            Assert.Empty(await after.InventoryCostRepairs.ToListAsync());

            var product = await after.Products.SingleAsync();
            Assert.Equal((12, 12, 13.20m, 1.10m), (product.QuantityInStock, product.CostingQuantity, product.InventoryValue, product.AverageUnitCost));
            var movement = await after.StockAdjustments.SingleAsync();
            Assert.Equal((12, 1.10m, 13.20m, 12), (movement.QuantityChange, movement.UnitCost, movement.TotalCost, movement.CostingQuantityAfter));
            var baseline = await after.InventoryCostTransitionBaselines.SingleAsync();
            Assert.Equal((12, 13.20m), (baseline.OpeningCostingQuantity, baseline.InventoryValue));
        }
    }
}
