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
    public async Task GetMachinesAsync_uses_the_most_recent_sale_name_as_the_latest_reliable_local_name()
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
        Assert.Equal(["Old Site Name"], machine.HistoricalNames);
        Assert.False(machine.CurrentNameIsAmbiguous);
    }

    /// <summary>
    /// Regression for the repair of PR #139: the current local name is only ambiguous when the most
    /// recent authorization time itself carries more than one distinct name. A rename recorded at an
    /// earlier time resolves cleanly (covered above) and must not set the flag.
    /// </summary>
    [Fact]
    public async Task GetMachinesAsync_marks_the_current_name_ambiguous_when_the_latest_sale_time_carries_two_names()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.For(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options, businessId: 1);
        var sameInstant = new DateTime(2026, 2, 1);
        db.NayaxSales.Add(new NayaxSales { TransactionID = 1, MachineID = 10, MachineName = "Machine At Depot", SettlementValue = 2m, MachineAuthorizationTime = sameInstant });
        db.NayaxSales.Add(new NayaxSales { TransactionID = 2, MachineID = 10, MachineName = "Machine At Site", SettlementValue = 2m, MachineAuthorizationTime = sameInstant });
        await db.SaveChangesAsync();
        var provider = new EfLocalCatalogSnapshotProvider(db);

        var machine = Assert.Single(await provider.GetMachinesAsync(CancellationToken.None));

        Assert.True(machine.CurrentNameIsAmbiguous);
        Assert.Equal("Machine At Depot", machine.Name);
        Assert.Equal(["Machine At Site"], machine.HistoricalNames);
    }

    [Fact]
    public async Task GetMachinesAsync_treats_a_case_only_difference_as_the_same_machine_name()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.For(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options, businessId: 1);
        db.NayaxSales.Add(new NayaxSales { TransactionID = 1, MachineID = 10, MachineName = "machine a", SettlementValue = 2m, MachineAuthorizationTime = new DateTime(2026, 1, 1) });
        db.NayaxSales.Add(new NayaxSales { TransactionID = 2, MachineID = 10, MachineName = "Machine A", SettlementValue = 2m, MachineAuthorizationTime = new DateTime(2026, 2, 1) });
        await db.SaveChangesAsync();
        var provider = new EfLocalCatalogSnapshotProvider(db);

        var machine = Assert.Single(await provider.GetMachinesAsync(CancellationToken.None));

        Assert.Equal("Machine A", machine.Name);
        Assert.Empty(machine.HistoricalNames);
        Assert.False(machine.CurrentNameIsAmbiguous);
    }

    [Fact]
    public async Task GetMachinesAsync_reports_no_historical_names_when_every_sale_agrees_on_the_machine_name()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.For(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options, businessId: 1);
        db.NayaxSales.Add(new NayaxSales { TransactionID = 1, MachineID = 10, MachineName = "Machine A", SettlementValue = 2m, MachineAuthorizationTime = new DateTime(2026, 1, 1) });
        db.NayaxSales.Add(new NayaxSales { TransactionID = 2, MachineID = 10, MachineName = "Machine A", SettlementValue = 2m, MachineAuthorizationTime = new DateTime(2026, 2, 1) });
        await db.SaveChangesAsync();
        var provider = new EfLocalCatalogSnapshotProvider(db);

        var machines = await provider.GetMachinesAsync(CancellationToken.None);

        var machine = Assert.Single(machines);
        Assert.Empty(machine.HistoricalNames);
        Assert.False(machine.CurrentNameIsAmbiguous);
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
