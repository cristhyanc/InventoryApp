using InventoryApi.Controllers;
using InventoryApi.DTOs;
using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The Sync Restock endpoints (issue #183) are thin bindings over
/// <see cref="INayaxMachineStockSyncService"/>: no AppDbContext, Nayax client, or business logic
/// lives in the controller itself.
/// </summary>
public class MachinesControllerTests
{
    [Fact]
    public async Task SyncRestock_returns_the_service_preview()
    {
        var preview = new NayaxMachineStockSyncPreviewDto(42, 1, [], [], null);
        var syncService = new Mock<INayaxMachineStockSyncService>();
        syncService.Setup(x => x.SyncAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(preview);
        var controller = new MachinesController(Mock.Of<IMachineService>(), syncService.Object);

        var result = await controller.SyncRestock(42, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(preview, ok.Value);
    }

    [Fact]
    public async Task ApplySyncRestock_passes_the_requested_event_ids_through()
    {
        var response = new NayaxMachineStockApplyResponseDto([
            new NayaxStockEventApplyResultDto(7, NayaxStockEventApplyOutcome.Applied, "Applied.", 99)
        ]);
        var syncService = new Mock<INayaxMachineStockSyncService>();
        syncService
            .Setup(x => x.ApplyAsync(
                42,
                It.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 7 })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
        var controller = new MachinesController(Mock.Of<IMachineService>(), syncService.Object);

        var result = await controller.ApplySyncRestock(42, new NayaxStockEventApplyRequestDto([7]), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(response, ok.Value);
    }
}
