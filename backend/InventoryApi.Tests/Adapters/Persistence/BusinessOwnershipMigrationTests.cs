using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test across both tenancy migrations (issue #64):
/// <c>AddBusinessOwnershipModel</c> (checkpoint 1, the Business/BusinessMembership tables) and
/// <c>AddBusinessOwnershipToTenantOwnedEntities</c> (checkpoint 2, the BusinessId column on every
/// tenant-owned table).
///
/// Both must be purely additive. Neither assigns an owner to an existing record: that backfill is
/// a separate, human-reviewed change, and this test is what keeps the two apart. A migration that
/// quietly handed existing financial history to a business would fail here.
/// </summary>
public class BusinessOwnershipMigrationTests
{
    private const string PreviousMigration = "20260915064737_AddProductRestockTo";
    private const string TenancyTablesMigration = "20260923110030_AddBusinessOwnershipModel";
    private const string OwnershipColumnMigration = "20260923111712_AddBusinessOwnershipToTenantOwnedEntities";
    private const string ScopedUniquenessMigration = "20260923114130_ScopeUniqueConstraintsByBusiness";

    private static async Task MigrateToAsync(AppDbContext db, string targetMigration) =>
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);

    private static async Task<List<string>> TableNamesAsync(SqliteConnection connection) =>
        await QueryStringsAsync(connection, "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;");

    private static async Task<List<string>> ColumnNamesAsync(SqliteConnection connection, string table) =>
        await QueryStringsAsync(connection, $"SELECT name FROM pragma_table_info('{table}');");

    private static async Task<List<string>> QueryStringsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();

        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Seeds at the pre-tenancy schema with raw SQL rather than the EF model, because the current
    /// model already knows about columns those older migrations have not created yet.
    /// </summary>
    private static async Task SeedLegacyBusinessDataAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Categories (Name, Description) VALUES ('Snacks', 'Legacy category');
            INSERT INTO Suppliers (Name) VALUES ('Legacy Supplier');
            INSERT INTO Products (Name, Sku, UnitPrice, AverageUnitCost, QuantityInStock, LowStockThreshold,
                                  RestockTo, IsActive, Unit, CreatedAt, UpdatedAt)
                VALUES ('Chips', 'CHIP-1', 3.50, 1.10, 12, 2, 20, 1, 'unit',
                        '2026-01-01 00:00:00', '2026-01-01 00:00:00');
            """;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Both_tenancy_migrations_are_additive_and_assign_no_existing_record_to_a_business()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await MigrateToAsync(before, PreviousMigration);

            var tables = await TableNamesAsync(connection);
            Assert.DoesNotContain("Businesses", tables);
            Assert.DoesNotContain("BusinessMemberships", tables);
            Assert.DoesNotContain("BusinessId", await ColumnNamesAsync(connection, "Products"));

            await SeedLegacyBusinessDataAsync(connection);
        }

        await using (var checkpointOne = TestAppDbContext.Unrestricted(options))
        {
            await MigrateToAsync(checkpointOne, TenancyTablesMigration);

            var tables = await TableNamesAsync(connection);
            Assert.Contains("Businesses", tables);
            Assert.Contains("BusinessMemberships", tables);

            // Checkpoint 1 introduces the ownership model only; it owns nothing yet.
            Assert.Equal(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM Businesses;"));
            Assert.Equal(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM BusinessMemberships;"));
            Assert.DoesNotContain("BusinessId", await ColumnNamesAsync(connection, "Products"));
        }

        await using (var checkpointTwo = TestAppDbContext.Unrestricted(options))
        {
            await MigrateToAsync(checkpointTwo, OwnershipColumnMigration);

            // Every tenant-owned table gains the ownership column.
            foreach (var table in new[]
            {
                "Products", "Categories", "Suppliers", "Receipts", "ReceiptItems", "StockAdjustments",
                "SupplierOrders", "SupplierOrderLines", "SupplierOrderReceiptAllocations", "NayaxSales",
                "OperatingExpenses", "SiteCommissionAgreements", "CommissionPayments",
                "NayaxProcessingFeeRates", "ImportedFiles", "ImportedReimbursements",
                "InventoryCostTransitionBaselines", "InventoryCostTransitionMachineStocks",
            })
            {
                Assert.Contains("BusinessId", await ColumnNamesAsync(connection, table));
            }

            // Existing business data survives the upgrade untouched...
            Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM Products;"));
            Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM Categories;"));
            Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM Suppliers;"));
            Assert.Equal(
                1,
                await ScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM Products WHERE Name = 'Chips' AND UnitPrice = 3.50 AND QuantityInStock = 12;"));

            // ...and is owned by nobody. 0 is not a valid business key, so no backfill happened
            // and no caller can see these rows until one is performed deliberately.
            Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM Products WHERE BusinessId = 0;"));
            Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM Categories WHERE BusinessId = 0;"));
            Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM Suppliers WHERE BusinessId = 0;"));
        }
    }

    /// <summary>
    /// The unassigned legacy rows above must be invisible to a real business, not merely
    /// unowned. This is the property the backfill will later change deliberately.
    /// </summary>
    [Fact]
    public async Task Legacy_rows_left_unassigned_are_invisible_to_a_scoped_caller()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var migrated = TestAppDbContext.Unrestricted(options))
        {
            await MigrateToAsync(migrated, PreviousMigration);
            await SeedLegacyBusinessDataAsync(connection);
            await MigrateToAsync(migrated, ScopedUniquenessMigration);

            migrated.Businesses.Add(new Business { Name = "Vending Co", CreatedAtUtc = DateTime.UtcNow });
            await migrated.SaveChangesAsync();
        }

        await using var scoped = TestAppDbContext.For(options, 1);

        Assert.Empty(await scoped.Products.ToListAsync());
        Assert.Empty(await scoped.Categories.ToListAsync());
        Assert.Empty(await scoped.Suppliers.ToListAsync());
    }

    /// <summary>
    /// The uniqueness-scoping migration rebuilds NayaxSales to give it a local primary key, so
    /// this checks the rebuild the way it will actually be met in production: with rows already
    /// in the table. Sales are financial records - losing one, or collapsing two into a single
    /// key, would be a silent data loss that no later backfill could reconstruct.
    /// </summary>
    [Fact]
    public async Task Rekeying_NayaxSales_preserves_every_existing_sale_and_gives_each_a_distinct_key()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await MigrateToAsync(before, OwnershipColumnMigration);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO NayaxSales (TransactionID, MachineID, SettlementValue, MachineAuthorizationTime,
                                        CostingStatus, CostSource, BusinessId)
                    VALUES (1001, 11, 3.50, '2026-07-01 10:00:00', 0, 0, 0),
                           (1002, 11, 4.25, '2026-07-01 11:00:00', 0, 0, 0),
                           (1003, 22, 5.00, '2026-07-02 09:00:00', 0, 0, 0);
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await MigrateToAsync(after, ScopedUniquenessMigration);

            var sales = await after.NayaxSales.AsNoTracking().OrderBy(s => s.TransactionID).ToListAsync();

            Assert.Equal(3, sales.Count);
            Assert.Equal(new long[] { 1001, 1002, 1003 }, sales.Select(s => s.TransactionID));
            Assert.Equal(new decimal[] { 3.50m, 4.25m, 5.00m }, sales.Select(s => s.SettlementValue));

            // Every row came out with its own local key rather than colliding on a default.
            Assert.Equal(3, sales.Select(s => s.Id).Distinct().Count());
            Assert.DoesNotContain(0L, sales.Select(s => s.Id));
        }
    }
}
