using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using InventoryApi.Auth.E2ETesting;
using InventoryApi.Tests.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The "Track supplier prices" switch over the real HTTP pipeline (issue #938, fourth task of #817):
/// authentication, the required scope, the <c>Inventory.Operate</c> capability policy,
/// <c>BusinessScopeMiddleware</c>, the controller, the EF adapter and relational SQLite with the real
/// migration history.
///
/// <para>Both synthetic E2E businesses are used, so "the business never sees which other businesses
/// track the same product" and "turning tracking off removes only this business's mapping" are proved
/// through the pipeline that resolves the business rather than through a hand-written business id -
/// including when business A <em>names</em> business B's product in the route, which is the one way a
/// caller could try to reach across the boundary here.</para>
///
/// <para>Every seeded E2E product carries the generic <c>"unit"</c> label, which is exactly the state
/// the issue's second criterion is about: a product the catalogue import created cannot be tracked
/// until somebody gives it a real unit label. The tests that need a trackable product relabel one
/// first, through the same <c>PUT api/products/{id}</c> an operator would use.</para>
///
/// <para>The capability, support-session and role-preview refusals belong with the agreed matrices
/// and are asserted there - <c>CapabilityAuthorizationApiTests</c>,
/// <c>SupportSessionRequestApiTests</c> and <c>RolePreviewApiTests</c> - because the E2E host's two
/// actors are both Owners and a role without the capability cannot be expressed in this fixture.</para>
/// </summary>
public sealed class ProductPriceTrackingApiTests
    : IClassFixture<ProductPriceTrackingApiTests.ApiFactory>
{
    private readonly ApiFactory _factory;

    public ProductPriceTrackingApiTests(ApiFactory factory) => _factory = factory;

    #region Turning the switch on

    /// <summary>
    /// The criterion: turning the switch on creates a mapping with status <c>requested</c> and copies
    /// the product's own <c>Name</c> and unit label as the search text. Nothing a caller submitted
    /// decides either - the body carries one boolean.
    /// </summary>
    [Fact]
    public async Task Turning_tracking_on_captures_the_products_own_name_and_unit_label()
    {
        var client = _factory.BusinessA();
        var productId = await _factory.TrackableProductAsync(client, "750mL");

        Assert.Equal(HttpStatusCode.NoContent, await SetTrackingAsync(client, productId, tracked: true));

        var state = await TrackingAsync(client, productId);
        Assert.True(state.GetProperty("tracked").GetBoolean());
        Assert.Equal(
            (int)ProductPriceTrackingStatus.Requested,
            state.GetProperty("status").GetInt32());
        Assert.Equal(
            await _factory.ProductNameAsync(productId),
            state.GetProperty("searchName").GetString());
        Assert.Equal("750mL", state.GetProperty("unitLabel").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("masterProduct").ValueKind);

        await _factory.UntrackAsync(productId);
    }

    /// <summary>
    /// Turning it on twice is the same answer, because the switch states an intention: a double click,
    /// a second tab or a retried request must not need a different one, and there is still exactly one
    /// mapping afterwards.
    /// </summary>
    [Fact]
    public async Task Turning_tracking_on_twice_leaves_one_mapping_and_the_same_answer()
    {
        var client = _factory.BusinessA();
        var productId = await _factory.TrackableProductAsync(client, "500mL");

        Assert.Equal(HttpStatusCode.NoContent, await SetTrackingAsync(client, productId, tracked: true));
        Assert.Equal(HttpStatusCode.NoContent, await SetTrackingAsync(client, productId, tracked: true));

        Assert.Equal(1, await _factory.MappingCountAsync(productId));

        await _factory.UntrackAsync(productId);
    }

    /// <summary>
    /// The criterion: tracking cannot be turned on while the unit label is the generic default, which
    /// is what every catalogue-imported product carries. The refusal is the rule's own sentence, and
    /// nothing is written.
    /// </summary>
    [Fact]
    public async Task A_product_with_the_generic_unit_label_cannot_be_tracked()
    {
        var client = _factory.BusinessA();
        var productId = await _factory.ReorderProductIdAsync();

        var response = await client.PutAsJsonAsync(
            TrackingUri(productId), new { tracked = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ProductPriceTrackingPolicy.UnitLabelNotSpecificMessage,
            problem.RootElement.GetProperty("message").GetString());
        Assert.Equal(0, await _factory.MappingCountAsync(productId));
    }

    /// <summary>
    /// And the switch is told why before it is touched: the state read publishes the eligibility
    /// answer and its sentence, decided by the same rule the write applies, so the form never words
    /// the unit-label rule a second time.
    /// </summary>
    [Fact]
    public async Task A_product_with_the_generic_unit_label_reads_as_not_trackable_with_the_reason()
    {
        var state = await TrackingAsync(_factory.BusinessA(), await _factory.ReorderProductIdAsync());

        Assert.False(state.GetProperty("canTrack").GetBoolean());
        Assert.Equal(
            ProductPriceTrackingPolicy.UnitLabelNotSpecificMessage,
            state.GetProperty("trackRefusal").GetString());
        Assert.False(state.GetProperty("tracked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("status").ValueKind);
    }

    /// <summary>A product with a real unit label reads as trackable with no reason to show.</summary>
    [Fact]
    public async Task A_product_with_a_real_unit_label_reads_as_trackable()
    {
        var client = _factory.BusinessA();
        var productId = await _factory.TrackableProductAsync(client, "375mL");

        var state = await TrackingAsync(client, productId);

        Assert.True(state.GetProperty("canTrack").GetBoolean());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("trackRefusal").ValueKind);
    }

    #endregion

    #region Turning the switch off

    /// <summary>
    /// The criterion: turning tracking off removes this business's mapping, and the shared master
    /// product and supplier listing it was matched through are untouched. Turning it off again is the
    /// same answer.
    /// </summary>
    [Fact]
    public async Task Turning_tracking_off_removes_only_this_business_s_mapping()
    {
        var client = _factory.BusinessA();
        var productId = await _factory.TrackableProductAsync(client, "1.25L");
        await SetTrackingAsync(client, productId, tracked: true);
        var (masterProductId, listingId) = await _factory.MatchAsync(
            productId, ProductPriceTrackingStatus.Matched);

        Assert.Equal(HttpStatusCode.NoContent, await SetTrackingAsync(client, productId, tracked: false));

        var state = await TrackingAsync(client, productId);
        Assert.False(state.GetProperty("tracked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("masterProduct").ValueKind);
        Assert.Equal(0, await _factory.MappingCountAsync(productId));
        Assert.True(await _factory.MasterProductExistsAsync(masterProductId));
        Assert.True(await _factory.ListingExistsAsync(listingId));

        // Idempotent: off again is the same answer and still no mapping.
        Assert.Equal(HttpStatusCode.NoContent, await SetTrackingAsync(client, productId, tracked: false));
        Assert.Equal(0, await _factory.MappingCountAsync(productId));
    }

    #endregion

    #region The status a matched mapping shows

    /// <summary>
    /// The criterion: an auto-matched product reads as auto-matched, with the master product's name
    /// and size - which is what the "matched automatically, awaiting check" state is shown with.
    /// </summary>
    [Fact]
    public async Task An_auto_matched_product_reads_with_the_master_products_name_and_size()
    {
        var client = _factory.BusinessA();
        var productId = await _factory.TrackableProductAsync(client, "375mL");
        await SetTrackingAsync(client, productId, tracked: true);
        await _factory.MatchAsync(productId, ProductPriceTrackingStatus.AutoMatched);

        var state = await TrackingAsync(client, productId);

        Assert.Equal(
            (int)ProductPriceTrackingStatus.AutoMatched,
            state.GetProperty("status").GetInt32());
        var master = state.GetProperty("masterProduct");
        Assert.Equal(ApiFactory.MasterProductName, master.GetProperty("name").GetString());
        Assert.Equal(ApiFactory.MasterProductSize, master.GetProperty("sizeValue").GetDecimal());
        Assert.Equal("ml", master.GetProperty("sizeUnit").GetString());

        await _factory.UntrackAsync(productId);
    }

    /// <summary>And a decided match reads the same way, in its own state.</summary>
    [Fact]
    public async Task A_matched_product_reads_with_the_master_products_name_and_size()
    {
        var client = _factory.BusinessA();
        var productId = await _factory.TrackableProductAsync(client, "375mL");
        await SetTrackingAsync(client, productId, tracked: true);
        await _factory.MatchAsync(productId, ProductPriceTrackingStatus.Matched);

        var state = await TrackingAsync(client, productId);

        Assert.Equal((int)ProductPriceTrackingStatus.Matched, state.GetProperty("status").GetInt32());
        Assert.Equal(
            ApiFactory.MasterProductName,
            state.GetProperty("masterProduct").GetProperty("name").GetString());

        await _factory.UntrackAsync(productId);
    }

    /// <summary>
    /// "Looked for and not found" is published as the answer it is, with no master product: a product
    /// nothing in the shared catalogue describes is a recorded outcome rather than a gap.
    /// </summary>
    [Fact]
    public async Task A_not_found_product_reads_as_not_found_with_no_master_product()
    {
        var client = _factory.BusinessA();
        var productId = await _factory.TrackableProductAsync(client, "375mL");
        await SetTrackingAsync(client, productId, tracked: true);
        await _factory.MarkNotFoundAsync(productId);

        var state = await TrackingAsync(client, productId);

        Assert.Equal((int)ProductPriceTrackingStatus.NotFound, state.GetProperty("status").GetInt32());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("masterProduct").ValueKind);

        await _factory.UntrackAsync(productId);
    }

    /// <summary>
    /// The decisive assertion about the platform-table read this issue reviews: the matched
    /// description carries <strong>only</strong> the master product's name and size. The brand is
    /// stored on that row and is deliberately not published, and nothing about a supplier listing,
    /// another business, or the catalogue's own keys appears anywhere in the body.
    /// </summary>
    [Fact]
    public async Task The_matched_description_publishes_only_the_name_and_the_size()
    {
        var client = _factory.BusinessA();
        var productId = await _factory.TrackableProductAsync(client, "375mL");
        await SetTrackingAsync(client, productId, tracked: true);
        await _factory.MatchAsync(productId, ProductPriceTrackingStatus.Matched);

        var response = await client.GetAsync(TrackingUri(productId));
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.Equal(
            ["name", "sizeValue", "sizeUnit"],
            document.RootElement
                .GetProperty("masterProduct")
                .EnumerateObject()
                .Select(property => property.Name));
        Assert.DoesNotContain(ApiFactory.MasterProductBrand, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFactory.ListingSupplierProductId, body, StringComparison.Ordinal);
        Assert.DoesNotContain(E2ETestFixture.BusinessBName, body, StringComparison.Ordinal);

        await _factory.UntrackAsync(productId);
    }

    /// <summary>
    /// The published keys, in the order the contract carries them. A client reads these names, so a
    /// rename or a reordering is a contract change rather than a refactor.
    /// </summary>
    [Fact]
    public async Task The_tracking_state_publishes_exactly_the_agreed_keys()
    {
        var state = await TrackingAsync(_factory.BusinessA(), await _factory.ReorderProductIdAsync());

        Assert.Equal(
            [
                "productId", "tracked", "status", "searchName", "unitLabel", "masterProduct",
                "canTrack", "trackRefusal",
            ],
            state.EnumerateObject().Select(property => property.Name));
    }

    #endregion

    #region What a product edit does

    /// <summary>
    /// The criterion: editing the name of a product whose mapping is still awaiting a match updates
    /// its search text, through the ordinary <c>PUT api/products/{id}</c> an operator uses.
    /// </summary>
    [Fact]
    public async Task Renaming_a_waiting_product_refreshes_its_search_text()
    {
        var client = _factory.BusinessA();
        var productId = await _factory.TrackableProductAsync(client, "375mL");
        await SetTrackingAsync(client, productId, tracked: true);

        await _factory.EditProductAsync(client, productId, "Renamed Tracked Product", "375mL");

        var state = await TrackingAsync(client, productId);
        Assert.Equal("Renamed Tracked Product", state.GetProperty("searchName").GetString());
        Assert.Equal(
            (int)ProductPriceTrackingStatus.Requested,
            state.GetProperty("status").GetInt32());

        await _factory.UntrackAsync(productId);
    }

    /// <summary>And so does a change of its unit label, which is the other half of the search text.</summary>
    [Fact]
    public async Task Relabelling_a_waiting_product_refreshes_its_unit_label()
    {
        var client = _factory.BusinessA();
        var productId = await _factory.TrackableProductAsync(client, "375mL");
        await SetTrackingAsync(client, productId, tracked: true);

        // The name is sent back exactly as it stands, so the only change is the unit label.
        await _factory.EditProductAsync(
            client, productId, await _factory.ProductNameAsync(productId), "600mL");

        var state = await TrackingAsync(client, productId);
        Assert.Equal("600mL", state.GetProperty("unitLabel").GetString());

        await _factory.UntrackAsync(productId);
    }

    /// <summary>
    /// The criterion: changing the unit label of a matched product returns its mapping to awaiting a
    /// match with the new text, because the size the match rests on may no longer agree. It holds for
    /// an automatic match and a decided one alike.
    /// </summary>
    [Theory]
    [InlineData(ProductPriceTrackingStatus.AutoMatched)]
    [InlineData(ProductPriceTrackingStatus.Matched)]
    public async Task Relabelling_a_matched_product_returns_it_to_awaiting_a_match(
        ProductPriceTrackingStatus matched)
    {
        var client = _factory.BusinessA();
        var productId = await _factory.TrackableProductAsync(client, "375mL");
        await SetTrackingAsync(client, productId, tracked: true);
        var (masterProductId, _) = await _factory.MatchAsync(productId, matched);

        // Only the unit label changes: the name is sent back exactly as it stands.
        await _factory.EditProductAsync(
            client, productId, await _factory.ProductNameAsync(productId), "1.25L");

        var state = await TrackingAsync(client, productId);
        Assert.Equal(
            (int)ProductPriceTrackingStatus.Requested,
            state.GetProperty("status").GetInt32());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("masterProduct").ValueKind);
        Assert.Equal("1.25L", state.GetProperty("unitLabel").GetString());
        Assert.True(await _factory.MasterProductExistsAsync(masterProductId));

        await _factory.UntrackAsync(productId);
    }

    /// <summary>
    /// The repair of this issue's review finding, over the whole pipeline: relabelling a matched
    /// product to the generic <c>"unit"</c> through the ordinary <c>PUT api/products/{id}</c> leaves
    /// the edit accepted and the mapping <strong>suspended</strong> - no master product, no match
    /// source, and a published state that says so with the sentence that explains it. The master
    /// product, its supplier listing and the other business's matched mapping of the same master
    /// product are all untouched.
    /// </summary>
    [Theory]
    [InlineData(ProductPriceTrackingStatus.AutoMatched)]
    [InlineData(ProductPriceTrackingStatus.Matched)]
    public async Task Relabelling_a_matched_product_to_the_generic_label_suspends_its_mapping(
        ProductPriceTrackingStatus matched)
    {
        var client = _factory.BusinessA();
        var productId = await _factory.TrackableProductAsync(client, "375mL");
        await SetTrackingAsync(client, productId, tracked: true);
        var (masterProductId, listingId) = await _factory.MatchAsync(productId, matched);

        // Business B's own product, matched to the same master product, so the isolation claim is
        // about a row that would be affected if the refresh reached past the tenant filters.
        var businessB = _factory.BusinessB();
        var theirs = await _factory.TrackableOtherBusinessProductAsync(businessB, "375mL");
        await SetTrackingAsync(businessB, theirs, tracked: true);
        await _factory.MatchAsync(theirs, ProductPriceTrackingStatus.Matched);

        // Only the unit label changes, and only to the generic placeholder.
        await _factory.EditProductAsync(
            client, productId, await _factory.ProductNameAsync(productId), "unit");

        var state = await TrackingAsync(client, productId);
        Assert.True(state.GetProperty("tracked").GetBoolean());
        Assert.Equal(
            (int)ProductPriceTrackingStatus.AwaitingProductDetails,
            state.GetProperty("status").GetInt32());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("masterProduct").ValueKind);
        Assert.False(state.GetProperty("canTrack").GetBoolean());
        Assert.Equal(
            ProductPriceTrackingPolicy.UnitLabelNotSpecificMessage,
            state.GetProperty("trackRefusal").GetString());

        // The stored row, from outside the boundary: the match is gone rather than merely unpublished.
        var stored = await _factory.MappingAsync(productId);
        Assert.Equal(ProductPriceTrackingStatus.AwaitingProductDetails, stored!.Status);
        Assert.Null(stored.MasterProductId);
        Assert.Null(stored.MatchSource);

        // The Tracked column says the same thing.
        var listed = Assert.Single(
            await TrackedListAsync(client),
            entry => entry.GetProperty("productId").GetInt64() == productId);
        Assert.Equal(
            (int)ProductPriceTrackingStatus.AwaitingProductDetails,
            listed.GetProperty("status").GetInt32());

        // Nothing shared was touched, and neither was the other business's match.
        Assert.True(await _factory.MasterProductExistsAsync(masterProductId));
        Assert.True(await _factory.ListingExistsAsync(listingId));
        var untouched = await _factory.MappingAsync(theirs);
        Assert.Equal(ProductPriceTrackingStatus.Matched, untouched!.Status);
        Assert.Equal(masterProductId, untouched.MasterProductId);

        await _factory.UntrackAsync(productId);
        await _factory.UntrackAsync(theirs);
    }

    /// <summary>
    /// And the other half of the repair: correcting the unit label to a real, different size captures
    /// the product's current name and label afresh and returns the mapping to awaiting a match, with
    /// no master product - the one it had was invalidated, not remembered.
    /// </summary>
    [Theory]
    [InlineData(ProductPriceTrackingStatus.AutoMatched)]
    [InlineData(ProductPriceTrackingStatus.Matched)]
    public async Task Correcting_the_unit_label_resumes_a_suspended_mapping(
        ProductPriceTrackingStatus matched)
    {
        var client = _factory.BusinessA();
        var productId = await _factory.TrackableProductAsync(client, "375mL");
        await SetTrackingAsync(client, productId, tracked: true);
        await _factory.MatchAsync(productId, matched);
        var name = await _factory.ProductNameAsync(productId);
        await _factory.EditProductAsync(client, productId, name, "unit");

        await _factory.EditProductAsync(client, productId, name, "1.25L");

        var state = await TrackingAsync(client, productId);
        Assert.Equal(
            (int)ProductPriceTrackingStatus.Requested,
            state.GetProperty("status").GetInt32());
        Assert.Equal("1.25L", state.GetProperty("unitLabel").GetString());
        Assert.Equal(name, state.GetProperty("searchName").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("masterProduct").ValueKind);
        Assert.True(state.GetProperty("canTrack").GetBoolean());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("trackRefusal").ValueKind);

        var stored = await _factory.MappingAsync(productId);
        Assert.Null(stored!.MasterProductId);
        Assert.Null(stored.MatchSource);
        Assert.Equal(PriceCatalogueWaitingKey.For(name, "1.25L"), stored.WaitingKey);

        await _factory.UntrackAsync(productId);
    }

    /// <summary>And the other half of that criterion: a name-only edit keeps the match.</summary>
    [Fact]
    public async Task Renaming_a_matched_product_keeps_the_match()
    {
        var client = _factory.BusinessA();
        var productId = await _factory.TrackableProductAsync(client, "375mL");
        await SetTrackingAsync(client, productId, tracked: true);
        await _factory.MatchAsync(productId, ProductPriceTrackingStatus.Matched);

        await _factory.EditProductAsync(client, productId, "Renamed But Still Matched", "375mL");

        var state = await TrackingAsync(client, productId);
        Assert.Equal((int)ProductPriceTrackingStatus.Matched, state.GetProperty("status").GetInt32());
        Assert.Equal(
            ApiFactory.MasterProductName,
            state.GetProperty("masterProduct").GetProperty("name").GetString());

        await _factory.UntrackAsync(productId);
    }

    #endregion

    #region The tracked list, and the boundary between two businesses

    /// <summary>
    /// The criterion behind the catalogue listing's "Tracked" column: the list holds this business's
    /// tracked products with their statuses, and nothing of the other business's - even though both
    /// are tracking a product at the same moment.
    /// </summary>
    [Fact]
    public async Task The_tracked_list_holds_only_the_callers_own_products()
    {
        var businessA = _factory.BusinessA();
        var businessB = _factory.BusinessB();
        var mine = await _factory.TrackableProductAsync(businessA, "375mL");
        var theirs = await _factory.TrackableOtherBusinessProductAsync(businessB, "375mL");
        await SetTrackingAsync(businessA, mine, tracked: true);
        await SetTrackingAsync(businessB, theirs, tracked: true);

        var mineListed = await TrackedListAsync(businessA);
        var theirsListed = await TrackedListAsync(businessB);

        Assert.Equal([mine], mineListed.Select(entry => entry.GetProperty("productId").GetInt64()));
        Assert.Equal(
            [(int)ProductPriceTrackingStatus.Requested],
            mineListed.Select(entry => entry.GetProperty("status").GetInt32()));
        Assert.Equal([theirs], theirsListed.Select(entry => entry.GetProperty("productId").GetInt64()));

        await _factory.UntrackAsync(mine);
        await _factory.UntrackAsync(theirs);
    }

    /// <summary>
    /// The list publishes a product id and a status and nothing else, so it cannot become a second,
    /// wider read of the shared catalogue.
    /// </summary>
    [Fact]
    public async Task The_tracked_list_publishes_exactly_the_agreed_keys()
    {
        var client = _factory.BusinessA();
        var productId = await _factory.TrackableProductAsync(client, "375mL");
        await SetTrackingAsync(client, productId, tracked: true);

        var entry = Assert.Single(await TrackedListAsync(client));

        Assert.Equal(
            ["productId", "status"],
            entry.EnumerateObject().Select(property => property.Name));

        await _factory.UntrackAsync(productId);
    }

    /// <summary>
    /// Naming the other business's product is "no such product" in both directions - the read and the
    /// write - because the product is resolved through the central tenant query filters. Knowing a
    /// real product id of another business therefore yields nothing, and writes nothing.
    /// </summary>
    [Fact]
    public async Task Another_business_s_product_is_not_found_for_the_read_or_the_write()
    {
        var businessB = _factory.BusinessB();
        var theirs = await _factory.TrackableOtherBusinessProductAsync(businessB, "375mL");
        await SetTrackingAsync(businessB, theirs, tracked: true);

        var businessA = _factory.BusinessA();

        Assert.Equal(HttpStatusCode.NotFound, (await businessA.GetAsync(TrackingUri(theirs))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, await SetTrackingAsync(businessA, theirs, tracked: true));
        Assert.Equal(HttpStatusCode.NotFound, await SetTrackingAsync(businessA, theirs, tracked: false));

        // And their mapping is exactly as it was: nothing of theirs was created, changed or removed.
        Assert.Equal(1, await _factory.MappingCountAsync(theirs));

        await _factory.UntrackAsync(theirs);
    }

    /// <summary>A product that exists in no business at all is the same answer.</summary>
    [Fact]
    public async Task An_unknown_product_is_not_found()
    {
        var client = _factory.BusinessA();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(TrackingUri(999999))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, await SetTrackingAsync(client, 999999, tracked: true));
    }

    #endregion

    private static string TrackingUri(long productId) =>
        $"/api/products/{productId.ToString(CultureInfo.InvariantCulture)}/price-tracking";

    private static async Task<HttpStatusCode> SetTrackingAsync(
        HttpClient client, long productId, bool tracked)
    {
        var response = await client.PutAsJsonAsync(TrackingUri(productId), new { tracked });
        return response.StatusCode;
    }

    private static async Task<JsonElement> TrackingAsync(HttpClient client, long productId)
    {
        var response = await client.GetAsync(TrackingUri(productId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<IReadOnlyList<JsonElement>> TrackedListAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/products/price-tracking");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.EnumerateArray().Select(element => element.Clone()).ToList();
    }

    /// <summary>
    /// The API hosted the way the end-to-end harness hosts it - the dedicated E2E environment, its two
    /// synthetic businesses with their own catalogues, and a disposable relational database - with the
    /// shared catalogue's platform rows arranged directly, because writing them is #928's and #941's
    /// work and no endpoint of this issue can.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        /// <summary>Business A's own product these tests give a real unit label to and track.</summary>
        public const string TrackableProductName = "E2E Tracked Cola";

        /// <summary>Business B's, for the two-business tests.</summary>
        public const string OtherBusinessTrackableProductName = "E2E Other Tracked Cola";

        /// <summary>The master product a match points at. Only its name and size may be published.</summary>
        public const string MasterProductName = "Classic";

        /// <summary>
        /// Its brand, which is stored beside the name and is deliberately <em>not</em> published: the
        /// absence of this string from a response is what the bounded-read test asserts.
        /// </summary>
        public const string MasterProductBrand = "E2E Master Brand";

        public const decimal MasterProductSize = 375m;

        /// <summary>
        /// A supplier listing under that master product, for the same reason: a business endpoint says
        /// nothing about a supplier's shelf, which is #925's and #930's subject.
        /// </summary>
        public const string ListingSupplierProductId = "E2E-LISTING-SKU";

        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        /// <summary>
        /// How many products these tests have created, which is what makes each one's name its own.
        /// </summary>
        private int _created;

        public ApiFactory()
        {
            // Hosting must not depend on the process's current directory; see E2ETestHostContentRoot.
            E2ETestHostContentRoot.Pin();
            _connection.Open();
        }

        public HttpClient BusinessA() => As(E2ETestActors.BusinessAOwner.Key);

        public HttpClient BusinessB() => As(E2ETestActors.BusinessBOwner.Key);

        public Task<long> ReorderProductIdAsync() => ProductIdAsync(E2ETestFixture.ReorderProductName);

        /// <summary>
        /// A fresh product of business A with a real unit label, created through
        /// <c>POST api/products</c> - which is how an operator gives a product a unit label the
        /// catalogue import never would, and keeps these tests off a hand-written row.
        ///
        /// <para>A new product each time, with a name of its own, so no test depends on what another
        /// renamed or relabelled: several of them edit the product's name, which is the behaviour
        /// under test.</para>
        /// </summary>
        public Task<long> TrackableProductAsync(HttpClient client, string unitLabel) =>
            NewProductAsync(client, TrackableProductName, unitLabel);

        /// <summary>The same for business B, so each business names only its own.</summary>
        public Task<long> TrackableOtherBusinessProductAsync(HttpClient client, string unitLabel) =>
            NewProductAsync(client, OtherBusinessTrackableProductName, unitLabel);

        /// <summary>That product's stored name, which is what its captured search text must equal.</summary>
        public async Task<string> ProductNameAsync(long productId) =>
            (await ProductAsync(productId)).Name;

        /// <summary>Edits a product's name and unit label through <c>PUT api/products/{id}</c>.</summary>
        public async Task EditProductAsync(
            HttpClient client, long productId, string name, string unitLabel)
        {
            var product = await ProductAsync(productId);

            var response = await client.PutAsJsonAsync(
                $"/api/products/{productId.ToString(CultureInfo.InvariantCulture)}",
                new
                {
                    name,
                    sku = product.Sku,
                    description = product.Description,
                    lowStockThreshold = product.LowStockThreshold,
                    restockTo = product.RestockTo,
                    unit = unitLabel,
                    categoryId = product.CategoryId,
                    isActive = product.IsActive,
                });

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        /// <summary>
        /// Records a decided or automatic match on a product's mapping, with a master product and a
        /// supplier listing under it, seeding both the first time. No endpoint can do this: writing the
        /// shared catalogue is the matcher's (#941) and the Super Admin review's (#928) work, so the
        /// state is arranged directly and only this issue's reads and writes are under test.
        /// </summary>
        public async Task<(long MasterProductId, long ListingId)> MatchAsync(
            long productId,
            ProductPriceTrackingStatus status)
        {
            var source = status == ProductPriceTrackingStatus.AutoMatched
                ? ProductPriceTrackingMatchSource.CollectorAutomaticMatch
                : ProductPriceTrackingMatchSource.SuperAdmin;
            ProductPriceTrackingPolicy.RequireConsistentMatch(status, masterProductId: 1, source);

            await using var db = Database();

            var master = await db.MasterProducts
                .SingleOrDefaultAsync(candidate => candidate.Name == MasterProductName);
            if (master is null)
            {
                master = new MasterProduct
                {
                    Brand = MasterProductBrand,
                    Name = MasterProductName,
                    SizeValue = MasterProductSize,
                    SizeUnit = MasterProductSizeUnit.Millilitre,
                    NormalisedKey = MasterProductPolicy.NormalisedKey(
                        MasterProductBrand,
                        MasterProductName,
                        MasterProductSize,
                        MasterProductSizeUnit.Millilitre),
                    CreatedAtUtc = E2ETestFixture.SeededAtUtc,
                    UpdatedAtUtc = E2ETestFixture.SeededAtUtc,
                };
                db.MasterProducts.Add(master);
                await db.SaveChangesAsync();
            }

            var listing = await db.SupplierListings
                .SingleOrDefaultAsync(candidate => candidate.SupplierProductId == ListingSupplierProductId);
            if (listing is null)
            {
                var preset = await db.SupplierPresets.AsNoTracking()
                    .OrderBy(candidate => candidate.Id)
                    .FirstAsync();

                listing = new SupplierListing
                {
                    SupplierPresetId = preset.Id,
                    MasterProductId = master.Id,
                    SupplierProductId = ListingSupplierProductId,
                    MatchStatus = SupplierListingMatchStatus.Proposed,
                    CreatedAtUtc = E2ETestFixture.SeededAtUtc,
                    UpdatedAtUtc = E2ETestFixture.SeededAtUtc,
                };
                db.SupplierListings.Add(listing);
                await db.SaveChangesAsync();
            }

            var mapping = await db.ProductPriceTrackings
                .SingleAsync(tracking => tracking.ProductId == productId);
            mapping.MasterProductId = master.Id;
            mapping.Status = status;
            mapping.MatchSource = source;
            await db.SaveChangesAsync();

            return (master.Id, listing.Id);
        }

        /// <summary>Records "looked for, and not found", which is an answer rather than a gap.</summary>
        public async Task MarkNotFoundAsync(long productId)
        {
            await using var db = Database();

            var mapping = await db.ProductPriceTrackings
                .SingleAsync(tracking => tracking.ProductId == productId);
            mapping.Status = ProductPriceTrackingStatus.NotFound;
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Removes a product's mapping from outside the boundary, so each test leaves the shared
        /// fixture as it found it. It is arrangement teardown rather than the feature: the endpoint's
        /// own turn-off is asserted on its own above.
        /// </summary>
        public async Task UntrackAsync(long productId)
        {
            await using var db = Database();

            var mapping = await db.ProductPriceTrackings
                .SingleOrDefaultAsync(tracking => tracking.ProductId == productId);
            if (mapping is null)
            {
                return;
            }

            db.ProductPriceTrackings.Remove(mapping);
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// That product's mapping exactly as it is stored, read from outside the boundary, or
        /// <see langword="null"/> when it has none. It is what proves a cleared match is cleared in
        /// the row rather than merely absent from the response.
        /// </summary>
        public async Task<StoredMapping?> MappingAsync(long productId)
        {
            await using var db = Database();

            return await db.ProductPriceTrackings.AsNoTracking()
                .Where(tracking => tracking.ProductId == productId)
                .Select(tracking => new StoredMapping(
                    tracking.Status,
                    tracking.MasterProductId,
                    tracking.MatchSource,
                    tracking.SearchName,
                    tracking.UnitLabel,
                    tracking.WaitingKey))
                .SingleOrDefaultAsync();
        }

        /// <summary>How many mappings that product has, read from outside the boundary.</summary>
        public async Task<int> MappingCountAsync(long productId)
        {
            await using var db = Database();

            return await db.ProductPriceTrackings
                .CountAsync(tracking => tracking.ProductId == productId);
        }

        public async Task<bool> MasterProductExistsAsync(long masterProductId)
        {
            await using var db = Database();

            return await db.MasterProducts.AnyAsync(master => master.Id == masterProductId);
        }

        public async Task<bool> ListingExistsAsync(long listingId)
        {
            await using var db = Database();

            return await db.SupplierListings.AnyAsync(listing => listing.Id == listingId);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(E2ETestEnvironment.EnvironmentName);

            // The E2E environment is not one of the environments that migrate automatically, so this
            // disposable database opts in with the setting that exists for exactly that case.
            builder.UseSetting("Database:AllowAutomaticMigrationUnsafeOutsideDevelopment", "true");

            builder.ConfigureServices(services =>
            {
                var dbContextOptions = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (dbContextOptions is not null)
                {
                    services.Remove(dbContextOptions);
                }

                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _connection.Dispose();
            }
        }

        private async Task<long> NewProductAsync(HttpClient client, string prefix, string unitLabel)
        {
            var name = string.Create(
                CultureInfo.InvariantCulture,
                $"{prefix} {Interlocked.Increment(ref _created)}");

            var response = await client.PostAsJsonAsync(
                "/api/products",
                new
                {
                    name,
                    sku = (string?)null,
                    description = (string?)null,
                    quantityInStock = 0,
                    lowStockThreshold = 0,
                    restockTo = 0,
                    unit = unitLabel,
                    categoryId = (long?)null,
                    isActive = true,
                    initialUnitCost = (decimal?)null,
                });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return created.RootElement.GetProperty("id").GetInt64();
        }

        private HttpClient As(string actorKey)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation(E2ETestActors.ActorHeaderName, actorKey);
            return client;
        }

        private async Task<Product> ProductAsync(long productId)
        {
            await using var db = Database();

            return await db.Products.AsNoTracking()
                .SingleAsync(product => product.Id == productId);
        }

        private async Task<long> ProductIdAsync(string productName)
        {
            await using var db = Database();

            var product = await db.Products.AsNoTracking()
                .SingleAsync(candidate => candidate.Name == productName);

            return product.Id;
        }

        /// <summary>
        /// One stored mapping's own columns, for the assertions that are about the row rather than
        /// about the response: the status, the match it names and how that was decided, and the text
        /// it is searched for by.
        /// </summary>
        public sealed record StoredMapping(
            ProductPriceTrackingStatus Status,
            long? MasterProductId,
            ProductPriceTrackingMatchSource? MatchSource,
            string SearchName,
            string UnitLabel,
            string WaitingKey);

        /// <summary>
        /// An unrestricted context for arranging and verifying both businesses' rows from outside the
        /// boundary, never over HTTP. Resolving <c>Services</c> starts the host, which is what creates
        /// the schema and seeds the fixture this then reads.
        /// </summary>
        private AppDbContext Database()
        {
            _ = Services;
            return TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        }
    }
}
