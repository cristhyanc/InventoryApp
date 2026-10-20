using Inventory.Domain.Stock;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddPickListRefills</c> (issue #743).
///
/// <para>The migration lands beside the inventory tables historical costing is derived from, so
/// "purely additive" has to be proved rather than asserted: the two new tables arrive with the
/// columns the model describes and the unique index that makes an idempotency key mean something,
/// the <c>Products</c>, <c>StockAdjustments</c> and <c>Machines</c> tables keep every column, index
/// and value they had, and nothing is backfilled - the refills that already exist in
/// <c>StockAdjustments</c> were not recorded by any submission, so inventing a batch for them would
/// invent an idempotency key nobody sent.</para>
///
/// <para>It matters more than usual that no existing table is rebuilt: Production applies migrations
/// at startup, so the code already running there has to keep working across this one. It reads
/// neither new table, and this test is what shows the tables it does read are untouched.</para>
///
/// <para>Relational SQLite rather than InMemory, because a created table, a unique index and "no
/// existing table was rebuilt" are relational facts.</para>
/// </summary>
public class AddPickListRefillsMigrationTests
{
    private const string PreviousMigration = "20261020081912_AddStockMachineForeignKeys";
    private const string RefillsMigration = "AddPickListRefills";

    private const string RefillsTable = "PickListRefills";
    private const string LinesTable = "PickListRefillLines";
    private const string KeyIndex = "IX_PickListRefills_BusinessId_IdempotencyKey";

    private const string CreatedAtUtc = "2026-01-01 03:04:05";
    private const string EffectiveAtUtc = "2026-01-02 03:04:05";

    [Fact]
    public async Task The_migration_creates_both_tables_with_the_columns_the_model_describes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        var before = await MigrationSchemaProbe.TableNamesAsync(connection);
        Assert.DoesNotContain(RefillsTable, before);
        Assert.DoesNotContain(LinesTable, before);

        await MigrateToAsync(db, RefillsMigration);

        var after = await MigrationSchemaProbe.TableNamesAsync(connection);
        Assert.Contains(RefillsTable, after);
        Assert.Contains(LinesTable, after);

        Assert.Equal(
            new[]
            {
                "Id",
                "BusinessId",
                "IdempotencyKey",
                "RequestFingerprint",
                "MachineId",
                "Notes",
                "RecordedAtUtc",
            },
            await MigrationSchemaProbe.ColumnNamesAsync(connection, RefillsTable));

        Assert.Equal(
            new[]
            {
                "Id",
                "BusinessId",
                "RefillId",
                "ProductId",
                "QuantityTaken",
                "StockAdjustmentId",
            },
            await MigrationSchemaProbe.ColumnNamesAsync(connection, LinesTable));

        Assert.Contains("IX_PickListRefills_BusinessId", await MigrationSchemaProbe.IndexNamesAsync(connection, RefillsTable));
        Assert.Contains("IX_PickListRefillLines_BusinessId", await MigrationSchemaProbe.IndexNamesAsync(connection, LinesTable));
    }

    /// <summary>
    /// The index that makes the key idempotent, and makes it idempotent <em>per business</em>: the
    /// database refuses a second refill under one business's key, and two businesses may choose the
    /// same string.
    /// </summary>
    [Fact]
    public async Task The_idempotency_key_is_unique_per_business_in_the_schema_itself()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, RefillsMigration);

        Assert.Contains(KeyIndex, await MigrationSchemaProbe.IndexNamesAsync(connection, RefillsTable));
        Assert.Equal("1", await ScalarAsync(
            connection,
            $"SELECT \"unique\" FROM pragma_index_list('{RefillsTable}') WHERE name = '{KeyIndex}';"));

        await ExecuteAsync(connection, $"""
            INSERT INTO {RefillsTable} (BusinessId, IdempotencyKey, RequestFingerprint, RecordedAtUtc)
                VALUES (1, 'one-key', 'f1', '{CreatedAtUtc}');
            INSERT INTO {RefillsTable} (BusinessId, IdempotencyKey, RequestFingerprint, RecordedAtUtc)
                VALUES (2, 'one-key', 'f2', '{CreatedAtUtc}');
            """);

        var refused = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"""
            INSERT INTO {RefillsTable} (BusinessId, IdempotencyKey, RequestFingerprint, RecordedAtUtc)
                VALUES (1, 'one-key', 'f3', '{CreatedAtUtc}');
            """));

        Assert.Contains("UNIQUE", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("2", await ScalarAsync(connection, $"SELECT COUNT(*) FROM {RefillsTable};"));
    }

    /// <summary>
    /// The stored column is as long as the key the contract accepts and the fingerprint the Domain
    /// renders, so the two cannot drift apart silently.
    /// </summary>
    [Fact]
    public async Task The_key_and_fingerprint_columns_match_the_policy_and_the_rendering()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, RefillsMigration);

        var schema = await ScalarAsync(
            connection,
            $"SELECT sql FROM sqlite_master WHERE type = 'table' AND name = '{RefillsTable}';");

        Assert.Contains(
            $"\"IdempotencyKey\" TEXT NOT NULL",
            schema,
            StringComparison.Ordinal);
        Assert.Equal(128, PickListRefillPolicy.MaximumIdempotencyKeyLength);
        Assert.Equal(500, PickListRefillPolicy.MaximumNotesLength);
        Assert.Equal(
            64,
            PickListRefillFingerprint.Compute(null, null, []).Length);
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

        await MigrateToAsync(db, RefillsMigration);

        Assert.Equal(before, await SchemaAndRowsAsync(connection));
    }

    /// <summary>
    /// A refill already in the audit trail was not recorded by any submission, so the migration
    /// must leave it with no batch: a backfilled one would claim an idempotency key nobody sent and
    /// make a later retry of a real submission replay somebody else's pick.
    /// </summary>
    [Fact]
    public async Task The_migration_records_no_batch_for_an_existing_machine_refill()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await SeedPreviousSchemaAsync(connection);

        await MigrateToAsync(db, RefillsMigration);

        Assert.Equal("1", await ScalarAsync(
            connection, "SELECT COUNT(*) FROM StockAdjustments WHERE Reason = 5;"));
        Assert.Equal("0", await ScalarAsync(connection, $"SELECT COUNT(*) FROM {RefillsTable};"));
        Assert.Equal("0", await ScalarAsync(connection, $"SELECT COUNT(*) FROM {LinesTable};"));
    }

    private static async Task MigrateToAsync(AppDbContext db, string targetMigration) =>
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);

    /// <summary>
    /// Seeded with raw SQL rather than the EF model, because the model already knows about tables
    /// the older schema has not created yet.
    /// </summary>
    private static async Task SeedPreviousSchemaAsync(SqliteConnection connection) =>
        await ExecuteAsync(connection, $"""
            INSERT INTO Businesses (Id, Name, Status, CreatedAtUtc, TimeZoneId, SalesProvider)
                VALUES (1, 'Vending Co', 20, '{CreatedAtUtc}', 'Australia/Sydney', 1);
            INSERT INTO Machines (Id, BusinessId, Name, IsActive, ExternalMachineId)
                VALUES (7, 1, 'Machine 42', 1, '42');
            INSERT INTO Products
                (Id, BusinessId, Name, UnitPrice, AverageUnitCost, CostingQuantity, InventoryValue,
                 QuantityInStock, LowStockThreshold, RestockTo, IsActive, GstRule, CreatedAt, UpdatedAt)
                VALUES (100, 1, 'Coke Zero', 3.0, 2.0, 10, 20.0, 4, 0, 0, 1, 0, '{CreatedAtUtc}', '{CreatedAtUtc}');
            INSERT INTO StockAdjustments
                (Id, BusinessId, ProductId, QuantityChange, QuantityAfter, Reason, Source, MachineId,
                 CreatedAt, EffectiveAt)
                VALUES (1, 1, 100, -6, 4, 5, 0, 7, '{CreatedAtUtc}', '{EffectiveAtUtc}');
            """);

    /// <summary>
    /// The schema and the stored values of every table a batch refill sits next to. Comparing one
    /// string before and after is what proves nothing was rebuilt, renamed or rewritten.
    /// </summary>
    private static async Task<string> SchemaAndRowsAsync(SqliteConnection connection)
    {
        var parts = new List<string>();
        foreach (var table in new[] { "Products", "StockAdjustments", "Machines" })
        {
            parts.Add(table);
            parts.AddRange(await MigrationSchemaProbe.ColumnNamesAsync(connection, table));
            parts.AddRange(await MigrationSchemaProbe.IndexNamesAsync(connection, table));
            parts.Add(await ScalarAsync(
                connection,
                $"SELECT sql FROM sqlite_master WHERE type = 'table' AND name = '{table}';") ?? string.Empty);
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
