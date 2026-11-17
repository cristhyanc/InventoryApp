using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddNayaxMachineStockEventOutgoingProductName</c> (issue #1325): one
/// additive, nullable column, an already imported 501 event kept exactly as it was with no value
/// invented for it, and a clean rollback.
/// </summary>
public class AddNayaxMachineStockEventOutgoingProductNameMigrationTests
{
    private const string PreviousMigration = "20261116120000_AddPurchaseIdempotencyKey";
    private const string TargetMigration = "20261117013345_AddNayaxMachineStockEventOutgoingProductName";

    [Fact]
    public async Task Migration_is_additive_preserves_an_imported_501_event_and_rolls_back()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            Assert.DoesNotContain(
                "ParsedOutgoingProductName",
                await MigrationSchemaProbe.ColumnNamesAsync(connection, "NayaxMachineStockEvents"));

            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO Businesses (Id, Name, Status, CreatedAtUtc)
                    VALUES (1, 'Vending A', 1, '2026-09-01 00:00:00');
                INSERT INTO NayaxMachineStockEvents (BusinessId, NayaxEventLogId, MachineId, EventCode,
                                                     EventDateTimeGmt, RawEventData, ParsedMdb,
                                                     ParsedProductName, ParsedQuantity, MatchStatus,
                                                     ProcessingStatus, DuplicateResolution, CreatedAt)
                    VALUES (1, 9001, 42, 501, '2026-09-01 10:00:00',
                            'Product MDB: 13 | Chips | 2', 13, 'Chips', 2, 0, 0, 0, '2026-09-01 10:05:00');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            Assert.Contains(
                "ParsedOutgoingProductName",
                await MigrationSchemaProbe.ColumnNamesAsync(connection, "NayaxMachineStockEvents"));

            await using var select = connection.CreateCommand();
            select.CommandText = """
                SELECT NayaxEventLogId, EventCode, RawEventData, ParsedMdb, ParsedProductName,
                       ParsedQuantity, ParsedOutgoingProductName
                    FROM NayaxMachineStockEvents;
                """;
            await using var reader = await select.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(9001L, reader.GetInt64(0));
            Assert.Equal(501L, reader.GetInt64(1));
            Assert.Equal("Product MDB: 13 | Chips | 2", reader.GetString(2));
            Assert.Equal(13L, reader.GetInt64(3));
            Assert.Equal("Chips", reader.GetString(4));
            Assert.Equal(2L, reader.GetInt64(5));
            Assert.True(reader.IsDBNull(6));
            Assert.False(await reader.ReadAsync());
        }

        await using (var rollback = TestAppDbContext.Unrestricted(options))
        {
            await rollback.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            Assert.DoesNotContain(
                "ParsedOutgoingProductName",
                await MigrationSchemaProbe.ColumnNamesAsync(connection, "NayaxMachineStockEvents"));

            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM NayaxMachineStockEvents WHERE NayaxEventLogId = 9001;";
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
        }
    }
}
