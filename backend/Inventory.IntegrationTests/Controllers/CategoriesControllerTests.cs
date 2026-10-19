using Inventory.Application.Categories;
using InventoryApi.Controllers;
using InventoryApi.DTOs;
using InventoryApi.Tests.Application.Categories;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace InventoryApi.Tests.Controllers;

public class CategoriesControllerTests
{
    private static CategoriesController CreateController(FakeCategoryStore store) =>
        new(
            new ListCategories(store),
            new GetCategory(store),
            new CreateCategory(store),
            new UpdateCategory(store),
            new DeleteCategory(store));

    [Fact]
    public async Task GetAll_returns_ok_with_categories_ordered_by_name()
    {
        var store = new FakeCategoryStore(
        [
            new CategoryRecord(1, "Zebra", null),
            new CategoryRecord(2, "Apple", "Fruit"),
        ]);
        var controller = CreateController(store);

        var result = await controller.GetAll(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var categories = Assert.IsAssignableFrom<IEnumerable<CategoryResponse>>(ok.Value).ToList();
        Assert.Equal(new[] { "Apple", "Zebra" }, categories.Select(c => c.Name));
    }

    [Fact]
    public async Task Get_returns_ok_for_existing_category()
    {
        var store = new FakeCategoryStore([new CategoryRecord(1, "Snacks", "Salty snacks")]);
        var controller = CreateController(store);

        var result = await controller.Get(1, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<CategoryResponse>(ok.Value);
        Assert.Equal(1, response.Id);
        Assert.Equal("Snacks", response.Name);
        Assert.Equal("Salty snacks", response.Description);
    }

    [Fact]
    public async Task Get_returns_not_found_for_missing_category()
    {
        var store = new FakeCategoryStore();
        var controller = CreateController(store);

        var result = await controller.Get(999, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    /// <summary>
    /// The category response says which of its fields the business's sales provider manages, so the
    /// UI can lock them (issue #736): its name for a provider-named category, nothing for a local
    /// one.
    /// </summary>
    [Fact]
    public async Task Get_declares_which_fields_the_provider_manages()
    {
        var store = new FakeCategoryStore(
        [
            new CategoryRecord(1, "Local", null),
            new CategoryRecord(2, "Nayax Drinks", null, IsProviderManaged: true),
        ]);
        var controller = CreateController(store);

        var local = Assert.IsType<CategoryResponse>(
            Assert.IsType<OkObjectResult>((await controller.Get(1, CancellationToken.None)).Result).Value);
        var linked = Assert.IsType<CategoryResponse>(
            Assert.IsType<OkObjectResult>((await controller.Get(2, CancellationToken.None)).Result).Value);

        Assert.Empty(local.ProviderManagedFields!);
        Assert.Equal(new[] { "name" }, linked.ProviderManagedFields!);
    }

    [Fact]
    public async Task Create_returns_created_with_the_category()
    {
        var store = new FakeCategoryStore();
        var controller = CreateController(store);

        var result = await controller.Create(new CategoryDto("Drinks", "Cold"), CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<CategoryResponse>(created.Value);
        Assert.Equal(("Drinks", "Cold"), (response.Name, response.Description));
        Assert.Equal(nameof(CategoriesController.Get), created.ActionName);
    }

    [Fact]
    public async Task Create_returns_bad_request_with_the_refusal()
    {
        var store = new FakeCategoryStore { RefusalToReturn = "This business already has a category called \"Drinks\"." };
        var controller = CreateController(store);

        var result = await controller.Create(new CategoryDto("Drinks", null), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("This business already has a category called \"Drinks\".", badRequest.Value);
    }

    [Fact]
    public async Task Update_returns_no_content_and_not_found_and_bad_request()
    {
        var store = new FakeCategoryStore([new CategoryRecord(1, "Drinks", null)]);
        var controller = CreateController(store);

        Assert.IsType<NoContentResult>(
            await controller.Update(1, new CategoryDto("Cold drinks", null), CancellationToken.None));
        Assert.IsType<NotFoundResult>(
            await controller.Update(999, new CategoryDto("Drinks", null), CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(
            await controller.Update(1, new CategoryDto("  ", null), CancellationToken.None));
    }

    /// <summary>
    /// A category products still use is a conflict with the state of the data, not an invalid
    /// request, so it answers <c>409</c> with the refusal that says how many products use it.
    /// </summary>
    [Fact]
    public async Task Delete_returns_no_content_not_found_and_conflict()
    {
        var store = new FakeCategoryStore([new CategoryRecord(1, "Drinks", null)]);
        var controller = CreateController(store);

        Assert.IsType<NotFoundResult>(await controller.Delete(999, CancellationToken.None));

        store.RefusalToReturn = "This category cannot be deleted because 2 products use it.";
        var conflict = Assert.IsType<ConflictObjectResult>(await controller.Delete(1, CancellationToken.None));
        Assert.Equal("This category cannot be deleted because 2 products use it.", conflict.Value);

        store.RefusalToReturn = null;
        Assert.IsType<NoContentResult>(await controller.Delete(1, CancellationToken.None));
    }
}
