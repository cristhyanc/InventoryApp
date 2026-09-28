using InventoryApi.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>RenameNayaxMachineStockEventToEventLogContract</c> (issue #183): the columns
/// are renamed to the documented Nayax Get Machine Last Alerts fields (<c>EventLogID</c>,
/// <c>EventDateTimeGMT</c>) and <c>EventDateTimeVMC</c> is added, without losing an already
/// imported event or weakening the per-business idempotency index.
/// </summary>
public class RenameNayaxMachineStockEventToEventLogContractMigrationTests
{
    private const string PreviousMigration = "20260927140121_AddNayaxMachineStockEvents";
    private const string TargetMigration = "20260928012603_RenameNayaxMachineStockEventToEventLogContract";

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
    public async Task Migration_renames_to_event_log_contract_and_preserves_imported_events_and_idempotency()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO NayaxMachineStockEvents (BusinessId, NayaxEventId, MachineId, EventCode, EventTimestamp,
                                                     RawEventData, MatchStatus, ProcessingStatus, CreatedAt)
                    VALUES (1, 9001, 42, 501, '2026-09-01 10:00:00',
                            'Product MDB: 13 | Chips | 2', 0, 0, '2026-09-01 10:05:00');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            var columns = await ColumnNamesAsync(connection, "NayaxMachineStockEvents");
            Assert.Contains("NayaxEventLogId", columns);
            Assert.Contains("EventDateTimeGmt", columns);
            Assert.Contains("EventDateTimeVmc", columns);
            Assert.DoesNotContain("NayaxEventId", columns);
            Assert.DoesNotContain("EventTimestamp", columns);

            // Read with raw SQL rather than through the current EF model, whose columns were
            // extended by the later AddNayaxMachineStockEventDuplicateResolution migration.
            await using var select = connection.CreateCommand();
            select.CommandText = """
                SELECT NayaxEventLogId, EventDateTimeGmt, EventDateTimeVmc, RawEventData
                    FROM NayaxMachineStockEvents;
                """;
            await using var reader = await select.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(9001L, reader.GetInt64(0));
            Assert.Equal(new DateTime(2026, 9, 1, 10, 0, 0), DateTime.Parse(reader.GetString(1)));
            Assert.True(reader.IsDBNull(2));
            Assert.Equal("Product MDB: 13 | Chips | 2", reader.GetString(3));
            Assert.False(await reader.ReadAsync());
            await reader.DisposeAsync();

            // The same EventLogID in the same business is still rejected by the renamed index.
            await using var duplicate = connection.CreateCommand();
            duplicate.CommandText = """
                INSERT INTO NayaxMachineStockEvents (BusinessId, NayaxEventLogId, MachineId, EventCode, EventDateTimeGmt,
                                                     RawEventData, MatchStatus, ProcessingStatus, CreatedAt)
                    VALUES (1, 9001, 42, 501, '2026-09-01 10:00:00', 'x', 0, 0, '2026-09-01 10:05:00');
                """;
            await Assert.ThrowsAsync<SqliteException>(() => duplicate.ExecuteNonQueryAsync());
        }
    }
}
