using Inventory.Application.Suppliers;
using InventoryApi.Controllers;
using InventoryApi.DTOs;
using InventoryApi.Tests.Application.Suppliers;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace InventoryApi.Tests.Controllers;

public class SuppliersControllerTests
{
    private static SuppliersController CreateController(FakeSupplierStore store) =>
        new(
            new ListSuppliers(store),
            new GetSupplier(store),
            new CreateSupplier(store),
            new UpdateSupplier(store),
            new DeleteSupplier(store));

    [Fact]
    public async Task Create_returns_created_at_action_with_response_body()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);

        var result = await controller.Create(new SupplierDto("Acme", "Jane", "555", "jane@acme.test", "1 Main St"), CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(nameof(SuppliersController.Get), created.ActionName);
        var response = Assert.IsType<SupplierResponse>(created.Value);
        Assert.Equal("Acme", response.Name);
        Assert.Equal(response.Id, created.RouteValues!["id"]);
    }

    [Fact]
    public async Task Get_returns_not_found_for_missing_supplier()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);

        var result = await controller.Get(999, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Update_returns_no_content_for_existing_supplier()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);
        var created = await controller.Create(new SupplierDto("Acme", null, null, null, null), CancellationToken.None);
        var id = (int)((CreatedAtActionResult)created.Result!).RouteValues!["id"]!;

        var result = await controller.Update(id, new SupplierDto("Acme Updated", "Jane", "555", "jane@acme.test", "1 Main St"), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task Update_returns_not_found_for_missing_supplier()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);

        var result = await controller.Update(999, new SupplierDto("Name", null, null, null, null), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Delete_returns_no_content_for_existing_supplier()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);
        var created = await controller.Create(new SupplierDto("Acme", null, null, null, null), CancellationToken.None);
        var id = (int)((CreatedAtActionResult)created.Result!).RouteValues!["id"]!;

        var result = await controller.Delete(id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task Delete_returns_not_found_for_missing_supplier()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);

        var result = await controller.Delete(999, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }
}
