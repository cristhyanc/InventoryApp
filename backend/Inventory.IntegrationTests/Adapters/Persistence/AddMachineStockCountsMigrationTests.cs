using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddMachineStockCounts</c> (issue #56).
///
/// The migration lands beside the inventory tables historical costing is derived from, so "purely
/// additive" has to be proved rather than asserted: the new table arrives with the columns the model
/// describes and its tenant-scoped indexes, the <c>StockAdjustments</c>, <c>NayaxSales</c> and
/// <c>Products</c> tables keep every column, index and value they had, and nothing is backfilled -
/// a machine whose stock nobody has counted must come out of the migration with no count, because
/// inventing one would invent an opening machine-stock baseline the operator never recorded.
///
/// Relational SQLite rather than InMemory, because a created table, an index and "no existing table
/// was rebuilt" are relational facts.
/// </summary>
public class AddMachineStockCountsMigrationTests
{
    private const string PreviousMigration = "20261013070841_AddSupportSessions";
    private const string CountsMigration = "AddMachineStockCounts";

    private const string CountsTable = "MachineStockCounts";
    private const string CountsIndex = "IX_MachineStockCounts_BusinessId_MachineId_ProductId_CountedAtUtc";

    private const string CreatedAtUtc = "2026-01-01 03:04:05";
    private const string EffectiveAtUtc = "2026-01-02 03:04:05";

    [Fact]
    public async Task The_migration_creates_the_count_table_with_its_tenant_scoped_indexes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        Assert.DoesNotContain(CountsTable, await MigrationSchemaProbe.TableNamesAsync(connection));

        await MigrateToAsync(db, CountsMigration);

        Assert.Contains(CountsTable, await MigrationSchemaProbe.TableNamesAsync(connection));
        Assert.Equal(
            new[]
            {
                "Id",
                "BusinessId",
                "MachineId",
                "ProductId",
                "CountedQuantity",
                "CountedAtUtc",
                "Notes",
            },
            await MigrationSchemaProbe.ColumnNamesAsync(connection, CountsTable));
        var indexes = await MigrationSchemaProbe.IndexNamesAsync(connection, CountsTable);
        Assert.Contains("IX_MachineStockCounts_BusinessId", indexes);
        Assert.Contains(CountsIndex, indexes);

        // A count is a repeatable observation, not a per-product record: the machine/product index
        // must not be unique, or a second count would be refused by the schema.
        Assert.Equal("0", await ScalarAsync(
            connection,
            $"SELECT \"unique\" FROM pragma_index_list('{CountsTable}') WHERE name = '{CountsIndex}';"));
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

        await MigrateToAsync(db, CountsMigration);

        Assert.Equal(before, await SchemaAndRowsAsync(connection));
    }

    [Fact]
    public async Task The_migration_counts_nothing_for_an_existing_machine_refill()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(db, PreviousMigration);
        await SeedPreviousSchemaAsync(connection);

        await MigrateToAsync(db, CountsMigration);

        Assert.Equal("0", await ScalarAsync(connection, $"SELECT COUNT(*) FROM {CountsTable};"));
    }

    private static async Task MigrateToAsync(AppDbContext db, string targetMigration) =>
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);

    /// <summary>
    /// Seeded with raw SQL rather than the EF model, because the model already knows about a table
    /// the older schema has not created yet.
    /// </summary>
    private static async Task SeedPreviousSchemaAsync(SqliteConnection connection) =>
        await ExecuteAsync(connection, $"""
            INSERT INTO Businesses (Id, Name, Status, CreatedAtUtc, TimeZoneId)
                VALUES (1, 'Vending Co', 20, '{CreatedAtUtc}', 'Australia/Sydney');
            INSERT INTO Products
                (Id, BusinessId, Name, UnitPrice, AverageUnitCost, CostingQuantity, InventoryValue,
                 QuantityInStock, LowStockThreshold, RestockTo, IsActive, GstRule, CreatedAt, UpdatedAt)
                VALUES (100, 1, 'Coke Zero', 3.0, 2.0, 10, 20.0, 4, 0, 0, 1, 0, '{CreatedAtUtc}', '{CreatedAtUtc}');
            INSERT INTO StockAdjustments
                (Id, BusinessId, ProductId, QuantityChange, QuantityAfter, Reason, Source, MachineId,
                 CreatedAt, EffectiveAt)
                VALUES (1, 1, 100, -6, 4, 5, 0, 42, '{CreatedAtUtc}', '{EffectiveAtUtc}');
            INSERT INTO NayaxSales
                (Id, BusinessId, TransactionID, TransactionStatusId, MachineID, NayaxProductId,
                 ProductName, SettlementValue, MachineAuthorizationTime, CostingStatus, CostSource)
                VALUES (1, 1, 5001, 12, 42, 100, 'Coke Zero', 3.0, '{EffectiveAtUtc}', 0, 0);
            """);

    /// <summary>
    /// The schema and the stored values of every table a machine-stock count sits next to. Comparing
    /// one string before and after is what proves nothing was rebuilt, renamed or rewritten.
    /// </summary>
    private static async Task<string> SchemaAndRowsAsync(SqliteConnection connection)
    {
        var parts = new List<string>();
        foreach (var table in new[] { "Products", "StockAdjustments", "NayaxSales" })
        {
            parts.Add(table);
            parts.AddRange(await MigrationSchemaProbe.ColumnNamesAsync(connection, table));
            parts.AddRange(await MigrationSchemaProbe.IndexNamesAsync(connection, table));
        }

        parts.Add(await ScalarAsync(
            connection,
            "SELECT Id || '|' || BusinessId || '|' || QuantityInStock || '|' || CostingQuantity || '|' || "
                + "InventoryValue || '|' || AverageUnitCost FROM Products;") ?? string.Empty);
        parts.Add(await ScalarAsync(
            connection,
            "SELECT Id || '|' || ProductId || '|' || QuantityChange || '|' || QuantityAfter || '|' || "
                + "Reason || '|' || MachineId || '|' || EffectiveAt FROM StockAdjustments;") ?? string.Empty);
        parts.Add(await ScalarAsync(
            connection,
            "SELECT TransactionID || '|' || TransactionStatusId || '|' || MachineID || '|' || "
                + "COALESCE(UnitCostAtSale, 'null') || '|' || COALESCE(CostOfGoodsSold, 'null') || '|' || "
                + "CostingStatus FROM NayaxSales;") ?? string.Empty);

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
