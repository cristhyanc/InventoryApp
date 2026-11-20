using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddNayaxSalesProductIdIndex</c> (issue #1304): one index on
/// <c>NayaxSales.ProductId</c>, added over a database that already holds sales, and the reason it
/// exists - the read of one product's stock-consuming sales, as the business filter writes it, is a
/// search of that index rather than a scan of every business's sales.
///
/// <para>The central business filter is translated as <c>(@unrestricted OR BusinessId = @business)</c>,
/// and SQLite does not use the composite <c>(BusinessId, ProductId)</c> index under that
/// <c>OR</c>. So before this index every sale-time cost replay and every inventory-cost rebuild read
/// the whole sales table once per product, which is what made each batch of the background sales
/// history import slower than the last as the table grew.</para>
/// </summary>
public class AddNayaxSalesProductIdIndexMigrationTests
{
    private const string PreviousMigration = "AddNayaxSalesHistoryImportJobs";
    private const string IndexMigration = "AddNayaxSalesProductIdIndex";
    private const string IndexName = "IX_NayaxSales_ProductId";

    [Fact]
    public async Task The_migration_adds_the_index_keeps_every_sale_and_rolls_back_cleanly()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
            await MigrateToAsync(before, PreviousMigration);

        Assert.Null(await IndexSqlAsync(connection));
        await ExecuteAsync(
            connection,
            """
            INSERT INTO Businesses (Id, Name, Status, CreatedAtUtc, SupplierPresetsCopiedAtUtc)
            VALUES (1, 'Fictional Vending A', 1, '2026-01-01 00:00:00', NULL);
            INSERT INTO NayaxSales (
                Id, BusinessId, TransactionID, MachineID, MachineAuthorizationTime,
                SettlementValue, CostingStatus, CostSource, Provider, ProviderProductIdIsRaw,
                SqsVerification)
            VALUES (1, 1, 7001, 942488501, '2026-06-01 00:00:00', 3.5, 0, 0, 1, 1, 0);
            """);

        await using (var after = TestAppDbContext.Unrestricted(options))
            await MigrateToAsync(after, IndexMigration);

        Assert.Contains("\"ProductId\"", await IndexSqlAsync(connection));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM NayaxSales WHERE TransactionID = 7001"));

        await using (var rollback = TestAppDbContext.Unrestricted(options))
            await MigrateToAsync(rollback, PreviousMigration);

        Assert.Null(await IndexSqlAsync(connection));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM NayaxSales WHERE TransactionID = 7001"));
    }

    /// <summary>
    /// The product-scoped sales read as the business filter writes it - the read the cost ledger
    /// makes - searches the index rather than scanning the table.
    /// </summary>
    [Fact]
    public async Task One_products_sales_are_read_through_the_index_under_the_business_filter()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var setup = TestAppDbContext.Unrestricted(options))
            await setup.Database.MigrateAsync();

        await using var db = TestAppDbContext.For(options, 1);
        const long productId = 5;
        var query = db.NayaxSales.AsNoTracking()
            .Where(s => s.TransactionStatusId == 12 || s.TransactionStatusId == 62)
            .Where(s => s.ProductId == productId)
            .ToQueryString();

        var plan = await PlanAsync(connection, query);

        Assert.Contains(plan, line => line.Contains(IndexName, StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.StartsWith("SCAN", StringComparison.Ordinal));
    }

    private static async Task<List<string>> PlanAsync(SqliteConnection connection, string queryString)
    {
        var lines = queryString.Split('\n');
        await using var command = connection.CreateCommand();
        foreach (var line in lines.Where(l => l.StartsWith(".param set ", StringComparison.Ordinal)))
        {
            var parts = line.Split(' ', 4);
            command.Parameters.AddWithValue(parts[2], parts[3].Trim().Trim('\''));
        }

        command.CommandText = "EXPLAIN QUERY PLAN "
            + string.Join('\n', lines.Where(l => !l.StartsWith(".param", StringComparison.Ordinal)));

        var plan = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            plan.Add(reader.GetString(3));
        return plan;
    }

    private static async Task MigrateToAsync(AppDbContext db, string targetMigration) =>
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);

    private static async Task<string?> IndexSqlAsync(SqliteConnection connection) =>
        await ScalarAsync(connection, $"SELECT sql FROM sqlite_master WHERE type = 'index' AND name = '{IndexName}'") as string;

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
