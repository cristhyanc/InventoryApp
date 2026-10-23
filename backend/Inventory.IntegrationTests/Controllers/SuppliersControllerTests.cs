using Inventory.Application.Suppliers;
using Inventory.Domain.Gst;
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
            new DeleteSupplier(store),
            new GetSupplierGstDefaults(store),
            new SetSupplierGstDefaults(store));

    [Fact]
    public async Task Create_returns_created_at_action_with_response_body()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);

        var result = await controller.Create(new SupplierDto("Acme", "Jane", "555", "jane@acme.test", "1 Main St", "  https://acme.test/trade  "), CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(nameof(SuppliersController.Get), created.ActionName);
        var response = Assert.IsType<SupplierResponse>(created.Value);
        Assert.Equal("Acme", response.Name);
        Assert.Equal("https://acme.test/trade", response.Url);
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
        var created = await controller.Create(new SupplierDto("Acme", null, null, null, null, null), CancellationToken.None);
        var id = (int)((CreatedAtActionResult)created.Result!).RouteValues!["id"]!;

        var result = await controller.Update(id, new SupplierDto("Acme Updated", "Jane", "555", "jane@acme.test", "1 Main St", null), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task Update_returns_not_found_for_missing_supplier()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);

        var result = await controller.Update(999, new SupplierDto("Name", null, null, null, null, null), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Delete_returns_no_content_for_existing_supplier()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);
        var created = await controller.Create(new SupplierDto("Acme", null, null, null, null, null), CancellationToken.None);
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

    /// <summary>
    /// The GST defaults resource (issue #430), end to end through the controller: a new supplier has
    /// no default, each of the three round-trips on its own, and the supplier's own details are not
    /// disturbed.
    /// </summary>
    [Fact]
    public async Task Gst_defaults_round_trip_through_their_own_resource()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);
        var id = await CreateSupplierAsync(controller, "Acme");

        var initial = Assert.IsType<SupplierGstDefaultsResponse>(
            Assert.IsType<OkObjectResult>((await controller.GetGstDefaults(id, CancellationToken.None)).Result).Value);
        Assert.Equal(
            (id, GstRules.None, GstRules.None, GstRules.None),
            (initial.SupplierId, initial.ProductLineGstDefault, initial.DeliveryGstDefault, initial.PackageGstDefault));

        var saved = await controller.SetGstDefaults(
            id,
            new SupplierGstDefaultsDto(GstClassification.GstFree, GstClassification.Taxable, GstRules.None),
            CancellationToken.None);

        Assert.IsType<NoContentResult>(saved);
        var updated = Assert.IsType<SupplierGstDefaultsResponse>(
            Assert.IsType<OkObjectResult>((await controller.GetGstDefaults(id, CancellationToken.None)).Result).Value);
        Assert.Equal(
            (GstClassification.GstFree, GstClassification.Taxable, GstRules.None),
            (updated.ProductLineGstDefault, updated.DeliveryGstDefault, updated.PackageGstDefault));
        var supplier = Assert.IsType<SupplierResponse>(
            Assert.IsType<OkObjectResult>((await controller.Get(id, CancellationToken.None)).Result).Value);
        Assert.Equal("Acme", supplier.Name);
    }

    [Fact]
    public async Task Gst_defaults_are_not_found_for_a_missing_supplier()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);

        Assert.IsType<NotFoundResult>((await controller.GetGstDefaults(999, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>(await controller.SetGstDefaults(
            999, new SupplierGstDefaultsDto(GstClassification.Taxable, GstRules.None, GstRules.None), CancellationToken.None));
    }

    /// <summary>
    /// An undefined default is answered <c>400</c> with the vocabulary's own message, and the
    /// supplier keeps the defaults it already had.
    /// </summary>
    [Fact]
    public async Task An_undefined_gst_default_is_rejected_and_changes_nothing()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);
        var id = await CreateSupplierAsync(controller, "Acme");
        await controller.SetGstDefaults(
            id,
            new SupplierGstDefaultsDto(GstClassification.Taxable, GstClassification.Taxable, GstClassification.Taxable),
            CancellationToken.None);

        var result = await controller.SetGstDefaults(
            id,
            new SupplierGstDefaultsDto((GstClassification)999, GstRules.None, GstRules.None),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(GstRules.UnsupportedRuleMessage, badRequest.Value);
        var unchanged = Assert.IsType<SupplierGstDefaultsResponse>(
            Assert.IsType<OkObjectResult>((await controller.GetGstDefaults(id, CancellationToken.None)).Result).Value);
        Assert.Equal(
            (GstClassification.Taxable, GstClassification.Taxable, GstClassification.Taxable),
            (unchanged.ProductLineGstDefault, unchanged.DeliveryGstDefault, unchanged.PackageGstDefault));
    }

    /// <summary>
    /// The optional website URL (issue #847) round-trips through create, get and list, and an
    /// absent one is published as <c>null</c> rather than omitted.
    /// </summary>
    [Fact]
    public async Task The_website_url_round_trips_through_create_get_and_list()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);

        var created = Assert.IsType<SupplierResponse>(
            ((CreatedAtActionResult)(await controller.Create(
                new SupplierDto("Acme", null, null, null, null, "https://acme.test/trade"),
                CancellationToken.None)).Result!).Value);
        var withoutUrl = await CreateSupplierAsync(controller, "Beta Supplies");

        var fetched = Assert.IsType<SupplierResponse>(
            Assert.IsType<OkObjectResult>((await controller.Get(created.Id, CancellationToken.None)).Result).Value);
        Assert.Equal("https://acme.test/trade", fetched.Url);

        var fetchedWithout = Assert.IsType<SupplierResponse>(
            Assert.IsType<OkObjectResult>((await controller.Get(withoutUrl, CancellationToken.None)).Result).Value);
        Assert.Null(fetchedWithout.Url);

        var all = Assert.IsAssignableFrom<IEnumerable<SupplierResponse>>(
            Assert.IsType<OkObjectResult>((await controller.GetAll(CancellationToken.None)).Result).Value);
        Assert.Equal(
            new[] { ("Acme", "https://acme.test/trade"), ("Beta Supplies", (string?)null) },
            all.Select(supplier => (supplier.Name, supplier.Url)));
    }

    /// <summary>
    /// A URL that is not an absolute http(s) address is refused with a field-level <c>400</c>, and
    /// nothing is written: no supplier is created, and an existing one keeps the URL it had.
    /// </summary>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("coles.com.au")]
    [InlineData("file:///etc/passwd")]
    public async Task An_unsafe_or_relative_website_url_is_refused_with_a_field_level_400(string submitted)
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);
        var existing = Assert.IsType<SupplierResponse>(
            ((CreatedAtActionResult)(await controller.Create(
                new SupplierDto("Acme", null, null, null, null, "https://acme.test"),
                CancellationToken.None)).Result!).Value);

        var createResult = await controller.Create(
            new SupplierDto("Rogue", null, null, null, null, submitted), CancellationToken.None);
        var updateResult = await controller.Update(
            existing.Id, new SupplierDto("Acme", null, null, null, null, submitted), CancellationToken.None);

        AssertUrlProblem(Assert.IsType<BadRequestObjectResult>(createResult.Result), SupplierUrlPolicy.InvalidMessage);
        AssertUrlProblem(Assert.IsType<BadRequestObjectResult>(updateResult), SupplierUrlPolicy.InvalidMessage);

        var all = Assert.IsAssignableFrom<IEnumerable<SupplierResponse>>(
            Assert.IsType<OkObjectResult>((await controller.GetAll(CancellationToken.None)).Result).Value);
        var unchanged = Assert.Single(all);
        Assert.Equal(("Acme", "https://acme.test"), (unchanged.Name, unchanged.Url));
    }

    [Fact]
    public async Task An_over_long_website_url_is_refused_with_the_length_message()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);
        var tooLong = "https://coles.com.au/" + new string('a', SupplierUrlPolicy.MaxLength);

        var result = await controller.Create(
            new SupplierDto("Acme", null, null, null, null, tooLong), CancellationToken.None);

        AssertUrlProblem(Assert.IsType<BadRequestObjectResult>(result.Result), SupplierUrlPolicy.TooLongMessage);
    }

    /// <summary>
    /// A supplier stored with no URL updates without the caller touching the field: the submitted
    /// <c>null</c> is "no website", not a refusal.
    /// </summary>
    [Fact]
    public async Task A_supplier_with_no_url_updates_without_touching_the_field()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);
        var id = await CreateSupplierAsync(controller, "Acme");

        var result = await controller.Update(
            id, new SupplierDto("Acme Renamed", "Jane", null, null, null, null), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        var fetched = Assert.IsType<SupplierResponse>(
            Assert.IsType<OkObjectResult>((await controller.Get(id, CancellationToken.None)).Result).Value);
        Assert.Equal(("Acme Renamed", (string?)null), (fetched.Name, fetched.Url));
    }

    private static void AssertUrlProblem(BadRequestObjectResult result, string expectedMessage)
    {
        var problem = Assert.IsType<ValidationProblemDetails>(result.Value);
        var messages = Assert.Contains("url", problem.Errors);
        Assert.Equal(expectedMessage, Assert.Single(messages));
    }

    private static async Task<int> CreateSupplierAsync(SuppliersController controller, string name)
    {
        var created = await controller.Create(new SupplierDto(name, null, null, null, null, null), CancellationToken.None);
        return (int)((CreatedAtActionResult)created.Result!).RouteValues!["id"]!;
    }
}
