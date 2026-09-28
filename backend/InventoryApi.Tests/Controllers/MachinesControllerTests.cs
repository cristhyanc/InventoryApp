using Inventory.Application.MachineStockSync;
using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using InventoryApi.Controllers;
using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The Sync Restock endpoints (issue #183) are thin bindings over the Application use cases
/// <see cref="SyncMachineStockFromNayax"/>/<see cref="ApplyMachineStockSync"/>: no AppDbContext,
/// Nayax client, or business logic lives in the controller itself.
/// </summary>
public class MachinesControllerTests
{
    private const long MachineId = 42;

    private static MachinesController Controller(IMachineStockEventStore store, INayaxLynxClient? nayax = null) =>
        new(
            Mock.Of<IMachineService>(),
            new SyncMachineStockFromNayax(nayax ?? Mock.Of<INayaxLynxClient>(), store),
            new ApplyMachineStockSync(store),
            new ResolveMachineStockDuplicate(store));

    [Fact]
    public async Task SyncRestock_returns_the_use_case_preview()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineLastAlertsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var store = new Mock<IMachineStockEventStore>();
        store.Setup(x => x.GetImportedNayaxEventLogIdsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        store.Setup(x => x.GetUnprocessedEventsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        store.Setup(x => x.GetManualMachineRefillsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await Controller(store.Object, nayax.Object).SyncRestock(MachineId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var preview = Assert.IsType<NayaxMachineStockSyncPreviewDto>(ok.Value);
        Assert.Equal(MachineId, preview.MachineId);
        Assert.Equal("No new Nayax stock-adjustment alerts to review.", preview.Message);
    }

    [Fact]
    public async Task ApplySyncRestock_passes_the_requested_event_ids_through()
    {
        var store = new Mock<IMachineStockEventStore>();
        store.Setup(x => x.FindEventAsync(MachineId, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineStockEventState(
                7, 5001, DateTime.UtcNow, NayaxStockEventMatchStatus.Matched, null,
                NayaxStockEventProcessingStatus.Unprocessed, 200, 2, null, NayaxDuplicateResolution.None));
        store.Setup(x => x.FindStorageProductAsync(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NayaxStockSyncProduct(200, "Coke 375mL", 10));
        store.Setup(x => x.GetManualMachineRefillsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        store.Setup(x => x.ApplyRefillAsync(
                7, MachineId, 5001, 200, 2, NayaxDuplicateResolution.None, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineRefillApplication(true, 99));

        var result = await Controller(store.Object)
            .ApplySyncRestock(MachineId, new NayaxStockEventApplyRequestDto([7]), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<NayaxMachineStockApplyResponseDto>(ok.Value);
        var applied = Assert.Single(response.Results);
        Assert.Equal(7, applied.EventId);
        Assert.Equal(NayaxStockEventApplyOutcome.Applied, applied.Outcome);
        Assert.Equal(99, applied.StockAdjustmentId);
        store.VerifyAll();
    }
}
