using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for the <c>AddBusinessOwnershipModel</c> migration (issue #64, checkpoint 1).
///
/// This step establishes the ownership model only. The migration must therefore be purely
/// additive: it creates the two tenancy tables, touches no existing table, and assigns no
/// existing record to a business. Backfilling existing data to the bootstrap business is a
/// separate, human-reviewed change, and this test is what keeps the two apart.
/// </summary>
public class AddBusinessOwnershipModelMigrationTests
{
    private const string PreviousMigration = "20260915064737_AddProductRestockTo";
    private const string TenancyMigration = "20260923110030_AddBusinessOwnershipModel";

    private static async Task MigrateToAsync(AppDbContext db, string targetMigration) =>
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);

    private static async Task<List<string>> TableNamesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync();

        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    [Fact]
    public async Task Upgrading_adds_the_tenancy_tables_without_changing_existing_data()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = new AppDbContext(options))
        {
            await MigrateToAsync(before, PreviousMigration);

            Assert.DoesNotContain("Businesses", await TableNamesAsync(connection));
            Assert.DoesNotContain("BusinessMemberships", await TableNamesAsync(connection));

            before.Categories.Add(new Category { Name = "Snacks" });
            before.Products.Add(new Product { Name = "Chips", Sku = "CHIP-1", UnitPrice = 3.50m, QuantityInStock = 12 });
            await before.SaveChangesAsync();
        }

        await using (var after = new AppDbContext(options))
        {
            await MigrateToAsync(after, TenancyMigration);

            var tables = await TableNamesAsync(connection);
            Assert.Contains("Businesses", tables);
            Assert.Contains("BusinessMemberships", tables);

            // Additive only: the migration creates the ownership tables and leaves them empty.
            // No record is assigned to a business yet, so nothing is backfilled here.
            Assert.Empty(await after.Businesses.ToListAsync());
            Assert.Empty(await after.BusinessMemberships.ToListAsync());

            // Existing business data is untouched and still readable after the upgrade.
            var product = Assert.Single(await after.Products.AsNoTracking().ToListAsync());
            Assert.Equal("Chips", product.Name);
            Assert.Equal(3.50m, product.UnitPrice);
            Assert.Equal(12, product.QuantityInStock);
            Assert.Equal("Snacks", Assert.Single(await after.Categories.AsNoTracking().ToListAsync()).Name);
        }
    }
}
