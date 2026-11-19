using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddNayaxMachineStockEventAppliedReplacement</c> (issue #1326): seven
/// additive, nullable columns, an already imported 501 event and an already imported 510 replacement
/// both kept exactly as they were with no value invented for either, and a clean rollback.
/// </summary>
public class AddNayaxMachineStockEventAppliedReplacementMigrationTests
{
    private const string PreviousMigration = "20261117013345_AddNayaxMachineStockEventOutgoingProductName";
    private const string TargetMigration = "20261118220000_AddNayaxMachineStockEventAppliedReplacement";

    private static readonly string[] AddedColumns =
    [
        "ReplacementIncomingProductId",
        "ReplacementIncomingQuantity",
        "ReplacementOutgoingDisposition",
        "ReplacementOutgoingProductId",
        "ReplacementOutgoingQuantity",
        "ReplacementOutgoingReturnStockAdjustmentId",
        "ReplacementOutgoingWriteOffStockAdjustmentId",
    ];

    [Fact]
    public async Task Migration_is_additive_preserves_the_imported_events_and_rolls_back()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            var columns = await MigrationSchemaProbe.ColumnNamesAsync(connection, "NayaxMachineStockEvents");
            Assert.All(AddedColumns, column => Assert.DoesNotContain(column, columns));

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
                INSERT INTO NayaxMachineStockEvents (BusinessId, NayaxEventLogId, MachineId, EventCode,
                                                     EventDateTimeGmt, RawEventData, ParsedMdb,
                                                     ParsedProductName, ParsedOutgoingProductName,
                                                     MatchStatus, ProcessingStatus, DuplicateResolution,
                                                     CreatedAt)
                    VALUES (1, 9002, 42, 510, '2026-09-02 10:00:00',
                            '[operator] ''has replaced product Water with product Chips | Product MDB: 13',
                            13, 'Chips', 'Water', 0, 0, 0, '2026-09-02 10:05:00');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            var columns = await MigrationSchemaProbe.ColumnNamesAsync(connection, "NayaxMachineStockEvents");
            Assert.All(AddedColumns, column => Assert.Contains(column, columns));

            await using var select = connection.CreateCommand();
            select.CommandText = """
                SELECT NayaxEventLogId, EventCode, ParsedProductName, ParsedOutgoingProductName,
                       ParsedQuantity, ProcessingStatus, StockAdjustmentId,
                       ReplacementIncomingProductId, ReplacementIncomingQuantity,
                       ReplacementOutgoingDisposition, ReplacementOutgoingProductId,
                       ReplacementOutgoingQuantity, ReplacementOutgoingReturnStockAdjustmentId,
                       ReplacementOutgoingWriteOffStockAdjustmentId
                    FROM NayaxMachineStockEvents ORDER BY NayaxEventLogId;
                """;
            await using var reader = await select.ExecuteReaderAsync();

            // The 501 event: unchanged, and nothing about a replacement recorded on it.
            Assert.True(await reader.ReadAsync());
            Assert.Equal(9001L, reader.GetInt64(0));
            Assert.Equal(501L, reader.GetInt64(1));
            Assert.Equal("Chips", reader.GetString(2));
            Assert.True(reader.IsDBNull(3));
            Assert.Equal(2L, reader.GetInt64(4));
            AssertNoReplacementRecorded(reader);

            // The 510 replacement: still pending, still carrying both reported names, and with no
            // applied choice invented for it.
            Assert.True(await reader.ReadAsync());
            Assert.Equal(9002L, reader.GetInt64(0));
            Assert.Equal(510L, reader.GetInt64(1));
            Assert.Equal("Chips", reader.GetString(2));
            Assert.Equal("Water", reader.GetString(3));
            Assert.True(reader.IsDBNull(4));
            AssertNoReplacementRecorded(reader);

            Assert.False(await reader.ReadAsync());
        }

        await using (var rollback = TestAppDbContext.Unrestricted(options))
        {
            await rollback.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            var columns = await MigrationSchemaProbe.ColumnNamesAsync(connection, "NayaxMachineStockEvents");
            Assert.All(AddedColumns, column => Assert.DoesNotContain(column, columns));

            // Both imported events are still there, with the outgoing name of the replacement intact.
            await using var count = connection.CreateCommand();
            count.CommandText = """
                SELECT COUNT(*) FROM NayaxMachineStockEvents
                    WHERE NayaxEventLogId IN (9001, 9002)
                      AND (NayaxEventLogId = 9001 OR ParsedOutgoingProductName = 'Water');
                """;
            Assert.Equal(2L, (long)(await count.ExecuteScalarAsync())!);
        }
    }

    /// <summary>
    /// Columns 5 to 13 of the read above: the processing state and every applied-replacement column.
    /// An existing row is <c>Unprocessed</c> with no movement and no recorded choice, which is
    /// exactly what both kinds of already-imported event mean.
    /// </summary>
    private static void AssertNoReplacementRecorded(SqliteDataReader reader)
    {
        Assert.Equal(0L, reader.GetInt64(5));
        for (var column = 6; column <= 13; column++)
        {
            Assert.True(reader.IsDBNull(column), $"Column {column} was expected to be NULL.");
        }
    }
}
