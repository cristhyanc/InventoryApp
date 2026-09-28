using InventoryApi.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddNayaxMachineStockEventDuplicateResolution</c> (issue #196): the migration
/// must be purely additive - three new, defaulted/nullable columns and one new nullable foreign key
/// to <c>StockAdjustments</c> - and must not touch, reinterpret, or lose an already imported event.
/// </summary>
public class AddNayaxMachineStockEventDuplicateResolutionMigrationTests
{
    private const string PreviousMigration = "20260928012603_RenameNayaxMachineStockEventToEventLogContract";
    private const string TargetMigration = "20260928053917_AddNayaxMachineStockEventDuplicateResolution";

    private static async Task<List<string>> ColumnNamesAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}');";
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values;
    }

    [Fact]
    public async Task Migration_is_additive_and_preserves_an_already_imported_event()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            var columnsBefore = await ColumnNamesAsync(connection, "NayaxMachineStockEvents");
            Assert.DoesNotContain("DuplicateResolution", columnsBefore);
            Assert.DoesNotContain("DuplicateResolvedAt", columnsBefore);
            Assert.DoesNotContain("MatchedManualStockAdjustmentId", columnsBefore);

            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO NayaxMachineStockEvents (BusinessId, NayaxEventLogId, MachineId, EventCode,
                                                     EventDateTimeGmt, RawEventData, MatchStatus,
                                                     ProcessingStatus, CreatedAt)
                    VALUES (1, 9001, 42, 501, '2026-09-01 10:00:00',
                            'Product MDB: 13 | Chips | 2', 0, 0, '2026-09-01 10:05:00');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            var columnsAfter = await ColumnNamesAsync(connection, "NayaxMachineStockEvents");
            Assert.Contains("DuplicateResolution", columnsAfter);
            Assert.Contains("DuplicateResolvedAt", columnsAfter);
            Assert.Contains("MatchedManualStockAdjustmentId", columnsAfter);

            // Read with raw SQL rather than through the current EF model, so this test stays
            // accurate even after a later migration extends this table further.
            await using var select = connection.CreateCommand();
            select.CommandText = """
                SELECT NayaxEventLogId, RawEventData, DuplicateResolution, DuplicateResolvedAt,
                       MatchedManualStockAdjustmentId
                    FROM NayaxMachineStockEvents;
                """;
            await using var reader = await select.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(9001L, reader.GetInt64(0));
            Assert.Equal("Product MDB: 13 | Chips | 2", reader.GetString(1));
            // An already-imported event defaults to unresolved, with no resolution timestamp or
            // matched manual movement - the migration never invents a resolution for prior history.
            Assert.Equal(0L, reader.GetInt64(2));
            Assert.True(reader.IsDBNull(3));
            Assert.True(reader.IsDBNull(4));
            Assert.False(await reader.ReadAsync());
        }
    }
}
