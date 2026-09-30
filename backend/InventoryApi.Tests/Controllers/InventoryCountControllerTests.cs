using Inventory.Application.InventoryCounting;
using InventoryApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

public class InventoryCountControllerTests
{
    [Fact]
    public async Task Apply_ReturnsNotFound_WhenTheUseCaseFindsNoProduct()
    {
        var store = new Mock<IInventoryCountAdjustmentStore>();
        store.Setup(x => x.GetCurrentStockAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InventoryCountProduct?)null);
        var controller = new InventoryCountController(new ApplyInventoryCount(store.Object));

        var result = await controller.Apply(1, new InventoryCountApplyRequestDto(5, 5), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Apply_ReturnsTheUseCaseResult_OnSuccess()
    {
        var store = new Mock<IInventoryCountAdjustmentStore>();
        store.Setup(x => x.GetCurrentStockAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InventoryCountProduct(1, "Coke", 46));
        var controller = new InventoryCountController(new ApplyInventoryCount(store.Object));

        var result = await controller.Apply(1, new InventoryCountApplyRequestDto(46, 46), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<InventoryCountApplyResultDto>(ok.Value);
        Assert.Equal(InventoryCountApplyOutcome.Confirmed, dto.Outcome);
    }
}
