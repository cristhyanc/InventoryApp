using Inventory.Domain.Providers;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
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
    private static ProductExternalId NayaxLink(long nayaxProductId, long localProductId) =>
        new()
        {
            Provider = ExternalProvider.Nayax,
            ExternalId = NayaxExternalId.From(nayaxProductId),
            ProductId = localProductId,
        };

    private static async Task<SqliteConnection> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await setup.Database.EnsureCreatedAsync();
        return connection;
    }

    /// <summary>
    /// The shape issue #732's backfill recorded for data that already existed: every product linked
    /// to the Nayax id that equals its own key, so the reconciliation reports exactly what it
    /// always did while reading the link table rather than <c>Product.Id</c> (issue #745).
    /// </summary>
    [Fact]
    public async Task GetProductsAsync_returns_every_persisted_product_by_its_Nayax_identity()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.For(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options, businessId: 1);
        db.Products.Add(new Product { Id = 1, Name = "Coke Zero", RestockTo = 0 });
        db.Products.Add(new Product { Id = 2, Name = "Chips", RestockTo = 0 });
        await db.SaveChangesAsync();
        db.ProductExternalIds.AddRange(NayaxLink(nayaxProductId: 1, localProductId: 1), NayaxLink(2, 2));
        await db.SaveChangesAsync();
        var provider = new EfLocalCatalogSnapshotProvider(db);

        var products = await provider.GetProductsAsync(CancellationToken.None);

        Assert.Equal(2, products.Count);
        Assert.Contains(products, p => p.ExternalId == 1 && p.Name == "Coke Zero");
        Assert.Contains(products, p => p.ExternalId == 2 && p.Name == "Chips");
    }

    /// <summary>
    /// Issue #745: a product's Nayax identity is its link, so a local key that differs from it is
    /// reported under the linked Nayax id - and a local product whose own key equals an unrelated
    /// Nayax id is not reported under that id, which is what would otherwise make the report
    /// describe the wrong product once #735 (A4) ends the coincidence.
    /// </summary>
    [Fact]
    public async Task GetProductsAsync_reports_a_product_under_the_Nayax_id_its_link_names()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.For(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options, businessId: 1);
        db.Products.Add(new Product { Id = 5001, Name = "Coke Zero", RestockTo = 0 });
        db.Products.Add(new Product { Id = 900, Name = "Locally created", RestockTo = 0 });
        await db.SaveChangesAsync();
        db.ProductExternalIds.Add(NayaxLink(nayaxProductId: 900, localProductId: 5001));
        await db.SaveChangesAsync();
        var provider = new EfLocalCatalogSnapshotProvider(db);

        var product = Assert.Single(await provider.GetProductsAsync(CancellationToken.None));

        Assert.Equal(900, product.ExternalId);
        Assert.Equal("Coke Zero", product.Name);
    }

    /// <summary>
    /// A product with no Nayax link has no Nayax identity at all, so it takes no part in a
    /// comparison against Nayax's catalogue: it is a product Nayax never named, not one Nayax
    /// stopped returning, and reporting it as either would be a data-quality claim about an identity
    /// that does not exist.
    /// </summary>
    [Fact]
    public async Task GetProductsAsync_omits_a_product_that_has_no_Nayax_link()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.For(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options, businessId: 1);
        db.Products.Add(new Product { Id = 7, Name = "Locally created", RestockTo = 0 });
        await db.SaveChangesAsync();
        var provider = new EfLocalCatalogSnapshotProvider(db);

        Assert.Empty(await provider.GetProductsAsync(CancellationToken.None));
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
            // Linked, so business one really does have a product to report: the other business
            // seeing nothing is the tenant filter's answer, not an absent link's.
            businessOne.ProductExternalIds.Add(NayaxLink(nayaxProductId: 1, localProductId: 1));
            await businessOne.SaveChangesAsync();
        }

        await using var businessTwo = TestAppDbContext.For(options, businessId: 2);
        var provider = new EfLocalCatalogSnapshotProvider(businessTwo);

        Assert.Empty(await provider.GetProductsAsync(CancellationToken.None));
        Assert.Empty(await provider.GetMachinesAsync(CancellationToken.None));
    }
}
