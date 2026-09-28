using InventoryApi.Data;
using Inventory.Application.Nayax;
using InventoryApi.Models;
using InventoryApi.Services;
using InventoryApi.Services.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Services;

public class MachineServiceTests
{
    private static AppDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return TestAppDbContext.Unrestricted(options);
    }

    /// <summary>
    /// Relational SQLite test: the supplier attached to a machine's product listing must come
    /// from an explicit query rather than lazy loading (issue #52), so it is still populated when
    /// read from a context separate from the one that seeded it - matching separate requests.
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

        IMachineService svc = new MachineService(db, nayaxMock.Object);
        var products = await svc.GetMachineProducts(1);

        var product = Assert.Single(products);
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

        IMachineService svc = new MachineService(db, nayaxMock.Object);
        var machine = await svc.GetById(1);
        Assert.NotNull(machine);
        Assert.Equal(1, machine.MachineID);
        Assert.True(machine.TodayGrossRevenue >= 0);
    }

    /// <summary>
    /// Issue #187: latest-sales synchronization is an explicit, shared operation
    /// (<see cref="NayaxLatestSalesSyncService"/>), no longer a hidden side effect of
    /// <see cref="MachineService.GetAll"/>. Machines must calculate from whatever <c>NayaxSales</c>
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
            MachineAuthorizationTime = DateTime.Now
        });
        await db.SaveChangesAsync();

        var nayaxMock = new Mock<INayaxLynxClient>();
        nayaxMock.Setup(m => m.GetMachinesAsync(default))
            .ReturnsAsync(new List<NayaxMachine> { new NayaxMachine { MachineID = 1, MachineName = "M1" } });

        IMachineService svc = new MachineService(db, nayaxMock.Object);
        var machines = await svc.GetAll();

        nayaxMock.Verify(m => m.GetMachineLastSalesAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        var machine = Assert.Single(machines);
        Assert.Equal(4m, machine.TodayGrossRevenue);
    }

    [Fact]
    public void Week_to_date_comparison_uses_same_elapsed_period()
    {
        var reference = new System.DateTime(2026, 9, 4, 14, 30, 0);

        var current = MachineService.GetWeekToDateRange(reference);
        var previous = MachineService.GetPreviousComparableWeekRange(reference);

        Assert.Equal(new System.DateTime(2026, 8, 31), current.Start);
        Assert.Equal(reference, current.End);
        Assert.Equal(new System.DateTime(2026, 8, 24), previous.Start);
        Assert.Equal(new System.DateTime(2026, 8, 28, 14, 30, 0), previous.End);
    }

    [Fact]
    public void Month_to_date_starts_at_the_first_local_day()
    {
        var range = MachineService.GetMonthToDateRange(new System.DateTime(2026, 9, 4, 14, 30, 0));

        Assert.Equal(new System.DateTime(2026, 9, 1), range.Start);
        Assert.Equal(new System.DateTime(2026, 9, 4, 14, 30, 0), range.End);
    }

}
