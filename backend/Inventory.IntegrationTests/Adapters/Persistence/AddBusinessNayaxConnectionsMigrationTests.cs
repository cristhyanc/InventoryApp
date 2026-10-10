using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddBusinessNayaxConnections</c> (issue #518): the migration adds one table
/// and nothing else. It stores no credentials, infers nothing from the existing
/// <c>NayaxLynx</c> configuration and touches no other table, so every business starts with no
/// connection record at all - which is exactly the <c>NotConfigured</c> state - and the existing
/// business's data is unchanged across the upgrade.
/// </summary>
public class AddBusinessNayaxConnectionsMigrationTests
{
    private const string PreviousMigration = "20261010025919_AddBusinessTimeZone";
    private const string TargetMigration = "AddBusinessNayaxConnections";

    [Fact]
    public async Task Migration_adds_only_the_connection_table_and_preserves_existing_data()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            Assert.DoesNotContain(
                "BusinessNayaxConnections",
                await MigrationSchemaProbe.TableNamesAsync(connection));

            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO Businesses (Name, IsActive, CreatedAtUtc, TimeZoneId)
                    VALUES ('Vending Co', 1, '2026-01-01 00:00:00', 'Australia/Sydney');
                INSERT INTO Products (Name, Sku, UnitPrice, AverageUnitCost, QuantityInStock, LowStockThreshold,
                                      RestockTo, IsActive, Unit, CreatedAt, UpdatedAt, BusinessId,
                                      CostingQuantity, InventoryValue, GstRule)
                    VALUES ('Chips', 'CHIP-1', 3.50, 1.10, 12, 2, 20, 1, 'unit',
                            '2026-01-01 00:00:00', '2026-01-01 00:00:00', 1, 12, 13.20, 0);
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            Assert.Contains(
                "BusinessNayaxConnections",
                await MigrationSchemaProbe.TableNamesAsync(connection));
            Assert.Equal(
                [
                    "AccessTokenCiphertext", "BusinessId", "CredentialRevision", "EncryptionKeyId", "Id",
                    "LastTestedAtUtc", "OperatorId", "Status", "UpdatedAtUtc",
                ],
                (await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessNayaxConnections"))
                    .Order(StringComparer.Ordinal));

            // No credential is created or inferred for the business that already exists: the
            // migration command for it is issue #519, run by a human.
            Assert.Empty(await after.BusinessNayaxConnections.ToListAsync());

            // One row per business is a schema guarantee, not an adapter convention.
            Assert.True(await UniqueBusinessIndexExistsAsync(connection));

            var business = await after.Businesses
                .Select(candidate => new { candidate.Name, candidate.IsActive, candidate.TimeZoneId })
                .SingleAsync();
            Assert.Equal(("Vending Co", true, "Australia/Sydney"), (business.Name, business.IsActive, business.TimeZoneId));

            // Projected rather than materialised: this database stops at TargetMigration, so a
            // column a later additive migration adds to Products does not exist here yet.
            var product = await after.Products
                .Select(candidate => new
                {
                    candidate.QuantityInStock,
                    candidate.CostingQuantity,
                    candidate.InventoryValue,
                    candidate.AverageUnitCost,
                })
                .SingleAsync();
            Assert.Equal(
                (12, 12, 13.20m, 1.10m),
                (product.QuantityInStock, product.CostingQuantity, product.InventoryValue, product.AverageUnitCost));
        }
    }

    private static async Task<bool> UniqueBusinessIndexExistsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM pragma_index_list('BusinessNayaxConnections') AS indexes
            JOIN pragma_index_info(indexes.name) AS columns
            WHERE indexes."unique" = 1 AND columns.name = 'BusinessId';
            """;

        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }
}
