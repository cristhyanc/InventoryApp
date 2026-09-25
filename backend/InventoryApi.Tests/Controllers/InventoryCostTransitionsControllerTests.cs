using InventoryApi.Controllers;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// InventoryCostTransitionsController no longer catches InvalidOperationException itself
/// (issue #59): DomainExceptionHandler now maps it centrally to the same 400 ProblemDetails each
/// action used to build directly. These tests prove every action lets the exception propagate
/// uncaught.
/// </summary>
public class InventoryCostTransitionsControllerTests
{
    [Fact]
    public async Task Preview_lets_invalid_operation_exception_propagate()
    {
        var service = new Mock<IInventoryCostTransitionService>();
        service
            .Setup(x => x.PreviewAsync(It.IsAny<InventoryCostTransitionPreviewRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The opening average unit cost cannot be negative."));
        var controller = new InventoryCostTransitionsController(service.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.Preview(new InventoryCostTransitionPreviewRequest(1, -1m, InventoryCostBaselineSource.ManualAuthoritative), CancellationToken.None));
    }

    [Fact]
    public async Task Apply_lets_invalid_operation_exception_propagate()
    {
        var service = new Mock<IInventoryCostTransitionService>();
        service
            .Setup(x => x.ApplyAsync(It.IsAny<ApplyInventoryCostTransitionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("This transition preview has already been applied."));
        var controller = new InventoryCostTransitionsController(service.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.Apply(new ApplyInventoryCostTransitionRequest(Guid.NewGuid(), true), CancellationToken.None));
    }

    [Fact]
    public async Task PreviewAll_lets_invalid_operation_exception_propagate()
    {
        var service = new Mock<IInventoryCostTransitionService>();
        service
            .Setup(x => x.PreviewAllAsync(It.IsAny<InventoryCostTransitionBatchPreviewRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("All products already have an inventory-cost transition baseline."));
        var controller = new InventoryCostTransitionsController(service.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.PreviewAll(new InventoryCostTransitionBatchPreviewRequest(InventoryCostBaselineSource.ManualAuthoritative), CancellationToken.None));
    }

    [Fact]
    public async Task ApplyAll_lets_invalid_operation_exception_propagate()
    {
        var service = new Mock<IInventoryCostTransitionService>();
        service
            .Setup(x => x.ApplyAllAsync(It.IsAny<ApplyInventoryCostTransitionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The all-products transition preview does not exist. Run the preview again."));
        var controller = new InventoryCostTransitionsController(service.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.ApplyAll(new ApplyInventoryCostTransitionRequest(Guid.NewGuid(), true), CancellationToken.None));
    }
}
