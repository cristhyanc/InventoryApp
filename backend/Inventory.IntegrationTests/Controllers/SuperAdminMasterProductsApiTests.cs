using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Suppliers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The Super Admin master products API over real HTTP (issues #1023 and #1024, parent #817): who may
/// reach it, what it reports, how it pages and searches, what a create and an edit store and refuse -
/// and the decisive assertion that nothing about any business appears in any of it.
///
/// <para>Everything here goes through the complete pipeline - authentication, <c>[Authorize]</c>, the
/// platform-admin policy, <c>[RequiredScope]</c>, <c>RolePreviewMiddleware</c>,
/// <c>BusinessScopeMiddleware</c>'s independent re-check of the same policy, the tenant query filters
/// and <c>BusinessOwnershipEnforcer</c> - so the authorization under test is the real one. Only the
/// authentication is synthetic.</para>
/// </summary>
public class SuperAdminMasterProductsApiTests
    : IClassFixture<SuperAdminBusinessesApiFactory>, IAsyncLifetime
{
    private const string MasterProductsUrl = "/api/super-admin/master-products";

    private const string SecondBusinessName = "Coastal Vending Two";

    private static readonly DateTime Observed = new(2026, 10, 30, 6, 0, 0, DateTimeKind.Utc);

    private readonly SuperAdminBusinessesApiFactory _factory;

    public SuperAdminMasterProductsApiTests(SuperAdminBusinessesApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        // The catalogue first: a supplier listing's own supplier master record is restricted on
        // delete, so the presets cannot be reset while a listing names one.
        await _factory.ResetPriceCatalogueAsync();
        await _factory.ResetAsync();
        await _factory.ResetSupplierPresetsAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    #region Who may reach it

    /// <summary>
    /// The issue's own criterion, asked of every endpoint: members, Owners included, are refused. An
    /// Owner holds the strongest business role there is and reaches none of this, because the master
    /// catalogue is shared by every business and a business role is not platform authority.
    /// </summary>
    [Theory]
    [InlineData(SuperAdminBusinessesApiFactory.TradingOwnerObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.TradingManagerObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.ApplicantObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.StrangerObjectId)]
    public async Task Every_endpoint_refuses_a_caller_who_is_not_the_configured_administrator(
        string objectId)
    {
        var client = _factory.Authenticated(objectId);

        foreach (var response in await EveryEndpointAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    /// <summary>
    /// An anonymous request is refused by authentication with <c>401</c>, not turned into a
    /// <c>403</c>: "you are not signed in" and "you are signed in and not the administrator" stay
    /// different answers.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_request_is_unauthorized()
    {
        var client = _factory.CreateClient();

        foreach (var response in await EveryEndpointAsync(client))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    /// <summary>
    /// The delegated scope is required of the administrator too: platform authority is an additional
    /// requirement on a delegated API call, never a replacement for one.
    /// </summary>
    [Fact]
    public async Task The_administrator_without_the_delegated_scope_is_refused()
    {
        var client = _factory.WithoutScope(SuperAdminBusinessesApiFactory.AdministratorObjectId);

        foreach (var response in await EveryEndpointAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    /// <summary>
    /// A member who presents a support-session id reaches nothing here either, and the refusal is the
    /// platform-admin one rather than anything about the session: this is a
    /// <c>PlatformDiagnosticsEndpointAttribute</c> endpoint, so the <c>X-Support-Session</c> header is
    /// never acted on at all. A member who sets the header is still an ordinary member.
    /// </summary>
    [Fact]
    public async Task A_member_presenting_a_support_session_id_is_still_refused()
    {
        const string SessionId = "a-seeded-support-session-id-for-the-catalogue";
        await _factory.SeedSupportSessionAsync(
            _factory.TradingBusinessId,
            SuperAdminBusinessesApiFactory.AdministratorObjectId,
            SessionId);

        var client = _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);
        client.DefaultRequestHeaders.Add("X-Support-Session", SessionId);

        foreach (var response in await EveryEndpointAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    /// <summary>
    /// A role preview is read-only everywhere (issue #720): the administrator previewing a business
    /// role still <em>reads</em> the catalogue, because a preview refuses writes and a read is not
    /// one. What it must not do is turn the platform-admin answer into a member refusal.
    /// </summary>
    [Theory]
    [InlineData("Owner")]
    [InlineData("Manager")]
    [InlineData("Operator")]
    public async Task A_role_preview_does_not_change_either_read(string role)
    {
        var client = _factory.AsAdministrator();
        client.DefaultRequestHeaders.Add("X-Role-Preview", role);

        foreach (var response in await BothEndpointsAsync(client))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    /// <summary>
    /// And the other half of that rule, which issue #1024 is the first thing on this controller to
    /// have: a role preview's <strong>writes</strong> are refused with the preview's own stable code,
    /// before the endpoint runs, and the catalogue is unchanged.
    /// </summary>
    [Theory]
    [InlineData("Owner")]
    [InlineData("Manager")]
    [InlineData("Operator")]
    public async Task A_role_preview_write_is_refused_as_read_only_and_stores_nothing(string role)
    {
        var arranged = await ArrangeAsync();

        var client = _factory.AsAdministrator();
        client.DefaultRequestHeaders.Add("X-Role-Preview", role);

        var before = await FingerprintAsync();

        foreach (var response in await WritesAsync(client, arranged))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("read_only_role_preview", await CodeAsync(response));
        }

        Assert.Equal(before, await FingerprintAsync());
    }

    /// <summary>
    /// A member previewing a role reaches it no more than that member does: the preview header is not
    /// a grant of anything, least of all of platform authority.
    /// </summary>
    [Fact]
    public async Task A_member_previewing_a_role_is_still_refused()
    {
        var client = _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);
        client.DefaultRequestHeaders.Add("X-Role-Preview", "Owner");

        foreach (var response in await EveryEndpointAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    /// <summary>
    /// A refused caller writes nothing, asserted against the database rather than against the status
    /// code alone: the catalogue's own rows and the mappings beside them are exactly as they were.
    /// </summary>
    [Fact]
    public async Task A_refused_caller_stores_nothing()
    {
        var arranged = await ArrangeAsync();
        var before = await FingerprintAsync();

        var owner = _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);
        foreach (var response in await WritesAsync(owner, arranged))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        foreach (var response in await WritesAsync(_factory.CreateClient(), arranged))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        Assert.Equal(before, await FingerprintAsync());
    }

    /// <summary>
    /// Reaching this surface grants nothing else: the administrator is a member of no business, so
    /// every business endpoint still refuses them.
    /// </summary>
    [Fact]
    public async Task The_administrator_still_reaches_no_business_endpoint()
    {
        var client = _factory.AsAdministrator();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/suppliers")).StatusCode);
    }

    /// <summary>
    /// The two writes this surface has are the create and the edit, and no other: <strong>deleting a
    /// master product is explicitly out of this issue's scope</strong>, so no action matches
    /// <c>DELETE</c> and nothing is removed.
    ///
    /// <para>The refusal is <c>403</c> rather than <c>405</c>, and that is the pipeline being exactly
    /// as strict as it should be: no action matches the method, so the request reaches no endpoint
    /// carrying <c>PlatformDiagnosticsEndpointAttribute</c>, and <c>BusinessScopeMiddleware</c>
    /// therefore applies the ordinary membership requirement - which the administrator, a member of no
    /// business, does not meet. Either answer would mean "there is nothing here to delete"; this one
    /// also means no delete could be reached without a new, classified endpoint.</para>
    /// </summary>
    [Fact]
    public async Task There_is_no_delete_on_this_surface()
    {
        var arranged = await ArrangeAsync();
        var client = _factory.AsAdministrator();
        var before = await FingerprintAsync();

        var response = await client.DeleteAsync($"{MasterProductsUrl}/{arranged.ColaId}");

        Assert.True(
            response.StatusCode is HttpStatusCode.Forbidden
                or HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.NotFound,
            $"A delete reached something: {response.StatusCode}.");

        Assert.Equal(before, await FingerprintAsync());
    }

    #endregion

    #region Creating one

    /// <summary>
    /// The first criterion over the wire: a create stores the description under the same
    /// normalisation and key the review's own create uses, answers <c>201</c> with the product it
    /// created and the location to read it at, and the catalogue then holds it.
    /// </summary>
    [Fact]
    public async Task A_create_stores_the_product_and_returns_it()
    {
        await ArrangeAsync();
        _factory.ClearLog();

        var response = await _factory.AsAdministrator().PostAsJsonAsync(
            MasterProductsUrl,
            new { brand = "  Solo  ", name = " Lemon ", sizeValue = 600m, sizeUnit = (int)MasterProductSizeUnit.Millilitre });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var id = created.GetProperty("id").GetInt64();

        Assert.Equal("Solo", created.GetProperty("brand").GetString());
        Assert.Equal("Lemon", created.GetProperty("name").GetString());
        Assert.Equal(600m, created.GetProperty("sizeValue").GetDecimal());
        Assert.Equal((int)MasterProductSizeUnit.Millilitre, created.GetProperty("sizeUnit").GetInt32());
        Assert.Equal(0, created.GetProperty("listingCounts").GetProperty("total").GetInt32());
        Assert.Equal(
            $"{MasterProductsUrl}/{id}",
            response.Headers.Location?.AbsolutePath);

        await using var db = _factory.Database();
        var stored = await db.MasterProducts.AsNoTracking().SingleAsync(master => master.Id == id);
        Assert.Equal(
            MasterProductPolicy.NormalisedKey("Solo", "Lemon", 600m, MasterProductSizeUnit.Millilitre),
            stored.NormalisedKey);
        Assert.Equal(stored.CreatedAtUtc, stored.UpdatedAtUtc);

        // Audited with the actor, on the shared catalogue's own trail, naming no business.
        var audit = Assert.Single(
            _factory.LoggedEntries(),
            entry => entry.Contains("Price catalogue review audit", StringComparison.Ordinal));
        Assert.Contains("MasterProductCreated", audit, StringComparison.Ordinal);
        Assert.Contains(
            SuperAdminBusinessesApiFactory.AdministratorObjectId,
            audit,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            SuperAdminBusinessesApiFactory.TradingBusinessName,
            audit,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The 400 half of the first criterion, with the policy's own sentence: every value is checked
    /// rather than trusted, including the unit - an enum is a compile-time constraint and not a
    /// validation, because a deserialized JSON number binds an arbitrary integer.
    /// </summary>
    [Theory]
    [InlineData("", "Lemon", 600, 1)]
    [InlineData("   ", "Lemon", 600, 1)]
    [InlineData("Solo", "", 600, 1)]
    [InlineData("Solo", "Lemon", 0, 1)]
    [InlineData("Solo", "Lemon", -600, 1)]
    [InlineData("Solo", "Lemon", 600.0001, 1)]
    [InlineData("Solo", "Lemon", 600, 0)]
    [InlineData("Solo", "Lemon", 600, 99)]
    public async Task An_invalid_create_is_a_bad_request_and_stores_nothing(
        string brand,
        string name,
        decimal sizeValue,
        int sizeUnit)
    {
        await ArrangeAsync();
        var before = await FingerprintAsync();

        var response = await _factory.AsAdministrator().PostAsJsonAsync(
            MasterProductsUrl,
            new { brand, name, sizeValue, sizeUnit });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await MessageAsync(response)));
        Assert.Equal(before, await FingerprintAsync());
    }

    /// <summary>
    /// The 409 half: one description is one master product, so a second create of it carries the
    /// review's own duplicate sentence - the same rule, so the same answer - and leaves one row.
    /// </summary>
    [Fact]
    public async Task A_duplicate_create_is_a_conflict_and_stores_nothing()
    {
        var arranged = await ArrangeAsync();
        var before = await FingerprintAsync();

        var response = await _factory.AsAdministrator().PostAsJsonAsync(
            MasterProductsUrl,
            // The same description the arrangement's "Coca-Cola Classic 375 ml" carries, spelled
            // differently on purpose: the key is what decides a duplicate, not the spelling.
            new { brand = "coca-cola", name = "  CLASSIC ", sizeValue = 375m, sizeUnit = (int)MasterProductSizeUnit.Millilitre });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            ProductMatchReviewPolicy.DuplicateMasterProductMessage,
            await MessageAsync(response));
        Assert.Equal(before, await FingerprintAsync());

        // And the row that was already there is untouched.
        await using var db = _factory.Database();
        Assert.Equal(
            "Classic",
            (await db.MasterProducts.AsNoTracking().SingleAsync(master => master.Id == arranged.ColaId)).Name);
    }

    #endregion

    #region Editing one

    /// <summary>
    /// The second and third criteria over the wire: an edit corrects the brand and the name, keeps
    /// the row's <strong>id</strong> - so everything pointing at it still does - recomputes the key
    /// and the instant, and changes nothing else at all.
    /// </summary>
    [Fact]
    public async Task An_edit_corrects_the_product_keeps_its_id_and_changes_nothing_else()
    {
        var arranged = await ArrangeAsync();
        var client = _factory.AsAdministrator();
        _factory.ClearLog();

        var read = await ProductAsync(arranged.ColaId);

        var response = await client.PutAsJsonAsync(
            $"{MasterProductsUrl}/{arranged.ColaId}",
            new
            {
                brand = "Coca-Cola",
                name = "Classic No Sugar",
                sizeValue = 375m,
                sizeUnit = (int)MasterProductSizeUnit.Millilitre,
                lastChangedAtUtc = read.UpdatedAtUtc,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(arranged.ColaId, updated.GetProperty("id").GetInt64());
        Assert.Equal("Classic No Sugar", updated.GetProperty("name").GetString());
        Assert.Equal(2, updated.GetProperty("listingCounts").GetProperty("total").GetInt32());

        await using var db = _factory.Database();
        var stored = await db.MasterProducts.AsNoTracking()
            .SingleAsync(master => master.Id == arranged.ColaId);
        Assert.Equal("Classic No Sugar", stored.Name);
        Assert.Equal(
            MasterProductPolicy.NormalisedKey(
                "Coca-Cola",
                "Classic No Sugar",
                375m,
                MasterProductSizeUnit.Millilitre),
            stored.NormalisedKey);
        Assert.True(stored.UpdatedAtUtc > read.UpdatedAtUtc);
        Assert.Equal(read.CreatedAtUtc, stored.CreatedAtUtc);

        // Both of this product's supplier listings still hang from it, with their own statuses, and
        // every mapping of both businesses is untouched.
        Assert.Equal(
            2,
            await db.SupplierListings.AsNoTracking()
                .CountAsync(listing => listing.MasterProductId == arranged.ColaId));

        foreach (var mapping in await db.ProductPriceTrackings.AsNoTracking()
            .IgnoreQueryFilters()
            .ToListAsync())
        {
            Assert.Equal(ProductPriceTrackingStatus.Requested, mapping.Status);
            Assert.Null(mapping.MasterProductId);
            Assert.Equal(Observed, mapping.UpdatedAtUtc);
        }

        var audit = Assert.Single(
            _factory.LoggedEntries(),
            entry => entry.Contains("Price catalogue review audit", StringComparison.Ordinal));
        Assert.Contains("MasterProductEdited", audit, StringComparison.Ordinal);
        Assert.Contains(arranged.ColaId.ToString(System.Globalization.CultureInfo.InvariantCulture), audit, StringComparison.Ordinal);
    }

    /// <summary>An id no master product has is <c>404</c> and nothing is written.</summary>
    [Fact]
    public async Task An_edit_of_an_unknown_product_is_not_found()
    {
        await ArrangeAsync();
        var before = await FingerprintAsync();

        var response = await _factory.AsAdministrator().PutAsJsonAsync(
            $"{MasterProductsUrl}/999999",
            Edit("Solo", "Lemon", 600m, Observed));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await FingerprintAsync());
    }

    /// <summary>
    /// An edit onto another master product's description is the same <c>409</c> a duplicate create
    /// is, and an edit that saves its <em>own</em> unchanged key is allowed - which is the issue's own
    /// distinction.
    /// </summary>
    [Fact]
    public async Task An_edit_onto_another_products_key_is_a_conflict_and_its_own_key_is_allowed()
    {
        var arranged = await ArrangeAsync();
        var client = _factory.AsAdministrator();

        var zero = await ProductAsync(arranged.ColaZeroId);

        var conflict = await client.PutAsJsonAsync(
            $"{MasterProductsUrl}/{arranged.ColaZeroId}",
            Edit("Coca-Cola", "Classic", 375m, zero.UpdatedAtUtc));

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(
            ProductMatchReviewPolicy.DuplicateMasterProductMessage,
            await MessageAsync(conflict));
        Assert.Equal("Zero Sugar", (await ProductAsync(arranged.ColaZeroId)).Name);

        // Its own key, re-submitted: a row is not a duplicate of itself.
        var allowed = await client.PutAsJsonAsync(
            $"{MasterProductsUrl}/{arranged.ColaZeroId}",
            Edit("Coca-Cola", "Zero Sugar", 375m, zero.UpdatedAtUtc));

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    /// <summary>
    /// The stale-edit refusal over the wire: the second of two tabs is refused rather than applied,
    /// and the first tab's correction stands.
    /// </summary>
    [Fact]
    public async Task An_edit_presenting_a_superseded_instant_is_a_conflict()
    {
        var arranged = await ArrangeAsync();
        var client = _factory.AsAdministrator();

        var read = await ProductAsync(arranged.PepsiId);

        var first = await client.PutAsJsonAsync(
            $"{MasterProductsUrl}/{arranged.PepsiId}",
            Edit("Pepsi", "Max Zero", 375m, read.UpdatedAtUtc));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PutAsJsonAsync(
            $"{MasterProductsUrl}/{arranged.PepsiId}",
            Edit("Pepsi", "Something Else", 375m, read.UpdatedAtUtc));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(MasterProductEditPolicy.StaleEditMessage, await MessageAsync(second));
        Assert.Equal("Max Zero", (await ProductAsync(arranged.PepsiId)).Name);
    }

    /// <summary>
    /// An edit that carries no <c>lastChangedAtUtc</c> at all is a <c>400</c> rather than the
    /// <c>409</c> a stale one gets: the request is incomplete, and the page must send the field
    /// rather than re-read.
    /// </summary>
    [Fact]
    public async Task An_edit_with_no_expected_instant_is_a_bad_request()
    {
        var arranged = await ArrangeAsync();
        var before = await FingerprintAsync();

        var response = await _factory.AsAdministrator().PutAsJsonAsync(
            $"{MasterProductsUrl}/{arranged.ColaId}",
            new { brand = "Coca-Cola", name = "Classic", sizeValue = 375m, sizeUnit = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            MasterProductEditPolicy.ExpectedUpdatedAtRequiredMessage,
            await MessageAsync(response));
        Assert.Equal(before, await FingerprintAsync());
    }

    /// <summary>
    /// <strong>The safeguard of this issue over the wire.</strong> The arrangement's cola has a
    /// confirmed supplier listing with a pack of 30, so a change to its size value or unit is
    /// <c>409</c> with the policy's own sentence and nothing is written - while a brand or name edit
    /// of the same product goes through.
    /// </summary>
    [Theory]
    [InlineData(600, 1)]
    [InlineData(375, 3)]
    public async Task A_size_change_is_refused_while_a_listing_is_confirmed(decimal sizeValue, int sizeUnit)
    {
        var arranged = await ArrangeAsync();
        var read = await ProductAsync(arranged.ColaId);
        var before = await FingerprintAsync();

        var refused = await _factory.AsAdministrator().PutAsJsonAsync(
            $"{MasterProductsUrl}/{arranged.ColaId}",
            new
            {
                brand = "Coca-Cola",
                name = "Classic",
                sizeValue,
                sizeUnit,
                lastChangedAtUtc = read.UpdatedAtUtc,
            });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(
            MasterProductEditPolicy.SizeChangeRefusedMessage,
            await MessageAsync(refused));
        Assert.Equal(before, await FingerprintAsync());

        // And the brand and the name are still editable on that very product.
        var allowed = await _factory.AsAdministrator().PutAsJsonAsync(
            $"{MasterProductsUrl}/{arranged.ColaId}",
            Edit("Coca-Cola Amatil", "Classic", 375m, read.UpdatedAtUtc));

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("Coca-Cola Amatil", (await ProductAsync(arranged.ColaId)).Brand);
    }

    /// <summary>
    /// The same product with nothing confirmed: the arrangement's "Coca-Cola Zero Sugar" has no
    /// supplier listing at all, so its size is editable and the key is recomputed.
    /// </summary>
    [Fact]
    public async Task A_size_change_is_allowed_with_no_confirmed_listing()
    {
        var arranged = await ArrangeAsync();
        var read = await ProductAsync(arranged.ColaZeroId);

        var response = await _factory.AsAdministrator().PutAsJsonAsync(
            $"{MasterProductsUrl}/{arranged.ColaZeroId}",
            Edit("Coca-Cola", "Zero Sugar", 600m, read.UpdatedAtUtc));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await ProductAsync(arranged.ColaZeroId);
        Assert.Equal(600m, stored.SizeValue);
        Assert.Equal(
            MasterProductPolicy.NormalisedKey(
                "Coca-Cola",
                "Zero Sugar",
                600m,
                MasterProductSizeUnit.Millilitre),
            stored.NormalisedKey);
    }

    #endregion

    #region The listing

    /// <summary>
    /// The first two criteria over the wire: the catalogue comes back ordered by brand, name and
    /// size, each product carrying its description, both instants and the counts of its supplier
    /// listings by match status, with the total number the page is a slice of.
    /// </summary>
    [Fact]
    public async Task The_listing_reports_the_catalogue_ordered_with_its_listing_counts()
    {
        var arranged = await ArrangeAsync();

        var response = await _factory.AsAdministrator().GetAsync(MasterProductsUrl);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(3, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(50, body.GetProperty("pageSize").GetInt32());
        Assert.False(body.GetProperty("hasMore").GetBoolean());

        var products = body.GetProperty("products").EnumerateArray().ToArray();
        Assert.Equal(
            [arranged.ColaId, arranged.ColaZeroId, arranged.PepsiId],
            products.Select(product => product.GetProperty("id").GetInt64()).ToArray());

        var cola = products[0];
        Assert.Equal("Coca-Cola", cola.GetProperty("brand").GetString());
        Assert.Equal("Classic", cola.GetProperty("name").GetString());
        Assert.Equal(375m, cola.GetProperty("sizeValue").GetDecimal());
        Assert.Equal(
            (int)MasterProductSizeUnit.Millilitre,
            cola.GetProperty("sizeUnit").GetInt32());
        Assert.Equal(Observed, cola.GetProperty("createdAtUtc").GetDateTime());
        Assert.Equal(Observed, cola.GetProperty("updatedAtUtc").GetDateTime());

        var counts = cola.GetProperty("listingCounts");
        Assert.Equal(1, counts.GetProperty("proposed").GetInt32());
        Assert.Equal(1, counts.GetProperty("confirmed").GetInt32());
        Assert.Equal(0, counts.GetProperty("needsReview").GetInt32());
        Assert.Equal(0, counts.GetProperty("rejected").GetInt32());
        Assert.Equal(2, counts.GetProperty("total").GetInt32());

        // A product nothing has listed yet is in the catalogue with four zeros, not left out.
        Assert.Equal(
            0,
            products[2].GetProperty("listingCounts").GetProperty("total").GetInt32());
    }

    /// <summary>
    /// The search over the wire, case-insensitively over the brand and the name, with the total
    /// reporting how many it matched rather than how many came back.
    /// </summary>
    [Theory]
    [InlineData("coca", 2)]
    [InlineData("ZERO", 1)]
    [InlineData("pepsi", 1)]
    [InlineData("   ", 3)]
    [InlineData("nothing at all", 0)]
    public async Task The_listing_is_searchable_over_the_brand_and_the_name(string search, int matches)
    {
        await ArrangeAsync();

        var response = await _factory.AsAdministrator()
            .GetAsync($"{MasterProductsUrl}?search={Uri.EscapeDataString(search)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(matches, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(matches, body.GetProperty("products").GetArrayLength());
    }

    /// <summary>
    /// The paging over the wire: a page of one is one product with two still to come, and the second
    /// page is the next one along.
    /// </summary>
    [Fact]
    public async Task The_listing_is_paged_server_side()
    {
        var arranged = await ArrangeAsync();
        var client = _factory.AsAdministrator();

        var first = JsonDocument
            .Parse(await (await client.GetAsync($"{MasterProductsUrl}?page=1&pageSize=1"))
                .Content.ReadAsStringAsync())
            .RootElement;
        var second = JsonDocument
            .Parse(await (await client.GetAsync($"{MasterProductsUrl}?page=2&pageSize=1"))
                .Content.ReadAsStringAsync())
            .RootElement;

        Assert.Equal(
            arranged.ColaId,
            Assert.Single(first.GetProperty("products").EnumerateArray())
                .GetProperty("id").GetInt64());
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        Assert.Equal(3, first.GetProperty("totalCount").GetInt32());

        Assert.Equal(
            arranged.ColaZeroId,
            Assert.Single(second.GetProperty("products").EnumerateArray())
                .GetProperty("id").GetInt64());
        Assert.Equal(2, second.GetProperty("page").GetInt32());
    }

    /// <summary>
    /// The bound the issue states, over the wire: a page size above the maximum is served at the
    /// maximum and the response says so, and a page size that is not a positive number takes the
    /// default - a bounded answer rather than a refusal the page could do nothing with.
    /// </summary>
    [Theory]
    [InlineData("pageSize=500", 200)]
    [InlineData("pageSize=0", 50)]
    [InlineData("pageSize=-5", 50)]
    [InlineData("pageSize=25", 25)]
    public async Task A_requested_page_size_is_bounded_and_reported(string query, int served)
    {
        await ArrangeAsync();

        var response = await _factory.AsAdministrator().GetAsync($"{MasterProductsUrl}?{query}");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(served, body.GetProperty("pageSize").GetInt32());
    }

    /// <summary>A page beyond the end is an empty page of a known total, not an error.</summary>
    [Fact]
    public async Task A_page_past_the_end_is_empty()
    {
        await ArrangeAsync();

        var response = await _factory.AsAdministrator().GetAsync($"{MasterProductsUrl}?page=9&pageSize=50");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(body.GetProperty("products").EnumerateArray());
        Assert.Equal(3, body.GetProperty("totalCount").GetInt32());
    }

    #endregion

    #region One product

    /// <summary>
    /// The third criterion over the wire: one master product with its supplier listings - the
    /// supplier's name, its own product id, its wording, the pack, the match status, the page it is
    /// on, when it was last checked and the pack price of the reading it points at, with that
    /// reading's currency, basis and instant.
    /// </summary>
    [Fact]
    public async Task One_product_reports_its_supplier_listings()
    {
        var arranged = await ArrangeAsync();

        var response = await _factory.AsAdministrator().GetAsync($"{MasterProductsUrl}/{arranged.ColaId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(arranged.ColaId, body.GetProperty("product").GetProperty("id").GetInt64());
        Assert.Equal(
            2,
            body.GetProperty("product").GetProperty("listingCounts").GetProperty("total").GetInt32());

        var listings = body.GetProperty("listings").EnumerateArray().ToArray();
        Assert.Equal(2, listings.Length);

        var priced = listings.Single(listing => listing.GetProperty("id").GetInt64() == arranged.PricedListingId);
        Assert.Equal("Alpha Grocer", priced.GetProperty("supplierName").GetString());
        Assert.Equal("SKU-COLA-30", priced.GetProperty("supplierProductId").GetString());
        Assert.Equal(
            "Alpha Grocer Coca-Cola Classic 375mL 30 pack",
            priced.GetProperty("title").GetString());
        Assert.Equal(375m, priced.GetProperty("sizeValue").GetDecimal());
        Assert.Equal(30, priced.GetProperty("unitsPerPack").GetInt32());
        Assert.Equal(
            (int)SupplierListingMatchStatus.Confirmed,
            priced.GetProperty("matchStatus").GetInt32());
        Assert.Equal("https://alpha.example/cola", priced.GetProperty("url").GetString());
        Assert.Equal(Observed, priced.GetProperty("lastCheckedAtUtc").GetDateTime());

        var price = priced.GetProperty("latestPrice");
        Assert.Equal(42.50m, price.GetProperty("packPrice").GetDecimal());
        Assert.Equal(SupplierPriceObservationPolicy.Currency, price.GetProperty("currency").GetString());
        Assert.Equal(
            (int)SupplierPriceBasis.InclusiveOfGst,
            price.GetProperty("priceBasis").GetInt32());
        Assert.Equal(Observed, price.GetProperty("observedAtUtc").GetDateTime());

        // A listing nothing has ever priced says so, rather than reporting a zero price.
        var never = listings.Single(listing => listing.GetProperty("id").GetInt64() != arranged.PricedListingId);
        Assert.Equal(JsonValueKind.Null, never.GetProperty("latestPrice").ValueKind);
        Assert.Equal(JsonValueKind.Null, never.GetProperty("lastCheckedAtUtc").ValueKind);
    }

    /// <summary>An id no master product has is <c>404</c>.</summary>
    [Fact]
    public async Task An_unknown_master_product_is_not_found()
    {
        await ArrangeAsync();

        var response = await _factory.AsAdministrator().GetAsync($"{MasterProductsUrl}/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion

    #region Nothing about a business

    /// <summary>
    /// <strong>The decisive assertion of this issue</strong>, over the serialized responses of both
    /// endpoints and a database where two businesses track the very product being read: neither
    /// business's name, neither business's id, neither business's product id and no count of
    /// businesses tracking anything appears anywhere in either body.
    ///
    /// <para>That is what makes "the master catalogue is browsable without learning who is tracking
    /// what" true by construction rather than by filtering: there is nowhere in this contract for any
    /// of it to travel, and nothing tenant-owned is read to build it.</para>
    /// </summary>
    [Fact]
    public async Task No_response_carries_any_business_data()
    {
        var arranged = await ArrangeAsync();
        var client = _factory.AsAdministrator();

        var listing = await (await client.GetAsync(MasterProductsUrl)).Content.ReadAsStringAsync();
        var detail = await (await client.GetAsync($"{MasterProductsUrl}/{arranged.ColaId}"))
            .Content.ReadAsStringAsync();

        // The two writes of issue #1024 answer with the same contract, so there is nowhere in them
        // for a business id, a business name or a tracking count to travel either.
        var created = await (await client.PostAsJsonAsync(
                MasterProductsUrl,
                Edit("Solo", "Lemon", 600m, Observed)))
            .Content.ReadAsStringAsync();
        var read = await ProductAsync(arranged.ColaZeroId);
        var updated = await (await client.PutAsJsonAsync(
                $"{MasterProductsUrl}/{arranged.ColaZeroId}",
                Edit("Coca-Cola", "Zero Sugar Corrected", 375m, read.UpdatedAtUtc)))
            .Content.ReadAsStringAsync();

        foreach (var body in new[] { listing, detail, created, updated })
        {
            Assert.DoesNotContain(
                SuperAdminBusinessesApiFactory.TradingBusinessName,
                body,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(SecondBusinessName, body, StringComparison.OrdinalIgnoreCase);

            // Asserted as JSON property names - the leading quote matters, because the supplier's own
            // product id is published and is a fact about a shelf rather than about anybody's
            // catalogue.
            Assert.DoesNotContain("\"businessId\"", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"productId\"", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("businessesWaiting", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("trackingCount", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("waitingKey", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Reading the catalogue writes nothing at all - not a master product, not a listing, not a
    /// mapping - which is the issue's last criterion asserted against the database rather than
    /// against the absence of a button.
    /// </summary>
    [Fact]
    public async Task Reading_the_catalogue_writes_nothing()
    {
        var arranged = await ArrangeAsync();
        var client = _factory.AsAdministrator();

        var before = await FingerprintAsync();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(MasterProductsUrl)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync($"{MasterProductsUrl}/{arranged.ColaId}")).StatusCode);

        Assert.Equal(before, await FingerprintAsync());
    }

    #endregion

    #region Arrangement

    /// <summary>
    /// Three master products - two of one brand, one of another - two supplier listings of the first
    /// one of which carries an accepted price, and <strong>two businesses tracking that same
    /// product</strong>, which is the tenant data the no-business-data assertion needs there to be.
    /// </summary>
    private async Task<Arranged> ArrangeAsync()
    {
        var secondBusinessId = await _factory.SeedBusinessAsync(SecondBusinessName, BusinessStatus.Active);
        var waitingKey = PriceCatalogueWaitingKey.For("Coca-Cola Classic", "375mL");

        await using var db = _factory.Database();

        var preset = new SupplierPreset
        {
            Name = "Alpha Grocer",
            NormalisedName = SupplierPresetPolicy.NormalisedName("Alpha Grocer"),
            IsEnabled = true,
            ServesNationwide = true,
            PriceCheckFreshnessHours = SupplierPriceFreshnessPolicy.DailyCheckLimitHours,
            CreatedAtUtc = Observed,
            UpdatedAtUtc = Observed,
        };
        db.SupplierPresets.Add(preset);

        var cola = MasterProduct("Coca-Cola", "Classic");
        var colaZero = MasterProduct("Coca-Cola", "Zero Sugar");
        var pepsi = MasterProduct("Pepsi", "Max");
        db.MasterProducts.AddRange(cola, colaZero, pepsi);

        var run = new PriceCollectorRun
        {
            StartedAtUtc = Observed,
            Status = PriceCollectorRunStatus.Running,
        };
        db.PriceCollectorRuns.Add(run);
        await db.SaveChangesAsync();

        var priced = new SupplierListing
        {
            SupplierPresetId = preset.Id,
            MasterProductId = cola.Id,
            SupplierProductId = "SKU-COLA-30",
            Url = "https://alpha.example/cola",
            Title = "Alpha Grocer Coca-Cola Classic 375mL 30 pack",
            SizeValue = 375m,
            SizeUnit = MasterProductSizeUnit.Millilitre,
            UnitsPerPack = 30,
            MatchStatus = SupplierListingMatchStatus.Confirmed,
            CreatedAtUtc = Observed,
            UpdatedAtUtc = Observed,
        };
        var unpriced = new SupplierListing
        {
            SupplierPresetId = preset.Id,
            MasterProductId = cola.Id,
            SupplierProductId = "SKU-COLA-SINGLE",
            MatchStatus = SupplierListingMatchStatus.Proposed,
            CreatedAtUtc = Observed,
            UpdatedAtUtc = Observed,
        };
        db.SupplierListings.AddRange(priced, unpriced);

        // Each business's own product, spelled differently on purpose: the two fold to one waiting
        // key, so there is cross-business tenant data in this database for a leak to show up in.
        var first = Product(_factory.TradingBusinessId, "Coca-Cola Classic", "375mL");
        var second = Product(secondBusinessId, "coca-cola  classic", "375 ML");
        db.Products.AddRange(first, second);
        await db.SaveChangesAsync();

        var observation = new SupplierPriceObservation
        {
            SupplierListingId = priced.Id,
            PriceCollectorRunId = run.Id,
            PackPrice = 42.50m,
            Currency = SupplierPriceObservationPolicy.Currency,
            PriceBasis = SupplierPriceBasis.InclusiveOfGst,
            StockStatus = SupplierStockStatus.InStock,
            ObservedAtUtc = Observed,
            CreatedAtUtc = Observed,
        };
        db.SupplierPriceObservations.Add(observation);
        db.ProductPriceTrackings.AddRange(
            Tracking(first, waitingKey),
            Tracking(second, waitingKey));
        await db.SaveChangesAsync();

        priced.LatestObservationId = observation.Id;
        priced.LastCheckedAtUtc = observation.ObservedAtUtc;
        priced.LastPriceChangeAtUtc = observation.ObservedAtUtc;
        await db.SaveChangesAsync();

        return new Arranged(cola.Id, colaZero.Id, pepsi.Id, priced.Id);
    }

    private sealed record Arranged(long ColaId, long ColaZeroId, long PepsiId, long PricedListingId);

    private static MasterProduct MasterProduct(string brand, string name) =>
        new()
        {
            Brand = brand,
            Name = name,
            SizeValue = 375m,
            SizeUnit = MasterProductSizeUnit.Millilitre,
            NormalisedKey = MasterProductPolicy.NormalisedKey(
                brand,
                name,
                375m,
                MasterProductSizeUnit.Millilitre),
            CreatedAtUtc = Observed,
            UpdatedAtUtc = Observed,
        };

    private static Product Product(int businessId, string name, string unit) =>
        new()
        {
            BusinessId = businessId,
            Name = name,
            Unit = unit,
            CreatedAt = Observed,
            UpdatedAt = Observed,
        };

    /// <summary>One business's statement that it wants that product priced, as the switch of #938 stores it.</summary>
    private static ProductPriceTracking Tracking(Product product, string waitingKey) =>
        new()
        {
            BusinessId = product.BusinessId,
            ProductId = product.Id,
            Status = ProductPriceTrackingStatus.Requested,
            WaitingKey = waitingKey,
            SearchName = product.Name.Trim(),
            UnitLabel = (product.Unit ?? string.Empty).Trim(),
            CreatedAtUtc = Observed,
            UpdatedAtUtc = Observed,
        };

    /// <summary>
    /// Everything this surface could conceivably have written, counted and described: the catalogue's
    /// own rows and the mappings beside them. A read must leave every one of them exactly as it was.
    /// </summary>
    private async Task<string> FingerprintAsync()
    {
        await using var db = _factory.Database();

        var masters = await db.MasterProducts
            .AsNoTracking()
            .OrderBy(master => master.Id)
            .Select(master => new { master.Id, master.Brand, master.Name, master.UpdatedAtUtc })
            .ToListAsync();
        var listings = await db.SupplierListings
            .AsNoTracking()
            .OrderBy(listing => listing.Id)
            .Select(listing => new
            {
                listing.Id,
                listing.MatchStatus,
                listing.LatestObservationId,
                listing.UpdatedAtUtc,
            })
            .ToListAsync();
        var mappings = await db.ProductPriceTrackings
            .AsNoTracking()
            .IgnoreQueryFilters()
            .OrderBy(mapping => mapping.Id)
            .Select(mapping => new
            {
                mapping.Id,
                mapping.BusinessId,
                mapping.Status,
                mapping.MasterProductId,
                mapping.UpdatedAtUtc,
            })
            .ToListAsync();
        var observations = await db.SupplierPriceObservations.AsNoTracking().CountAsync();

        return JsonSerializer.Serialize(new { masters, listings, mappings, observations });
    }

    private async Task<IReadOnlyList<HttpResponseMessage>> BothEndpointsAsync(HttpClient client)
    {
        var arranged = await ArrangeAsync();

        return
        [
            await client.GetAsync(MasterProductsUrl),
            await client.GetAsync($"{MasterProductsUrl}/{arranged.ColaId}"),
        ];
    }

    /// <summary>
    /// All four endpoints of this surface - both reads and both writes - so an authorization
    /// assertion covers the create and the edit of issue #1024 rather than only what #1023 shipped.
    /// </summary>
    private async Task<IReadOnlyList<HttpResponseMessage>> EveryEndpointAsync(HttpClient client)
    {
        var arranged = await ArrangeAsync();

        return
        [
            await client.GetAsync(MasterProductsUrl),
            await client.GetAsync($"{MasterProductsUrl}/{arranged.ColaId}"),
            .. await WritesAsync(client, arranged),
        ];
    }

    /// <summary>The two writes, each carrying a body a permitted caller would have been answered for.</summary>
    private static async Task<IReadOnlyList<HttpResponseMessage>> WritesAsync(
        HttpClient client,
        Arranged arranged) =>
        [
            await client.PostAsJsonAsync(MasterProductsUrl, Edit("Solo", "Lemon", 600m, Observed)),
            await client.PutAsJsonAsync(
                $"{MasterProductsUrl}/{arranged.ColaId}",
                Edit("Coca-Cola", "Classic Corrected", 375m, Observed)),
        ];

    /// <summary>
    /// The body both writes take: the description, and - for the edit - the instant it expects the row
    /// to carry. The create ignores the extra property, which is what lets one shape serve both in the
    /// authorization assertions.
    /// </summary>
    private static object Edit(string brand, string name, decimal sizeValue, DateTime lastChangedAtUtc) =>
        new
        {
            brand,
            name,
            sizeValue,
            sizeUnit = (int)MasterProductSizeUnit.Millilitre,
            lastChangedAtUtc,
        };

    /// <summary>One stored master product, read from outside the boundary for verifying a write.</summary>
    private async Task<MasterProduct> ProductAsync(long masterProductId)
    {
        await using var db = _factory.Database();

        return await db.MasterProducts
            .AsNoTracking()
            .SingleAsync(master => master.Id == masterProductId);
    }

    /// <summary>
    /// The refusal's own sentence, which is what the page shows: <c>DomainExceptionHandler</c> writes
    /// the Domain's caller-safe message onto the problem details as <c>message</c>.
    /// </summary>
    private static async Task<string?> MessageAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        return body.TryGetProperty("message", out var message) ? message.GetString() : null;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    #endregion
}
