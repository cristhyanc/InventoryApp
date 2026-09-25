using Inventory.Application.Exceptions;
using InventoryApi.Controllers;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services;
using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// StockController.Adjust no longer catches InsufficientStockException/DomainValidationException
/// itself (issue #59): DomainExceptionHandler now maps them centrally to the same 400
/// ProblemDetails this action used to build directly. These tests prove the action lets both
/// exception types propagate uncaught, and that its unrelated not-found/bad-request behaviour is
/// unchanged.
/// </summary>
public class StockControllerTests
{
    private static StockAdjustmentDto AdjustDto() =>
        new(1, StockAdjustmentReason.Restock, "note", null, null, 1m);

    [Fact]
    public async Task Adjust_lets_insufficient_stock_exception_propagate()
    {
        var service = new Mock<IStockService>();
        service.Setup(x => x.Adjust(1, It.IsAny<StockAdjustmentDto>()))
            .ThrowsAsync(new InsufficientStockException(0));
        var controller = new StockController(service.Object);

        await Assert.ThrowsAsync<InsufficientStockException>(() => controller.Adjust(1, AdjustDto()));
    }

    [Fact]
    public async Task Adjust_lets_domain_validation_exception_propagate()
    {
        var service = new Mock<IStockService>();
        service.Setup(x => x.Adjust(1, It.IsAny<StockAdjustmentDto>()))
            .ThrowsAsync(new DomainValidationException("Unit cost is required for a positive Restock adjustment."));
        var controller = new StockController(service.Object);

        await Assert.ThrowsAsync<DomainValidationException>(() => controller.Adjust(1, AdjustDto()));
    }

    [Fact]
    public async Task Adjust_still_returns_bad_request_when_the_service_returns_null()
    {
        var service = new Mock<IStockService>();
        service.Setup(x => x.Adjust(1, It.IsAny<StockAdjustmentDto>())).ReturnsAsync((StockAdjustment?)null);
        var controller = new StockController(service.Object);

        var result = await controller.Adjust(1, AdjustDto());

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Invalid product or resulting quantity", badRequest.Value);
    }

    [Fact]
    public async Task History_still_returns_not_found_when_the_product_has_no_history()
    {
        var service = new Mock<IStockService>();
        service.Setup(x => x.History(1)).ReturnsAsync(Enumerable.Empty<StockAdjustment>());
        var controller = new StockController(service.Object);

        var result = await controller.History(1);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal("Product not found", notFound.Value);
    }
}
