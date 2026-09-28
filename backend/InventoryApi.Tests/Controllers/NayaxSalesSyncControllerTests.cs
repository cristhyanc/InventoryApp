using Inventory.Application.Nayax;
using Inventory.Application.SalesSync;
using InventoryApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// Issue #187: <c>POST api/nayax-sales-sync</c> is a thin binding over the Application use case
/// <see cref="SyncLatestNayaxSales"/> - the controller holds no AppDbContext, no Nayax client and no
/// import logic of its own, and it passes the request's cancellation token through to the use case.
/// </summary>
public class NayaxSalesSyncControllerTests
{
    [Fact]
    public async Task SyncLatest_runs_the_use_case_with_the_request_token_and_returns_no_content()
    {
        using var cancellation = new CancellationTokenSource();
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(cancellation.Token))
            .ReturnsAsync(new List<NayaxMachine> { new() { MachineID = 1, MachineName = "Machine A" } });
        nayax.Setup(x => x.GetMachineLastSalesAsync(1, cancellation.Token))
            .ReturnsAsync(new List<NayaxLastSalesReport>());
        var store = new Mock<ILatestNayaxSalesStore>();
        store.Setup(x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), cancellation.Token))
            .ReturnsAsync(new LatestNayaxSalesPersistResult(new Dictionary<long, DateTime>()));
        var controller = new NayaxSalesSyncController(new SyncLatestNayaxSales(nayax.Object, store.Object));

        var result = await controller.SyncLatest(cancellation.Token);

        Assert.IsType<NoContentResult>(result);
        nayax.VerifyAll();
        store.VerifyAll();
    }
}
