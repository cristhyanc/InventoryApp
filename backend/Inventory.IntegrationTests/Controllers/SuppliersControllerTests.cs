using Inventory.Application.Suppliers;
using Inventory.Domain.Gst;
using Inventory.Domain.Suppliers;
using InventoryApi.Controllers;
using InventoryApi.DTOs;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Suppliers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace InventoryApi.Tests.Controllers;

public class SuppliersControllerTests
{
    /// <summary>The business every controller test here runs as, resolved from a membership.</summary>
    private const int BusinessId = 11;

    private static SuppliersController CreateController(
        FakeSupplierStore store,
        FakeAvailableSupplierPresetStore? presets = null)
    {
        var available = presets ?? new FakeAvailableSupplierPresetStore();
        var currentBusiness = new FakeCurrentBusinessProvider(BusinessId);

        return new(
            new ListSuppliers(store),
            new GetSupplier(store),
            new CreateSupplier(store),
            new UpdateSupplier(store),
            new DeleteSupplier(store),
            new SetSupplierNotUsed(store),
            new ListAvailableSupplierPresets(currentBusiness, available),
            new AddSupplierFromPreset(currentBusiness, available),
            new GetSupplierGstDefaults(store),
            new SetSupplierGstDefaults(store));
    }

    [Fact]
    public async Task Create_returns_created_at_action_with_response_body()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);

        var result = await controller.Create(new SupplierDto("Acme", "Jane", "555", "jane@acme.test", "1 Main St", "  https://acme.test/trade  "), CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(nameof(SuppliersController.Get), created.ActionName);
        var response = Assert.IsType<SupplierDetailResponse>(created.Value);
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
        var supplier = Assert.IsType<SupplierDetailResponse>(
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

        var created = Assert.IsType<SupplierDetailResponse>(
            ((CreatedAtActionResult)(await controller.Create(
                new SupplierDto("Acme", null, null, null, null, "https://acme.test/trade"),
                CancellationToken.None)).Result!).Value);
        var withoutUrl = await CreateSupplierAsync(controller, "Beta Supplies");

        var fetched = Assert.IsType<SupplierDetailResponse>(
            Assert.IsType<OkObjectResult>((await controller.Get(created.Id, CancellationToken.None)).Result).Value);
        Assert.Equal("https://acme.test/trade", fetched.Url);

        var fetchedWithout = Assert.IsType<SupplierDetailResponse>(
            Assert.IsType<OkObjectResult>((await controller.Get(withoutUrl, CancellationToken.None)).Result).Value);
        Assert.Null(fetchedWithout.Url);

        var all = Assert.IsAssignableFrom<IEnumerable<SupplierDetailResponse>>(
            Assert.IsType<OkObjectResult>((await controller.GetAll(CancellationToken.None, includeHidden: true)).Result).Value);
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
        var existing = Assert.IsType<SupplierDetailResponse>(
            ((CreatedAtActionResult)(await controller.Create(
                new SupplierDto("Acme", null, null, null, null, "https://acme.test"),
                CancellationToken.None)).Result!).Value);

        var createResult = await controller.Create(
            new SupplierDto("Rogue", null, null, null, null, submitted), CancellationToken.None);
        var updateResult = await controller.Update(
            existing.Id, new SupplierDto("Acme", null, null, null, null, submitted), CancellationToken.None);

        AssertUrlProblem(Assert.IsType<BadRequestObjectResult>(createResult.Result), SupplierUrlPolicy.InvalidMessage);
        AssertUrlProblem(Assert.IsType<BadRequestObjectResult>(updateResult), SupplierUrlPolicy.InvalidMessage);

        var all = Assert.IsAssignableFrom<IEnumerable<SupplierDetailResponse>>(
            Assert.IsType<OkObjectResult>((await controller.GetAll(CancellationToken.None, includeHidden: true)).Result).Value);
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
        var fetched = Assert.IsType<SupplierDetailResponse>(
            Assert.IsType<OkObjectResult>((await controller.Get(id, CancellationToken.None)).Result).Value);
        Assert.Equal(("Acme Renamed", (string?)null), (fetched.Name, fetched.Url));
    }

    /// <summary>
    /// What a supplier a business created itself publishes (issue #934): not linked, not hidden,
    /// and both writes allowed - the state every supplier was in before this issue.
    /// </summary>
    [Fact]
    public async Task An_unlinked_supplier_publishes_an_unlocked_link_state()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);
        var id = await CreateSupplierAsync(controller, "Acme");

        var fetched = Fetch(await controller.Get(id, CancellationToken.None));

        Assert.Equal(
            (false, false, true, true),
            (fetched.IsLinked, fetched.IsHidden, fetched.CanRename, fetched.CanDelete));
    }

    /// <summary>
    /// And what a copy of a platform preset publishes: linked, with the two writes the link locks
    /// reported as unavailable, on the list and on the detail alike.
    /// </summary>
    [Fact]
    public async Task A_linked_supplier_publishes_its_lock_state_on_the_list_and_the_detail()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);
        var linked = store.AddLinked("Coles", "https://coles.test", supplierPresetId: 3);

        var fetched = Fetch(await controller.Get(linked.Id, CancellationToken.None));
        Assert.Equal(
            (true, false, false, false),
            (fetched.IsLinked, fetched.IsHidden, fetched.CanRename, fetched.CanDelete));

        var listed = Assert.Single(await ListAsync(controller, includeHidden: true));
        Assert.Equal(
            ("Coles", true, false, false, false),
            (listed.Name, listed.IsLinked, listed.IsHidden, listed.CanRename, listed.CanDelete));
    }

    /// <summary>
    /// The refusals the API gives for a linked copy: <c>400</c> on a rename, reported against
    /// <c>name</c>; <c>400</c> on a website change, reported against <c>url</c>; and <c>400</c> on
    /// a delete. The supplier is still there, unchanged, afterwards.
    /// </summary>
    [Fact]
    public async Task A_linked_supplier_refuses_a_rename_a_website_change_and_a_delete()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);
        var linked = store.AddLinked("Coles", "https://coles.test", supplierPresetId: 3);

        var renamed = await controller.Update(
            linked.Id,
            new SupplierDto("Coles Group", null, null, null, null, "https://coles.test"),
            CancellationToken.None);
        AssertFieldProblem(
            Assert.IsType<BadRequestObjectResult>(renamed), "name", SupplierPresetLinkPolicy.RenameRefusedMessage);

        var rewebbed = await controller.Update(
            linked.Id,
            new SupplierDto("Coles", null, null, null, null, "https://elsewhere.test"),
            CancellationToken.None);
        AssertFieldProblem(
            Assert.IsType<BadRequestObjectResult>(rewebbed), "url", SupplierPresetLinkPolicy.WebsiteRefusedMessage);

        var deleted = await controller.Delete(linked.Id, CancellationToken.None);
        AssertRefusalProblem(
            Assert.IsType<BadRequestObjectResult>(deleted), SupplierPresetLinkPolicy.DeleteRefusedMessage);

        var unchanged = Fetch(await controller.Get(linked.Id, CancellationToken.None));
        Assert.Equal(("Coles", "https://coles.test"), (unchanged.Name, unchanged.Url));
    }

    /// <summary>
    /// The contact details on a linked copy save normally, with the stored name and website
    /// submitted alongside them exactly as the Suppliers form sends them.
    /// </summary>
    [Fact]
    public async Task A_linked_suppliers_contact_details_still_save()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);
        var linked = store.AddLinked("Coles", "https://coles.test", supplierPresetId: 3);

        var result = await controller.Update(
            linked.Id,
            new SupplierDto("Coles", "Pat", "555", "pat@coles.test", "1 Road", "https://coles.test"),
            CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        var fetched = Fetch(await controller.Get(linked.Id, CancellationToken.None));
        Assert.Equal(
            ("Pat", "555", "pat@coles.test", "1 Road"),
            (fetched.ContactName, fetched.Phone, fetched.Email, fetched.Address));
        Assert.True(fetched.IsLinked);
    }

    /// <summary>
    /// A linked copy's GST defaults are the business's own accounting settings and are untouched by
    /// the link (issue #934, the owner's 27 October 2026 decision).
    /// </summary>
    [Fact]
    public async Task A_linked_suppliers_gst_defaults_still_save()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);
        var linked = store.AddLinked("Coles", null, supplierPresetId: 3);

        var saved = await controller.SetGstDefaults(
            linked.Id,
            new SupplierGstDefaultsDto(GstClassification.Taxable, GstClassification.Taxable, GstRules.None),
            CancellationToken.None);

        Assert.IsType<NoContentResult>(saved);
        var defaults = Assert.IsType<SupplierGstDefaultsResponse>(
            Assert.IsType<OkObjectResult>((await controller.GetGstDefaults(linked.Id, CancellationToken.None)).Result).Value);
        Assert.Equal(GstClassification.Taxable, defaults.ProductLineGstDefault);
    }

    /// <summary>
    /// The "Not used" switch end to end: the linked copy leaves the list a picker reads, stays on
    /// the one the Suppliers page reads, is still readable by id, and comes back when the switch is
    /// cleared.
    /// </summary>
    [Fact]
    public async Task Not_used_takes_a_linked_supplier_out_of_the_picker_list_and_back()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);
        var linked = store.AddLinked("Coles", null, supplierPresetId: 3);
        await CreateSupplierAsync(controller, "Acme");

        Assert.IsType<NoContentResult>(
            await controller.SetNotUsed(linked.Id, new SupplierNotUsedDto(true), CancellationToken.None));

        Assert.Equal(
            ["Acme"],
            (await ListAsync(controller, includeHidden: false)).Select(supplier => supplier.Name));
        var page = await ListAsync(controller, includeHidden: true);
        Assert.Equal(["Acme", "Coles"], page.Select(supplier => supplier.Name));
        Assert.True(page.Single(supplier => supplier.Name == "Coles").IsHidden);
        Assert.True(Fetch(await controller.Get(linked.Id, CancellationToken.None)).IsHidden);

        Assert.IsType<NoContentResult>(
            await controller.SetNotUsed(linked.Id, new SupplierNotUsedDto(false), CancellationToken.None));
        Assert.Equal(
            ["Acme", "Coles"],
            (await ListAsync(controller, includeHidden: false)).Select(supplier => supplier.Name));
    }

    /// <summary>
    /// A picker asks for the list without saying anything about hidden suppliers, and must not get
    /// them: the default is the fail-safe direction, because a client that forgot the parameter
    /// would otherwise offer a supplier the business has taken out of use.
    /// </summary>
    [Fact]
    public async Task The_default_list_leaves_out_the_suppliers_marked_not_used()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);
        var linked = store.AddLinked("Coles", null, supplierPresetId: 3);
        await CreateSupplierAsync(controller, "Acme");
        await controller.SetNotUsed(linked.Id, new SupplierNotUsedDto(true), CancellationToken.None);

        var defaulted = Assert.IsAssignableFrom<IEnumerable<SupplierDetailResponse>>(
            Assert.IsType<OkObjectResult>((await controller.GetAll(CancellationToken.None)).Result).Value);

        Assert.Equal(["Acme"], defaulted.Select(supplier => supplier.Name));
    }

    [Fact]
    public async Task Not_used_is_refused_for_a_supplier_the_business_created_itself()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);
        var id = await CreateSupplierAsync(controller, "Acme");

        var result = await controller.SetNotUsed(id, new SupplierNotUsedDto(true), CancellationToken.None);

        AssertRefusalProblem(
            Assert.IsType<BadRequestObjectResult>(result), SupplierPresetLinkPolicy.HideRefusedMessage);
        Assert.False(Fetch(await controller.Get(id, CancellationToken.None)).IsHidden);
    }

    [Fact]
    public async Task Not_used_returns_not_found_for_a_missing_supplier()
    {
        var store = new FakeSupplierStore();
        var controller = CreateController(store);

        Assert.IsType<NotFoundResult>(
            await controller.SetNotUsed(999, new SupplierNotUsedDto(true), CancellationToken.None));
    }

    /// <summary>
    /// The "Available to add" list (issue #935) publishes the records that serve this business's
    /// state and that it holds no copy of, with the id, the name and the website - and nothing
    /// else, which is what bounds this one member-facing read of a platform table.
    /// </summary>
    [Fact]
    public async Task Available_presets_publishes_the_id_name_and_website_of_the_records_that_serve_this_business()
    {
        var presets = new FakeAvailableSupplierPresetStore()
            .WithPreset(1, "Coles", "https://www.coles.com.au", servesNationwide: true)
            .WithPreset(2, "JB Metro South Coast & ACT", "https://jbmetro.test", servesNationwide: false, servedStates: "NSW,ACT")
            .WithPreset(3, "Perth Wholesale", null, servesNationwide: false, servedStates: "WA")
            .WithPreset(4, "Costco", null, isEnabled: false, servesNationwide: true)
            .WithCopyOf(1);
        presets.BusinessState = "ACT";
        var controller = CreateController(new FakeSupplierStore(), presets);

        var available = Assert.IsAssignableFrom<IEnumerable<AvailableSupplierPresetResponse>>(
            Assert.IsType<OkObjectResult>(
                (await controller.GetAvailablePresets(CancellationToken.None)).Result).Value)
            .ToList();

        var offered = Assert.Single(available);
        Assert.Equal(new AvailableSupplierPresetResponse(2, "JB Metro South Coast & ACT", "https://jbmetro.test"), offered);

        // The business the list was asked for is the resolved one, never a value a request carried.
        Assert.Equal([BusinessId], presets.BusinessIds.Distinct());
    }

    /// <summary>
    /// Adding one creates the linked copy and answers <c>201</c> with it, pointing at the supplier
    /// resource the new row has: the name and the website are the record's, and the copy arrives
    /// with no contact detail, not hidden, and locked by issue #934's rules.
    /// </summary>
    [Fact]
    public async Task Adding_a_record_creates_the_linked_copy_and_reports_it()
    {
        var presets = new FakeAvailableSupplierPresetStore()
            .WithPreset(2, "JB Metro South Coast & ACT", "https://jbmetro.test", servesNationwide: false, servedStates: "NSW,ACT");
        presets.BusinessState = "ACT";
        var controller = CreateController(new FakeSupplierStore(), presets);

        var result = await controller.AddFromPreset(2, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(nameof(SuppliersController.Get), created.ActionName);
        var response = Assert.IsType<SupplierDetailResponse>(created.Value);
        Assert.Equal("JB Metro South Coast & ACT", response.Name);
        Assert.Equal("https://jbmetro.test", response.Url);
        Assert.Equal(response.Id, created.RouteValues!["id"]);
        Assert.True(response.IsLinked);
        Assert.False(response.IsHidden);
        Assert.False(response.CanRename);
        Assert.False(response.CanDelete);
        Assert.Null(response.Phone);
        Assert.Equal([2], presets.Copies);
    }

    /// <summary>
    /// A record that does not serve this business, one that is no longer suggested, and one that
    /// does not exist are one answer - <c>404</c>, with nothing written - so a member learns nothing
    /// about the platform's list beyond what the list told them.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(999)]
    public async Task Adding_a_record_this_business_is_not_offered_is_not_found(int presetId)
    {
        var presets = new FakeAvailableSupplierPresetStore()
            .WithPreset(3, "Perth Wholesale", null, servesNationwide: false, servedStates: "WA")
            .WithPreset(4, "Costco", null, isEnabled: false, servesNationwide: true);
        presets.BusinessState = "ACT";
        var controller = CreateController(new FakeSupplierStore(), presets);

        Assert.IsType<NotFoundResult>((await controller.AddFromPreset(presetId, CancellationToken.None)).Result);
        Assert.Empty(presets.Copies);
    }

    /// <summary>
    /// Adding the same record twice - a double-click, a second tab - is a <c>409</c> carrying the
    /// rule's own sentence, and leaves exactly one copy. Never a second row, and never a <c>500</c>.
    /// </summary>
    [Fact]
    public async Task Adding_the_same_record_twice_is_a_conflict_and_leaves_one_copy()
    {
        var presets = new FakeAvailableSupplierPresetStore().WithPreset(1, "Coles");
        presets.BusinessState = "ACT";
        var controller = CreateController(new FakeSupplierStore(), presets);

        Assert.IsType<CreatedAtActionResult>((await controller.AddFromPreset(1, CancellationToken.None)).Result);

        var second = Assert.IsType<ConflictObjectResult>(
            (await controller.AddFromPreset(1, CancellationToken.None)).Result);
        var problem = Assert.IsType<ProblemDetails>(second.Value);
        Assert.Equal(StatusCodes.Status409Conflict, problem.Status);
        Assert.Equal(SupplierPresetMarketPolicy.AlreadyAddedMessage, problem.Detail);
        Assert.Equal(
            SupplierPresetMarketPolicy.AlreadyAddedMessage,
            Assert.Contains("message", problem.Extensions));

        Assert.Equal([1], presets.Copies);
    }

    /// <summary>
    /// The one case the in-lock read cannot see: a copy written by a writer this operation was not
    /// serialised with - another process, or a row written by hand - which the unique index of issue
    /// #934 refuses. The caller gets the same <c>409</c> the in-lock answer gives, and there is one
    /// copy; never a <c>500</c>.
    /// </summary>
    [Fact]
    public async Task An_add_the_unique_index_refuses_is_still_a_conflict()
    {
        var presets = new FakeAvailableSupplierPresetStore().WithPreset(1, "Coles");
        presets.BusinessState = "ACT";
        presets.RefuseNextCopy = true;
        var controller = CreateController(new FakeSupplierStore(), presets);

        var result = Assert.IsType<ConflictObjectResult>(
            (await controller.AddFromPreset(1, CancellationToken.None)).Result);

        Assert.Equal(
            SupplierPresetMarketPolicy.AlreadyAddedMessage,
            Assert.IsType<ProblemDetails>(result.Value).Detail);
        Assert.Equal([1], presets.Copies);
    }

    private static SupplierDetailResponse Fetch(ActionResult<SupplierDetailResponse> result) =>
        Assert.IsType<SupplierDetailResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static async Task<IReadOnlyList<SupplierDetailResponse>> ListAsync(
        SuppliersController controller, bool includeHidden) =>
        Assert.IsAssignableFrom<IEnumerable<SupplierDetailResponse>>(
            Assert.IsType<OkObjectResult>(
                (await controller.GetAll(CancellationToken.None, includeHidden)).Result).Value)
            .ToList();

    private static void AssertUrlProblem(BadRequestObjectResult result, string expectedMessage) =>
        AssertFieldProblem(result, "url", expectedMessage);

    private static void AssertFieldProblem(BadRequestObjectResult result, string field, string expectedMessage)
    {
        var problem = Assert.IsType<ValidationProblemDetails>(result.Value);
        var messages = Assert.Contains(field, problem.Errors);
        Assert.Equal(expectedMessage, Assert.Single(messages));
    }

    /// <summary>
    /// A refusal of the request rather than of a field: the sentence is in <c>detail</c> and in the
    /// <c>message</c> extension the Angular error handling reads.
    /// </summary>
    private static void AssertRefusalProblem(BadRequestObjectResult result, string expectedMessage)
    {
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal(expectedMessage, problem.Detail);
        Assert.Equal(expectedMessage, Assert.Contains("message", problem.Extensions));
    }

    private static async Task<int> CreateSupplierAsync(SuppliersController controller, string name)
    {
        var created = await controller.Create(new SupplierDto(name, null, null, null, null, null), CancellationToken.None);
        return (int)((CreatedAtActionResult)created.Result!).RouteValues!["id"]!;
    }
}
