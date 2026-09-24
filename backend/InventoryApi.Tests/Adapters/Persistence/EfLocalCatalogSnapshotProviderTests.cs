using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational SQLite tests: machine history is derived by grouping <c>NayaxSales</c> rows, which
/// depends on SQL translation and in-memory grouping/ordering, not just a simple projection.
/// </summary>
public class EfLocalCatalogSnapshotProviderTests
{
    private static async Task<SqliteConnection> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await setup.Database.EnsureCreatedAsync();
        return connection;
    }

    [Fact]
    public async Task GetProductsAsync_returns_every_persisted_product_by_its_Nayax_identity()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.For(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options, businessId: 1);
        db.Products.Add(new Product { Id = 1, Name = "Coke Zero", RestockTo = 0 });
        db.Products.Add(new Product { Id = 2, Name = "Chips", RestockTo = 0 });
        await db.SaveChangesAsync();
        var provider = new EfLocalCatalogSnapshotProvider(db);

        var products = await provider.GetProductsAsync(CancellationToken.None);

        Assert.Equal(2, products.Count);
        Assert.Contains(products, p => p.ExternalId == 1 && p.Name == "Coke Zero");
        Assert.Contains(products, p => p.ExternalId == 2 && p.Name == "Chips");
    }

    [Fact]
    public async Task GetMachinesAsync_uses_the_most_recent_sale_name_as_the_current_local_name()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.For(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options, businessId: 1);
        db.NayaxSales.Add(new NayaxSales { TransactionID = 1, MachineID = 10, MachineName = "Old Site Name", SettlementValue = 2m, MachineAuthorizationTime = new DateTime(2026, 1, 1) });
        db.NayaxSales.Add(new NayaxSales { TransactionID = 2, MachineID = 10, MachineName = "New Site Name", SettlementValue = 2m, MachineAuthorizationTime = new DateTime(2026, 2, 1) });
        await db.SaveChangesAsync();
        var provider = new EfLocalCatalogSnapshotProvider(db);

        var machines = await provider.GetMachinesAsync(CancellationToken.None);

        var machine = Assert.Single(machines);
        Assert.Equal(10, machine.ExternalId);
        Assert.Equal("New Site Name", machine.Name);
        Assert.Equal(["Old Site Name"], machine.PriorNames);
    }

    [Fact]
    public async Task GetMachinesAsync_reports_no_prior_names_when_every_sale_agrees_on_the_machine_name()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.For(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options, businessId: 1);
        db.NayaxSales.Add(new NayaxSales { TransactionID = 1, MachineID = 10, MachineName = "Machine A", SettlementValue = 2m, MachineAuthorizationTime = new DateTime(2026, 1, 1) });
        db.NayaxSales.Add(new NayaxSales { TransactionID = 2, MachineID = 10, MachineName = "Machine A", SettlementValue = 2m, MachineAuthorizationTime = new DateTime(2026, 2, 1) });
        await db.SaveChangesAsync();
        var provider = new EfLocalCatalogSnapshotProvider(db);

        var machines = await provider.GetMachinesAsync(CancellationToken.None);

        var machine = Assert.Single(machines);
        Assert.Empty(machine.PriorNames);
    }

    [Fact]
    public async Task Products_and_machines_from_one_business_are_invisible_to_another_business()
    {
        await using var connection = await CreateSqliteAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var businessOne = TestAppDbContext.For(options, businessId: 1))
        {
            businessOne.Products.Add(new Product { Id = 1, Name = "Business One Product", RestockTo = 0 });
            businessOne.NayaxSales.Add(new NayaxSales { TransactionID = 1, MachineID = 10, MachineName = "Business One Machine", SettlementValue = 2m, MachineAuthorizationTime = new DateTime(2026, 1, 1) });
            await businessOne.SaveChangesAsync();
        }

        await using var businessTwo = TestAppDbContext.For(options, businessId: 2);
        var provider = new EfLocalCatalogSnapshotProvider(businessTwo);

        Assert.Empty(await provider.GetProductsAsync(CancellationToken.None));
        Assert.Empty(await provider.GetMachinesAsync(CancellationToken.None));
    }
}
