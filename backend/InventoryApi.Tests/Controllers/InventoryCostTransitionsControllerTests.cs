using Inventory.Domain.Exceptions;
using InventoryApi.Controllers;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// InventoryCostTransitionsController no longer catches its service's validation failures
/// (issue #59): the service now throws DomainValidationException, which DomainExceptionHandler
/// maps centrally to the same 400 ProblemDetails each action used to build directly. These tests
/// prove every action lets that exception propagate uncaught.
/// </summary>
public class InventoryCostTransitionsControllerTests
{
    [Fact]
    public async Task Preview_lets_domain_validation_exception_propagate()
    {
        var service = new Mock<IInventoryCostTransitionService>();
        service
            .Setup(x => x.PreviewAsync(It.IsAny<InventoryCostTransitionPreviewRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainValidationException("The opening average unit cost cannot be negative."));
        var controller = new InventoryCostTransitionsController(service.Object);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            controller.Preview(new InventoryCostTransitionPreviewRequest(1, -1m, InventoryCostBaselineSource.ManualAuthoritative), CancellationToken.None));
    }

    [Fact]
    public async Task Apply_lets_domain_validation_exception_propagate()
    {
        var service = new Mock<IInventoryCostTransitionService>();
        service
            .Setup(x => x.ApplyAsync(It.IsAny<ApplyInventoryCostTransitionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainValidationException("This transition preview has already been applied."));
        var controller = new InventoryCostTransitionsController(service.Object);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            controller.Apply(new ApplyInventoryCostTransitionRequest(Guid.NewGuid(), true), CancellationToken.None));
    }

    [Fact]
    public async Task PreviewAll_lets_domain_validation_exception_propagate()
    {
        var service = new Mock<IInventoryCostTransitionService>();
        service
            .Setup(x => x.PreviewAllAsync(It.IsAny<InventoryCostTransitionBatchPreviewRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainValidationException("All products already have an inventory-cost transition baseline."));
        var controller = new InventoryCostTransitionsController(service.Object);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            controller.PreviewAll(new InventoryCostTransitionBatchPreviewRequest(InventoryCostBaselineSource.ManualAuthoritative), CancellationToken.None));
    }

    [Fact]
    public async Task ApplyAll_lets_domain_validation_exception_propagate()
    {
        var service = new Mock<IInventoryCostTransitionService>();
        service
            .Setup(x => x.ApplyAllAsync(It.IsAny<ApplyInventoryCostTransitionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainValidationException("The all-products transition preview does not exist. Run the preview again."));
        var controller = new InventoryCostTransitionsController(service.Object);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            controller.ApplyAll(new ApplyInventoryCostTransitionRequest(Guid.NewGuid(), true), CancellationToken.None));
    }
}
