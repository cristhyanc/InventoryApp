using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddNayaxMachineStockEvents</c> (issue #183): the migration must be purely
/// additive - a new table plus a defaulted <c>StockAdjustments.Source</c> column - and must not
/// touch, reinterpret, or lose any existing stock-adjustment history.
/// </summary>
public class AddNayaxMachineStockEventsMigrationTests
{
    private const string PreviousMigration = "20260923120151_AddBusinessBackfillAudit";
    private const string TargetMigration = "20260927140121_AddNayaxMachineStockEvents";

    [Fact]
    public async Task Migration_is_additive_and_preserves_existing_stock_adjustment_history()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            Assert.DoesNotContain(
                "Source", await MigrationSchemaProbe.ColumnNamesAsync(connection, "StockAdjustments"));
            Assert.DoesNotContain(
                "NayaxMachineStockEvents", await MigrationSchemaProbe.TableNamesAsync(connection));

            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO Products (Name, Sku, UnitPrice, AverageUnitCost, QuantityInStock, LowStockThreshold,
                                      RestockTo, IsActive, Unit, CreatedAt, UpdatedAt, BusinessId)
                    VALUES ('Chips', 'CHIP-1', 3.50, 1.10, 12, 2, 20, 1, 'unit',
                            '2026-01-01 00:00:00', '2026-01-01 00:00:00', 1);
                INSERT INTO StockAdjustments (ProductId, QuantityChange, QuantityAfter, Reason, MachineId,
                                              CreatedAt, EffectiveAt, BusinessId)
                    VALUES (1, -3, 9, 5, 42, '2026-01-02 00:00:00', '2026-01-02 00:00:00', 1);
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            Assert.Contains("Source", await MigrationSchemaProbe.ColumnNamesAsync(connection, "StockAdjustments"));
            Assert.Contains("NayaxMachineStockEvents", await MigrationSchemaProbe.TableNamesAsync(connection));

            var preserved = await after.StockAdjustments.SingleAsync();
            Assert.Equal(-3, preserved.QuantityChange);
            Assert.Equal(42, preserved.MachineId);
            // Existing rows default to Manual (0): the migration never reinterprets prior history
            // as Nayax-sourced.
            Assert.Equal(Inventory.Infrastructure.Models.StockAdjustmentSource.Manual, preserved.Source);

            // Counted with SQL rather than through the current EF model, whose columns were renamed
            // by the later RenameNayaxMachineStockEventToEventLogContract migration.
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM NayaxMachineStockEvents;";
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }
    }
}
