using Inventory.Application.SupplierOrders;
using Inventory.Domain.Exceptions;
using InventoryApi.Controllers;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// SupplierOrdersController.Create no longer catches its use case's validation failures
/// (issue #59): CreateSupplierOrder throws DomainValidationException, which DomainExceptionHandler
/// maps centrally to the same 400 ProblemDetails this action used to build directly. These tests
/// prove the action lets that exception propagate uncaught, and that its unrelated
/// not-found/bad-request behaviour is unchanged.
///
/// Since issue #304 the controller injects the Inventory.Application.SupplierOrders use cases
/// directly, so these tests drive it through a mocked <see cref="ISupplierOrderStore"/> - the
/// narrow persistence port - instead of a mocked delegator, and assert the API-owned
/// <see cref="SupplierOrderResponse"/> it now returns.
/// </summary>
public class SupplierOrdersControllerTests
{
    private static SupplierOrderCreateDto CreateDto() =>
        new(1, DateTime.UtcNow, null, null, null, [new SupplierOrderLineCreateDto(1, 5)]);

    private static SupplierOrdersController CreateController(ISupplierOrderStore store) =>
        new(
            new ListActiveSupplierOrders(store),
            new GetSupplierOrder(store),
            new CreateSupplierOrder(store),
            new CancelSupplierOrder(store));

    /// <summary>
    /// A store that accepts the supplier and the products, so only the behaviour under test
    /// decides the outcome.
    /// </summary>
    private static Mock<ISupplierOrderStore> StoreWithKnownSupplierAndProducts()
    {
        var store = new Mock<ISupplierOrderStore>();
        store.Setup(s => s.SupplierExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        store.Setup(s => s.AllProductsExistAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return store;
    }

    [Fact]
    public async Task Create_lets_domain_validation_exception_propagate()
    {
        var store = StoreWithKnownSupplierAndProducts();
        store.Setup(s => s.CreateAsync(It.IsAny<SupplierOrderCreateFields>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainValidationException("Ordered quantity must be positive."));

        await Assert.ThrowsAsync<DomainValidationException>(() => CreateController(store.Object).Create(CreateDto()));
    }

    [Fact]
    public async Task Create_still_returns_bad_request_when_the_supplier_is_unknown()
    {
        var store = StoreWithKnownSupplierAndProducts();
        store.Setup(s => s.SupplierExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateController(store.Object).Create(CreateDto());

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Invalid supplier.", badRequest.Value);
        store.Verify(s => s.CreateAsync(It.IsAny<SupplierOrderCreateFields>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetById_still_returns_not_found_when_missing()
    {
        var store = new Mock<ISupplierOrderStore>();
        store.Setup(s => s.FindByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync((SupplierOrderRecord?)null);

        var result = await CreateController(store.Object).GetById(1);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetById_returns_the_api_owned_response_for_a_known_order()
    {
        var store = new Mock<ISupplierOrderStore>();
        store.Setup(s => s.FindByIdAsync(9, It.IsAny<CancellationToken>())).ReturnsAsync(Record());

        var result = await CreateController(store.Object).GetById(9);

        var order = Assert.IsType<SupplierOrderResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(9, order.Id);
        Assert.Equal("Acme", order.Supplier!.Name);
        var line = Assert.Single(order.Lines);
        Assert.Equal("Coke", line.Product.Name);
        Assert.Equal(18m, line.OutstandingQuantity);
    }

    [Fact]
    public async Task GetActive_returns_the_api_owned_responses()
    {
        var store = new Mock<ISupplierOrderStore>();
        store.Setup(s => s.ListActiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Record()]);

        var result = await CreateController(store.Object).GetActive();

        var orders = Assert.IsAssignableFrom<IEnumerable<SupplierOrderResponse>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(9, Assert.Single(orders).Id);
    }

    [Fact]
    public async Task Create_returns_created_at_the_listing_route_with_the_api_owned_response()
    {
        var store = StoreWithKnownSupplierAndProducts();
        store.Setup(s => s.CreateAsync(It.IsAny<SupplierOrderCreateFields>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Record());

        var result = await CreateController(store.Object).Create(CreateDto());

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(nameof(SupplierOrdersController.GetActive), created.ActionName);
        Assert.Equal(9, created.RouteValues!["id"]);
        Assert.Equal(9, Assert.IsType<SupplierOrderResponse>(created.Value).Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancel_maps_the_use_case_result_to_no_content_or_not_found(bool cancelled)
    {
        var store = new Mock<ISupplierOrderStore>();
        store.Setup(s => s.CancelAsync(9, It.IsAny<CancellationToken>())).ReturnsAsync(cancelled);

        var result = await CreateController(store.Object).Cancel(9);

        if (cancelled)
        {
            Assert.IsType<NoContentResult>(result);
        }
        else
        {
            Assert.IsType<NotFoundResult>(result);
        }
    }

    private static SupplierOrderRecord Record() => new(
        9,
        BusinessId: 1,
        4,
        new SupplierOrderSupplierRecord(4, "Acme", null, null, null, null),
        new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        null,
        null,
        null,
        Inventory.Domain.SupplierOrders.SupplierOrderStatus.PartiallyReceived,
        new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        [
            new SupplierOrderLineRecord(
                11,
                9,
                3,
                new SupplierOrderProductSummaryRecord(
                    3, "Coke", null, null, 3.50m, 1.25m, null, null, 4, 10, 20, "can", true,
                    new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    null,
                    null),
                24m,
                6m,
                0.50m,
                null),
        ]);
}
