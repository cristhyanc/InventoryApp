using Inventory.Application.Machines;
using Inventory.Application.Products;
using InventoryApi.Tests.Application.Time;
using InventoryApi.Adapters.Mapping;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using Inventory.Application.Nayax;
using InventoryApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

using Inventory.Domain.FinancialConfiguration;

namespace InventoryApi.Tests.Services;

/// <summary>
/// Regression tests for the migrated machine dashboard and machine-product slices over the real
/// EF adapters, retargeted from the <c>MachineService</c> delegator tests they replace when issue
/// #302 deleted it: <see cref="GetMachineDashboard"/>/<see cref="ListMachineDashboard"/> (issue
/// #241) and <see cref="ListMachineProducts"/> (issue #240) are what the endpoints call now, and
/// the API-owned responses they serialise come from
/// <see cref="MachineResponseMapper"/>/<see cref="ProductRecordResponseMapper"/>.
/// </summary>
public class MachineUseCaseTests
{
    /// <summary>
    /// One pinned instant drives both the seeded sale timestamps and the dashboard clock, so a sale
    /// "just now" falls inside the Australia/Sydney business day the use case resolves (issue #310)
    /// whatever the host's own timezone is.
    /// </summary>
    private static readonly FixedSydneyTime Time = FixedSydneyTime.PinnedToNow();

    private static AppDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return TestAppDbContext.Unrestricted(options);
    }

    private static ListMachineProducts MachineProducts(AppDbContext db, INayaxLynxClient nayax) =>
        new(
            nayax,
            new EfProductCatalogStore(db),
            new ResolveMachineProductPricing(new EfSiteFactsStore(db), Time.Calendar));

    private static GetMachineDashboard MachineDashboard(AppDbContext db, INayaxLynxClient nayax) =>
        new(
            nayax,
            new EfMachineDashboardFactsStore(db, TestFinancialUseCases.ProcessingFees(db, Time.Calendar)),
            Time.Clock,
            Time.Calendar);

    private static ListMachineDashboard MachineDashboardListing(AppDbContext db, INayaxLynxClient nayax) =>
        new(
            nayax,
            new EfMachineDashboardFactsStore(db, TestFinancialUseCases.ProcessingFees(db, Time.Calendar)),
            Time.Clock,
            Time.Calendar);

    /// <summary>
    /// Relational SQLite test: the supplier attached to a machine's product listing must come
    /// from an explicit query rather than lazy loading (issue #52), so it is still populated when
    /// read from a context separate from the one that seeded it - matching separate requests. It is
    /// asserted on the serialised response, because that nesting is what the endpoint promises.
    /// </summary>
    [Fact]
    public async Task GetMachineProducts_ReturnsSupplierWithoutLazyLoading()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var schema = TestAppDbContext.Unrestricted(options))
            await schema.Database.EnsureCreatedAsync();

        await using (var seed = TestAppDbContext.Unrestricted(options))
        {
            seed.Suppliers.Add(new Supplier { Id = 1, Name = "Acme" });
            seed.Products.Add(new Product { Id = 200, Name = "px", UnitPrice = 2m, SupplierId = 1 });
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.Unrestricted(options);
        var nayaxMock = new Mock<INayaxLynxClient>();
        nayaxMock.Setup(m => m.GetMachineAsync(1, default))
            .ReturnsAsync(new NayaxMachine { MachineID = 1, MachineName = "M1" });
        nayaxMock.Setup(m => m.GetMachineProductsAsync(1, default))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new NayaxMachineProduct { NayaxProductID = 200, ProductName = "px", RetailPrice = 5m }
            });

        var rows = await MachineProducts(db, nayaxMock.Object).Handle(1, CancellationToken.None);

        var product = ProductRecordResponseMapper.ToResponse(Assert.Single(rows));
        Assert.NotNull(product.Supplier);
        Assert.Equal("Acme", product.Supplier!.Name);
    }

    [Fact]
    public async Task GetById_Computes_Revenues()
    {
        using var db = CreateDbContext("mach_test");
        // seed product referenced by Nayax
        db.Products.Add(new Product { Id = 200, Name = "px", UnitPrice = 2m });
        await db.SaveChangesAsync();

        var nayaxMock = new Mock<INayaxLynxClient>();
        nayaxMock.Setup(m => m.GetMachineAsync(1, default))
            .ReturnsAsync(new NayaxMachine { MachineID = 1, MachineName = "M1" });

        nayaxMock.Setup(m => m.GetMachineProductsAsync(1, default))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new NayaxMachineProduct { NayaxProductID = 200, ProductName = "ProdX", RetailPrice = 5m, CommissionValue = 10 }
            });

        nayaxMock.Setup(m => m.GetMachineLastSalesAsync(1, default))
            .ReturnsAsync(new List<NayaxLastSalesReport>
            {
                new NayaxLastSalesReport { MachineID = 1, ProductName = "ProdX", SettlementValue = 5m, MachineAuthorizationTime = System.DateTime.UtcNow }
            });

        var summary = await MachineDashboard(db, nayaxMock.Object).Handle(1, CancellationToken.None);
        Assert.NotNull(summary);
        var machine = MachineResponseMapper.ToResponse(summary!);
        Assert.Equal(1, machine.MachineID);
        Assert.True(machine.TodayGrossRevenue >= 0);
    }

    /// <summary>
    /// Issue #187: latest-sales synchronization is an explicit, shared operation
    /// (<c>Inventory.Application.SalesSync.SyncLatestNayaxSales</c>), no longer a hidden side effect of
    /// the machine listing. Machines must calculate from whatever <c>NayaxSales</c>
    /// rows are already persisted, never trigger a fresh Nayax import themselves.
    /// </summary>
    [Fact]
    public async Task GetAll_DoesNotImportLatestSalesAsASideEffect()
    {
        using var db = CreateDbContext("mach_test_no_side_effect");
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 1,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = 1,
            SettlementValue = 4m,
            MachineAuthorizationTime = Time.NowUtc
        });
        await db.SaveChangesAsync();

        var nayaxMock = new Mock<INayaxLynxClient>();
        nayaxMock.Setup(m => m.GetMachinesAsync(default))
            .ReturnsAsync(new List<NayaxMachine> { new NayaxMachine { MachineID = 1, MachineName = "M1" } });

        var machines = await MachineDashboardListing(db, nayaxMock.Object).Handle(CancellationToken.None);

        nayaxMock.Verify(m => m.GetMachineLastSalesAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        var machine = Assert.Single(machines);
        Assert.Equal(4m, machine.TodayGrossRevenue);
    }
}
