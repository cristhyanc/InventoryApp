using Inventory.Application.Nayax;
using Inventory.Application.SalesSync;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.SalesSync;

/// <summary>
/// Issue #187: the coordinated latest-Nayax-sales synchronization as the Application use case
/// <see cref="SyncLatestNayaxSales"/> now owns it, instead of the hidden
/// <c>MachineService.SaveMachinesLastSalesAsync</c> side effect it was extracted from. The use case
/// discovers the machines through the <see cref="INayaxLynxClient"/> port and delegates persistence,
/// deduplication, costing and the inventory-cost rebuild to the <see cref="ILatestNayaxSalesStore"/>
/// port.
///
/// The first two tests run over the real <see cref="EfLatestNayaxSalesStore"/> adapter rather than a
/// stub, so the import behaviour is covered end to end; the rest use a stubbed port, because what they
/// prove is the use case's own orchestration (read every machine before persisting anything, rebuild
/// only what a completed sale affected). The transaction dedup/costing/classification rules
/// themselves stay covered by <c>InventoryCostTransitionServiceTests</c> and
/// <c>NayaxHistoricalCostTests</c>, which already exercised them before the extraction.
/// </summary>
public class SyncLatestNayaxSalesTests
{
    private static AppDbContext CreateDb() =>
        TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static SyncLatestNayaxSales UseCase(AppDbContext db, INayaxLynxClient nayax)
    {
        var rebuild = TestCostingUseCases.Rebuild(db);
        return new SyncLatestNayaxSales(
            nayax, new EfLatestNayaxSalesStore(db, new SaleCostingService(db, rebuild), rebuild));
    }

    [Fact]
    public async Task Handle_discovers_machines_itself_and_persists_every_machines_sales()
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

        await UseCase(db, nayax.Object).Handle();

        nayax.Verify(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()), Times.Once);
        var persisted = await db.NayaxSales.OrderBy(s => s.TransactionID).ToListAsync();
        Assert.Equal(2, persisted.Count);
        Assert.Equal(100, persisted[0].TransactionID);
        Assert.Equal(200, persisted[1].TransactionID);
    }

    [Fact]
    public async Task Handle_run_twice_does_not_duplicate_the_same_transaction()
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
        var useCase = UseCase(db, nayax.Object);

        await useCase.Handle();
        await useCase.Handle();

        Assert.Single(await db.NayaxSales.ToListAsync());
    }

    /// <summary>
    /// The former private method added every machine's rows to the change tracker and saved once at
    /// the end, so a Nayax read that failed part-way through persisted nothing. The use case keeps
    /// that boundary by reading all machines before it calls the store at all.
    /// </summary>
    [Fact]
    public async Task Handle_persists_nothing_when_a_machines_sales_read_fails()
    {
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
            .ThrowsAsync(new InvalidOperationException("Nayax unavailable"));
        var store = new Mock<ILatestNayaxSalesStore>(MockBehavior.Strict);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SyncLatestNayaxSales(nayax.Object, store.Object).Handle());

        store.Verify(
            x => x.PersistLatestSalesAsync(It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_rebuilds_inventory_costs_for_the_products_a_completed_sale_affected()
    {
        var affected = new Dictionary<long, DateTime> { [10] = new(2026, 9, 2, 14, 30, 0) };
        var store = new Mock<ILatestNayaxSalesStore>();
        store.Setup(x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LatestNayaxSalesPersistResult(affected));

        await new SyncLatestNayaxSales(SingleMachineNayax().Object, store.Object).Handle();

        store.Verify(
            x => x.RebuildInventoryCostsAsync(affected, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_does_not_rebuild_inventory_costs_when_no_completed_sale_was_affected()
    {
        var store = new Mock<ILatestNayaxSalesStore>();
        store.Setup(x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LatestNayaxSalesPersistResult(new Dictionary<long, DateTime>()));

        await new SyncLatestNayaxSales(SingleMachineNayax().Object, store.Object).Handle();

        store.Verify(
            x => x.RebuildInventoryCostsAsync(
                It.IsAny<IReadOnlyDictionary<long, DateTime>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static Mock<INayaxLynxClient> SingleMachineNayax()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine> { new() { MachineID = 1, MachineName = "Machine A" } });
        nayax.Setup(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxLastSalesReport>
            {
                new() { TransactionID = 100, MachineID = 1, SettlementValue = 3m, MachineAuthorizationTime = DateTime.UtcNow }
            });
        return nayax;
    }
}
