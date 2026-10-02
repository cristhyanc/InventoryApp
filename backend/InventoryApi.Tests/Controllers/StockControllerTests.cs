using Inventory.Application.Stock;
using Inventory.Domain.Exceptions;
using InventoryApi.Controllers;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Tests.Application.Stock;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// StockController no longer catches InsufficientStockException/DomainValidationException itself
/// (issue #59): DomainExceptionHandler maps them centrally to the same 400 ProblemDetails this
/// action used to build directly. These tests prove the action lets both exception types propagate
/// uncaught, and that its unrelated not-found/bad-request behaviour is unchanged after routing
/// through the migrated use cases (issue #282).
/// </summary>
public class StockControllerTests
{
    private static StockAdjustmentDto AdjustDto() =>
        new(1, StockAdjustmentReason.Restock, "note", null, null, 1m);

    private static StockController CreateController(FakeStockAdjustmentStore store) =>
        new(new GetStockHistory(store), new GetRestockCostSuggestion(store), new AdjustStock(store));

    [Fact]
    public async Task Adjust_lets_insufficient_stock_exception_propagate()
    {
        var store = new FakeStockAdjustmentStore { ThrowOnApply = new InsufficientStockException(0) };
        var controller = CreateController(store);

        await Assert.ThrowsAsync<InsufficientStockException>(() => controller.Adjust(1, AdjustDto(), CancellationToken.None));
    }

    [Fact]
    public async Task Adjust_lets_domain_validation_exception_propagate()
    {
        var store = new FakeStockAdjustmentStore();
        var controller = CreateController(store);
        var invalidDto = new StockAdjustmentDto(0, StockAdjustmentReason.Correction, "note", null, null, null);

        var exception = await Assert.ThrowsAsync<DomainValidationException>(
            () => controller.Adjust(1, invalidDto, CancellationToken.None));

        Assert.Equal("Correction quantity must remove stock.", exception.Message);
        Assert.False(store.ApplyCalled);
    }

    [Fact]
    public async Task Adjust_still_returns_bad_request_when_the_product_does_not_exist()
    {
        var store = new FakeStockAdjustmentStore { ProductExists = false };
        var controller = CreateController(store);

        var result = await controller.Adjust(1, AdjustDto(), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Invalid product or resulting quantity", badRequest.Value);
    }

    [Fact]
    public async Task History_still_returns_not_found_when_the_product_has_no_history()
    {
        var store = new FakeStockAdjustmentStore { ProductExists = true, History = [] };
        var controller = CreateController(store);

        var result = await controller.History(1, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal("Product not found", notFound.Value);
    }
}
