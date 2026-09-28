using InventoryApi.Data;
using Inventory.Application.Nayax;
using InventoryApi.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Services;

/// <summary>
/// Issue #187: <see cref="NayaxLatestSalesSyncService"/> is the extracted, explicit shared
/// latest-sales import previously hidden inside <c>MachineService.SaveMachinesLastSalesAsync</c>.
/// These tests cover the orchestration this extraction adds - it discovers machines itself rather
/// than being handed a machine id list - while the transaction dedup/costing/classification
/// behaviour itself stays covered by <c>InventoryCostTransitionServiceTests</c> and
/// <c>NayaxHistoricalCostTests</c>, which already exercised it through this same method before the
/// extraction.
/// </summary>
public class NayaxLatestSalesSyncServiceTests
{
    private static AppDbContext CreateDb() =>
        TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task SyncLatestSalesAsync_DiscoversMachinesItself_AndPersistsEachMachinesSales()
    {
        await using var db = CreateDb();
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine>
            {
                new() { MachineID = 1, MachineName = "Machine A" },
                new() { MachineID = 2, MachineName = "Machine B" }
            });
        nayax.Setup(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxLastSalesReport>
            {
                new() { TransactionID = 100, MachineID = 1, SettlementValue = 3m, MachineAuthorizationTime = DateTime.UtcNow }
            });
        nayax.Setup(x => x.GetMachineLastSalesAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxLastSalesReport>
            {
                new() { TransactionID = 200, MachineID = 2, SettlementValue = 5m, MachineAuthorizationTime = DateTime.UtcNow }
            });

        await new NayaxLatestSalesSyncService(db, nayax.Object).SyncLatestSalesAsync();

        nayax.Verify(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()), Times.Once);
        var persisted = await db.NayaxSales.OrderBy(s => s.TransactionID).ToListAsync();
        Assert.Equal(2, persisted.Count);
        Assert.Equal(100, persisted[0].TransactionID);
        Assert.Equal(200, persisted[1].TransactionID);
    }

    [Fact]
    public async Task SyncLatestSalesAsync_RunTwice_DoesNotDuplicateTheSameTransaction()
    {
        await using var db = CreateDb();
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine> { new() { MachineID = 1, MachineName = "Machine A" } });
        nayax.Setup(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxLastSalesReport>
            {
                new() { TransactionID = 100, MachineID = 1, SettlementValue = 3m, MachineAuthorizationTime = DateTime.UtcNow }
            });
        var service = new NayaxLatestSalesSyncService(db, nayax.Object);

        await service.SyncLatestSalesAsync();
        await service.SyncLatestSalesAsync();

        Assert.Single(await db.NayaxSales.ToListAsync());
    }
}
