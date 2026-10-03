using Inventory.Application.Costing;
using Inventory.Application.Nayax;
using Inventory.Domain.Exceptions;
using InventoryApi.Controllers;
using InventoryApi.Tests.Application.Time;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;
using DomainBaselineSource = Inventory.Domain.Costing.InventoryCostBaselineSource;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// InventoryCostTransitionsController calls the Application transition use cases directly (issue
/// #298) and does not catch their validation failures (issue #59): each use case throws
/// DomainValidationException, which DomainExceptionHandler maps centrally to the same 400
/// ProblemDetails each action used to build directly. These tests prove every action lets that
/// exception propagate uncaught and returns 200 with the use case's result on success.
/// </summary>
public class InventoryCostTransitionsControllerTests
{
    private static readonly DateTime Now = new(2026, 9, 1, 2, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Preview_lets_domain_validation_exception_propagate()
    {
        var controller = Controller(new Mock<IInventoryCostTransitionStore>());

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            controller.Preview(new InventoryCostTransitionPreviewRequest(1, -1m, DomainBaselineSource.ManualAuthoritative), CancellationToken.None));

        Assert.Equal("The opening average unit cost cannot be negative.", exception.Message);
    }

    [Fact]
    public async Task Apply_lets_domain_validation_exception_propagate()
    {
        var store = StoreWithTransaction();
        store.Setup(x => x.FindDraftAsync(It.IsAny<Guid>(), InventoryCostTransitionPreviewScope.SingleProduct, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredInventoryCostTransitionDraft(Guid.NewGuid(), "{}", Now.AddMinutes(5), Now));
        var controller = Controller(store);

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            controller.Apply(new ApplyInventoryCostTransitionRequest(Guid.NewGuid(), true), CancellationToken.None));

        Assert.Equal("This transition preview has already been applied.", exception.Message);
    }

    [Fact]
    public async Task PreviewAll_lets_domain_validation_exception_propagate()
    {
        var store = new Mock<IInventoryCostTransitionStore>();
        store.Setup(x => x.ListProductsWithoutBaselineAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var controller = Controller(store);

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            controller.PreviewAll(new InventoryCostTransitionBatchPreviewRequest(DomainBaselineSource.ManualAuthoritative), CancellationToken.None));

        Assert.Equal("All products already have an inventory-cost transition baseline.", exception.Message);
    }

    [Fact]
    public async Task ApplyAll_lets_domain_validation_exception_propagate()
    {
        var controller = Controller(StoreWithTransaction());

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            controller.ApplyAll(new ApplyInventoryCostTransitionRequest(Guid.NewGuid(), true), CancellationToken.None));

        Assert.Equal("The all-products transition preview does not exist. Run the preview again.", exception.Message);
    }

    [Fact]
    public async Task Preview_returns_ok_with_the_use_case_preview()
    {
        var store = new Mock<IInventoryCostTransitionStore>();
        store.Setup(x => x.GetProductAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InventoryCostTransitionProduct(10, "Snack", 3, 1m));
        store.Setup(x => x.SumPhysicalMovementsAsync(It.IsAny<IReadOnlyCollection<long>>(), Now, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<long, int> { [10] = 3 });
        var controller = Controller(store);

        var result = await controller.Preview(
            new InventoryCostTransitionPreviewRequest(10, 2m, DomainBaselineSource.ManualAuthoritative), CancellationToken.None);

        var preview = Assert.IsType<InventoryCostTransitionPreview>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(3, preview.OpeningCostingQuantity);
        Assert.Equal(6m, preview.InventoryValue);
        store.Verify(x => x.AddDraft(It.Is<NewInventoryCostTransitionDraft>(d => d.Id == preview.PreviewId && d.ProductId == 10)));
        store.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()));
    }

    private static Mock<IInventoryCostTransitionStore> StoreWithTransaction()
    {
        var store = new Mock<IInventoryCostTransitionStore>();
        store.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Mock<IInventoryCostTransitionTransaction>().Object);
        return store;
    }

    private static InventoryCostTransitionsController Controller(Mock<IInventoryCostTransitionStore> store)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var rebuild = new Mock<IRebuildProductCost>(MockBehavior.Strict);
        var clock = new FakeClock(Now);
        return new InventoryCostTransitionsController(
            new PreviewInventoryCostTransition(store.Object, nayax.Object, clock),
            new ApplyInventoryCostTransition(store.Object, nayax.Object, rebuild.Object, clock),
            new PreviewAllInventoryCostTransitions(store.Object, nayax.Object, clock),
            new ApplyAllInventoryCostTransitions(store.Object, nayax.Object, rebuild.Object, clock));
    }
}
