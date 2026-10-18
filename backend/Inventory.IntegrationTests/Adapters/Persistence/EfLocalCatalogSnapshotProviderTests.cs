using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
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
    /// <summary>
    /// Both businesses sell through Nayax (issue #759), because the provider the business sells
    /// through is what makes a stored external id a Nayax product id at all. Seeding it is what lets
    /// the tenant-isolation test below prove the *filter* keeps one business's catalogue out of the
    /// other's answer, rather than both answers being empty for want of a provider.
    /// </summary>
    private static async Task<SqliteConnection> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await setup.Database.EnsureCreatedAsync();
        setup.Businesses.AddRange(NayaxBusiness(1, "Vending A"), NayaxBusiness(2, "Vending B"));
        await setup.SaveChangesAsync();
        return connection;
    }

    private static Business NayaxBusiness(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Status = BusinessStatus.Active,
        SalesProvider = ExternalProvider.Nayax,
        CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    /// <summary>
    /// The shape issue #732's backfill recorded for data that already existed, and issue #759's copy
    /// moved onto the row: every product carrying the Nayax id that equals its own key, so the
    /// reconciliation reports exactly what it always did while reading
    /// <c>Product.ExternalProductId</c> rather than <c>Product.Id</c> (issue #745).
    /// </summary>
    [Fact]
    public async Task GetProductsAsync_returns_every_persisted_product_by_its_Nayax_identity()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.For(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options, businessId: 1);
        db.Products.Add(new Product { Id = 1, Name = "Coke Zero", RestockTo = 0, ExternalProductId = NayaxExternalId.From(1) });
        db.Products.Add(new Product { Id = 2, Name = "Chips", RestockTo = 0, ExternalProductId = NayaxExternalId.From(2) });
        await db.SaveChangesAsync();
        var provider = new EfLocalCatalogSnapshotProvider(db);

        var products = await provider.GetProductsAsync(CancellationToken.None);

        Assert.Equal(2, products.Count);
        Assert.Contains(products, p => p.ExternalId == 1 && p.Name == "Coke Zero");
        Assert.Contains(products, p => p.ExternalId == 2 && p.Name == "Chips");
    }

    /// <summary>
    /// Issue #745: a product's Nayax identity is the id recorded on it, so a local key that differs
    /// from it is reported under the recorded Nayax id - and a local product whose own key equals an
    /// unrelated Nayax id is not reported under that id, which is what would otherwise make the
    /// report describe the wrong product once #735 (A4) ends the coincidence.
    /// </summary>
    [Fact]
    public async Task GetProductsAsync_reports_a_product_under_the_Nayax_id_recorded_on_it()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.For(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options, businessId: 1);
        db.Products.Add(new Product { Id = 5001, Name = "Coke Zero", RestockTo = 0, ExternalProductId = NayaxExternalId.From(900) });
        db.Products.Add(new Product { Id = 900, Name = "Locally created", RestockTo = 0 });
        await db.SaveChangesAsync();
        var provider = new EfLocalCatalogSnapshotProvider(db);

        var product = Assert.Single(await provider.GetProductsAsync(CancellationToken.None));

        Assert.Equal(900, product.ExternalId);
        Assert.Equal("Coke Zero", product.Name);
    }

    /// <summary>
    /// A product with no recorded external id has no provider identity at all, so it takes no part
    /// in a comparison against Nayax's catalogue: it is a product Nayax never named, not one Nayax
    /// stopped returning, and reporting it as either would be a data-quality claim about an identity
    /// that does not exist.
    /// </summary>
    [Fact]
    public async Task GetProductsAsync_omits_a_product_that_has_no_recorded_external_id()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.For(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options, businessId: 1);
        db.Products.Add(new Product { Id = 7, Name = "Locally created", RestockTo = 0 });
        await db.SaveChangesAsync();
        var provider = new EfLocalCatalogSnapshotProvider(db);

        Assert.Empty(await provider.GetProductsAsync(CancellationToken.None));
    }

    /// <summary>
    /// The external id column holds the id the business's own sales provider issued and does not say
    /// which system that was (issue #759), so a business that does not sell through Nayax has no
    /// Nayax catalogue to reconcile - whatever its rows happen to carry. The product here is given
    /// the exact text a Nayax id would have, because the wrong answer is to report it.
    /// </summary>
    [Fact]
    public async Task GetProductsAsync_reports_nothing_for_a_business_that_does_not_sell_through_Nayax()
    {
        await using var connection = await CreateSqliteAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var withoutProvider = TestAppDbContext.Unrestricted(options))
        {
            withoutProvider.Businesses.Add(new Business
            {
                Id = 3,
                Name = "Vending C",
                Status = BusinessStatus.Active,
                CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await withoutProvider.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(options, businessId: 3);
        db.Products.Add(new Product { Id = 1, Name = "Coke Zero", RestockTo = 0, ExternalProductId = NayaxExternalId.From(1) });
        await db.SaveChangesAsync();

        Assert.Empty(await new EfLocalCatalogSnapshotProvider(db).GetProductsAsync(CancellationToken.None));
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
            // The product carries its Nayax id, so business one really does have a product to
            // report: the other business seeing nothing is the tenant filter's answer, not an
            // unrecorded id's.
            businessOne.Products.Add(new Product
            {
                Id = 1,
                Name = "Business One Product",
                RestockTo = 0,
                ExternalProductId = NayaxExternalId.From(1),
            });
            businessOne.NayaxSales.Add(new NayaxSales { TransactionID = 1, MachineID = 10, MachineName = "Business One Machine", SettlementValue = 2m, MachineAuthorizationTime = new DateTime(2026, 1, 1) });
            await businessOne.SaveChangesAsync();
        }

        await using var businessTwo = TestAppDbContext.For(options, businessId: 2);
        var provider = new EfLocalCatalogSnapshotProvider(businessTwo);

        Assert.Empty(await provider.GetProductsAsync(CancellationToken.None));
        Assert.Empty(await provider.GetMachinesAsync(CancellationToken.None));
    }
}
