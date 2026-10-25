using Inventory.Domain.Stock;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddPickListRefillLineMachine</c> (issue #897).
///
/// <para>SQLite cannot add a foreign key to an existing table, so this migration <strong>rebuilds</strong>
/// <c>PickListRefillLines</c> and copies its rows across. A rebuild is exactly the kind of migration
/// that has to be proved rather than asserted: the refills, lines and idempotency keys a business
/// already recorded must come out the other side unchanged, with the new column <c>NULL</c> on every
/// one of them, because a line recorded before this change belongs to a refill whose machine is on the
/// batch row and backfilling it would rewrite what that submission recorded.</para>
///
/// <para>It matters more than usual: Production applies migrations at startup, so the code already
/// running there has to keep working across this one. It writes <c>PickListRefillLines</c> without
/// naming the new column, which a nullable column accepts, and it reads neither it nor anything this
/// migration touches - and the tables historical costing <em>is</em> derived from (<c>Products</c>,
/// <c>StockAdjustments</c>, <c>Machines</c>) are proved byte-identical before and after.</para>
///
/// <para>Relational SQLite rather than InMemory, because a rebuilt table, a foreign key, a unique
/// index and "these rows survived" are relational facts.</para>
/// </summary>
public class AddPickListRefillLineMachineMigrationTests
{
    private const string PreviousMigration = "20261024094143_AddPlatformOptionVisibility";
    private const string MachineColumnMigration = "AddPickListRefillLineMachine";

    private const string RefillsTable = "PickListRefills";
    private const string LinesTable = "PickListRefillLines";
    private const string KeyIndex = "IX_PickListRefills_BusinessId_IdempotencyKey";
    private const string MachineForeignKey = "FK_PickListRefillLines_Machines_BusinessId_MachineId";
    private const string MachineIndex = "IX_PickListRefillLines_BusinessId_MachineId";

    private const string RecordedAtUtc = "2026-10-20 03:04:05";
    private const string CreatedAtUtc = "2026-01-01 03:04:05";
    private const string EffectiveAtUtc = "2026-01-02 03:04:05";

    private const string Key = "a-recorded-submission";

    [Fact]
    public async Task The_migration_adds_the_machine_column_with_its_restricting_foreign_key()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);

        Assert.DoesNotContain("MachineId", await MigrationSchemaProbe.ColumnNamesAsync(connection, LinesTable));

        await MigrateToAsync(db, MachineColumnMigration);

        var columns = await MigrationSchemaProbe.ColumnNamesAsync(connection, LinesTable);
        Assert.Contains("MachineId", columns);

        // Nothing the table already held was dropped or renamed by the rebuild.
        Assert.All(
            new[] { "Id", "BusinessId", "RefillId", "ProductId", "QuantityTaken", "StockAdjustmentId" },
            column => Assert.Contains(column, columns));

        // The same composite key, on the same principal key, restricted on delete, that every other
        // column naming a local machine carries.
        Assert.Contains(
            MachineForeignKey,
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, LinesTable),
            StringComparison.Ordinal);
        Assert.Equal(
            "Machines|BusinessId|BusinessId|RESTRICT",
            await ScalarAsync(
                connection,
                $"""
                SELECT "table" || '|' || "from" || '|' || "to" || '|' || on_delete
                    FROM pragma_foreign_key_list('{LinesTable}')
                    WHERE "table" = 'Machines' AND "from" = 'BusinessId';
                """));
        Assert.Equal(
            "Machines|MachineId|Id|RESTRICT",
            await ScalarAsync(
                connection,
                $"""
                SELECT "table" || '|' || "from" || '|' || "to" || '|' || on_delete
                    FROM pragma_foreign_key_list('{LinesTable}')
                    WHERE "table" = 'Machines' AND "from" = 'MachineId';
                """));

        Assert.Contains(MachineIndex, await MigrationSchemaProbe.IndexNamesAsync(connection, LinesTable));
    }

    /// <summary>
    /// The criterion: a refill, its lines and its idempotency key recorded before this migration come
    /// through the rebuild exactly as they were, with no machine filled in on any line.
    /// </summary>
    [Fact]
    public async Task Existing_refills_lines_and_keys_are_preserved_with_no_machine()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await SeedPreviousSchemaAsync(connection);

        var refillsBefore = await RecordedRefillsAsync(connection);
        var linesBefore = await RecordedLinesAsync(connection);

        await MigrateToAsync(db, MachineColumnMigration);

        Assert.Equal(refillsBefore, await RecordedRefillsAsync(connection));
        Assert.Equal(linesBefore, await RecordedLinesAsync(connection));

        // Nothing was backfilled: the batch's machine stays on the batch.
        Assert.Equal(
            "2",
            await ScalarAsync(connection, $"SELECT COUNT(*) FROM {LinesTable} WHERE MachineId IS NULL;"));
        Assert.Equal(
            "0",
            await ScalarAsync(connection, $"SELECT COUNT(*) FROM {LinesTable} WHERE MachineId IS NOT NULL;"));
    }

    /// <summary>
    /// The index that makes the key idempotent, and makes it idempotent <em>per business</em>, survives
    /// the rebuild of the child table: the database still refuses a second refill under one business's
    /// key, and two businesses may still choose the same string.
    /// </summary>
    [Fact]
    public async Task The_idempotency_key_is_still_unique_per_business_after_the_migration()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await SeedPreviousSchemaAsync(connection);
        await MigrateToAsync(db, MachineColumnMigration);

        Assert.Contains(KeyIndex, await MigrationSchemaProbe.IndexNamesAsync(connection, RefillsTable));

        // Another business may hold the same key.
        await ExecuteAsync(connection, $"""
            INSERT INTO Businesses (Id, Name, Status, CreatedAtUtc, TimeZoneId, SalesProvider)
                VALUES (2, 'Other Co', 20, '{CreatedAtUtc}', 'Australia/Sydney', NULL);
            INSERT INTO {RefillsTable} (BusinessId, IdempotencyKey, RequestFingerprint, RecordedAtUtc)
                VALUES (2, '{Key}', 'f2', '{RecordedAtUtc}');
            """);

        var refused = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"""
            INSERT INTO {RefillsTable} (BusinessId, IdempotencyKey, RequestFingerprint, RecordedAtUtc)
                VALUES (1, '{Key}', 'f3', '{RecordedAtUtc}');
            """));

        Assert.Contains("UNIQUE", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("2", await ScalarAsync(connection, $"SELECT COUNT(*) FROM {RefillsTable};"));

        // And the stored fingerprint column is still as long as the renderings it holds - the
        // multi-machine one included.
        Assert.Equal(64, MultiMachinePickListRefillFingerprint.Compute(null, []).Length);
        Assert.Equal(64, PickListRefillFingerprint.Compute(null, null, []).Length);
    }

    [Fact]
    public async Task The_migration_leaves_the_inventory_tables_exactly_as_they_were()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await SeedPreviousSchemaAsync(connection);

        var before = await SchemaAndRowsAsync(connection);

        await MigrateToAsync(db, MachineColumnMigration);

        Assert.Equal(before, await SchemaAndRowsAsync(connection));
    }

    private static async Task MigrateToAsync(AppDbContext db, string targetMigration) =>
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);

    /// <summary>
    /// Seeded with raw SQL rather than the EF model, because the model already knows about the column
    /// this migration has not added yet.
    /// </summary>
    private static async Task SeedPreviousSchemaAsync(SqliteConnection connection) =>
        await ExecuteAsync(connection, $"""
            INSERT INTO Businesses (Id, Name, Status, CreatedAtUtc, TimeZoneId, SalesProvider)
                VALUES (1, 'Vending Co', 20, '{CreatedAtUtc}', 'Australia/Sydney', 1);
            INSERT INTO Machines (Id, BusinessId, Name, IsActive, ExternalMachineId)
                VALUES (7, 1, 'Machine 42', 1, '42');
            INSERT INTO Products
                (Id, BusinessId, Name, AverageUnitCost, CostingQuantity, InventoryValue,
                 QuantityInStock, LowStockThreshold, RestockTo, IsActive, GstRule, CreatedAt, UpdatedAt)
                VALUES (100, 1, 'Coke Zero', 2.0, 10, 20.0, 4, 0, 0, 1, 0, '{CreatedAtUtc}', '{CreatedAtUtc}');
            INSERT INTO StockAdjustments
                (Id, BusinessId, ProductId, QuantityChange, QuantityAfter, Reason, Source, MachineId,
                 CreatedAt, EffectiveAt)
                VALUES (1, 1, 100, -6, 4, 5, 0, 7, '{CreatedAtUtc}', '{EffectiveAtUtc}');
            INSERT INTO {RefillsTable}
                (Id, BusinessId, IdempotencyKey, RequestFingerprint, MachineId, Notes, RecordedAtUtc)
                VALUES (1, 1, '{Key}', 'f1', 7, 'Friday run', '{RecordedAtUtc}');
            INSERT INTO {LinesTable} (Id, BusinessId, RefillId, ProductId, QuantityTaken, StockAdjustmentId)
                VALUES (1, 1, 1, 100, 4, 1);
            INSERT INTO {LinesTable} (Id, BusinessId, RefillId, ProductId, QuantityTaken, StockAdjustmentId)
                VALUES (2, 1, 1, 100, 2, 1);
            """);

    private static Task<string?> RecordedRefillsAsync(SqliteConnection connection) =>
        ScalarAsync(
            connection,
            $"""
            SELECT group_concat(
                Id || '|' || BusinessId || '|' || IdempotencyKey || '|' || RequestFingerprint || '|' ||
                COALESCE(MachineId, 'null') || '|' || COALESCE(Notes, 'null') || '|' || RecordedAtUtc, ';')
                FROM (SELECT * FROM {RefillsTable} ORDER BY Id);
            """);

    private static Task<string?> RecordedLinesAsync(SqliteConnection connection) =>
        ScalarAsync(
            connection,
            $"""
            SELECT group_concat(
                Id || '|' || BusinessId || '|' || RefillId || '|' || ProductId || '|' ||
                QuantityTaken || '|' || StockAdjustmentId, ';')
                FROM (SELECT * FROM {LinesTable} ORDER BY Id);
            """);

    /// <summary>
    /// The schema and the stored values of every table a refill line sits next to. Comparing one
    /// string before and after is what proves none of them was rebuilt, renamed or rewritten.
    /// </summary>
    private static async Task<string> SchemaAndRowsAsync(SqliteConnection connection)
    {
        var parts = new List<string>();
        foreach (var table in new[] { "Products", "StockAdjustments", "Machines", RefillsTable })
        {
            parts.Add(table);
            parts.AddRange(await MigrationSchemaProbe.ColumnNamesAsync(connection, table));
            parts.AddRange(await MigrationSchemaProbe.IndexNamesAsync(connection, table));
            parts.Add(await MigrationSchemaProbe.CreateTableSqlAsync(connection, table));
        }

        parts.Add(await ScalarAsync(
            connection,
            "SELECT Id || '|' || BusinessId || '|' || QuantityInStock || '|' || CostingQuantity || '|' || "
                + "InventoryValue || '|' || AverageUnitCost FROM Products;") ?? string.Empty);
        parts.Add(await ScalarAsync(
            connection,
            "SELECT Id || '|' || ProductId || '|' || QuantityChange || '|' || QuantityAfter || '|' || "
                + "Reason || '|' || Source || '|' || MachineId || '|' || EffectiveAt FROM StockAdjustments;")
            ?? string.Empty);
        parts.Add(await ScalarAsync(
            connection,
            "SELECT Id || '|' || BusinessId || '|' || Name || '|' || IsActive || '|' || "
                + "COALESCE(ExternalMachineId, 'null') FROM Machines;") ?? string.Empty);

        return string.Join("\n", parts);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync())?.ToString();
    }
}
