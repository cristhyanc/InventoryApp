using InventoryApi.Controllers;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// SupplierOrdersController.Create no longer catches InvalidOperationException itself
/// (issue #59): DomainExceptionHandler now maps it centrally to the same 400 ProblemDetails this
/// action used to build directly. These tests prove the action lets the exception propagate
/// uncaught, and that its unrelated not-found/bad-request behaviour is unchanged.
/// </summary>
public class SupplierOrdersControllerTests
{
    private static SupplierOrderCreateDto CreateDto() =>
        new(1, DateTime.UtcNow, null, null, null, [new SupplierOrderLineCreateDto(1, 5)]);

    [Fact]
    public async Task Create_lets_invalid_operation_exception_propagate()
    {
        var service = new Mock<ISupplierOrderService>();
        service.Setup(x => x.Create(It.IsAny<SupplierOrderCreateDto>()))
            .ThrowsAsync(new InvalidOperationException("Ordered quantity must be positive."));
        var controller = new SupplierOrdersController(service.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.Create(CreateDto()));
    }

    [Fact]
    public async Task Create_still_returns_bad_request_when_the_service_returns_null()
    {
        var service = new Mock<ISupplierOrderService>();
        service.Setup(x => x.Create(It.IsAny<SupplierOrderCreateDto>())).ReturnsAsync((SupplierOrder?)null);
        var controller = new SupplierOrdersController(service.Object);

        var result = await controller.Create(CreateDto());

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Invalid supplier.", badRequest.Value);
    }

    [Fact]
    public async Task GetById_still_returns_not_found_when_missing()
    {
        var service = new Mock<ISupplierOrderService>();
        service.Setup(x => x.GetById(1)).ReturnsAsync((SupplierOrder?)null);
        var controller = new SupplierOrdersController(service.Object);

        var result = await controller.GetById(1);

        Assert.IsType<NotFoundResult>(result.Result);
    }
}
