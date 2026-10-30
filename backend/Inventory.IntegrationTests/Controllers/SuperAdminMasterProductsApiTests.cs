using System.Net;
using System.Text.Json;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Suppliers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The Super Admin master products API over real HTTP (issue #1023, parent #817): who may reach it,
/// what it reports, how it pages and searches, what it refuses - and the decisive assertion that
/// nothing about any business appears in any of it.
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
    /// The issue's own criterion, asked of both endpoints: members, Owners included, are refused. An
    /// Owner holds the strongest business role there is and reaches none of this, because the master
    /// catalogue is shared by every business and a business role is not platform authority.
    /// </summary>
    [Theory]
    [InlineData(SuperAdminBusinessesApiFactory.TradingOwnerObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.TradingManagerObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.ApplicantObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.StrangerObjectId)]
    public async Task Both_endpoints_refuse_a_caller_who_is_not_the_configured_administrator(
        string objectId)
    {
        var client = _factory.Authenticated(objectId);

        foreach (var response in await BothEndpointsAsync(client))
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

        foreach (var response in await BothEndpointsAsync(client))
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

        foreach (var response in await BothEndpointsAsync(client))
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

        foreach (var response in await BothEndpointsAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    /// <summary>
    /// A role preview is read-only everywhere (issue #720), and these two endpoints are reads: the
    /// administrator previewing a business role still reads the catalogue, because a preview refuses
    /// writes and there is no write on this surface to refuse. What it must not do is turn the
    /// platform-admin answer into a member refusal.
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
    /// A member previewing a role reaches it no more than that member does: the preview header is not
    /// a grant of anything, least of all of platform authority.
    /// </summary>
    [Fact]
    public async Task A_member_previewing_a_role_is_still_refused()
    {
        var client = _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);
        client.DefaultRequestHeaders.Add("X-Role-Preview", "Owner");

        foreach (var response in await BothEndpointsAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
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
    /// The surface is read only, which is the issue's last criterion: there is no create, edit or
    /// delete action at either address, so every write verb is refused and nothing is stored.
    ///
    /// <para>The refusal is <c>403</c> rather than <c>405</c>, and that is the pipeline being exactly
    /// as strict as it should be: no action matches the method, so the request reaches no endpoint
    /// carrying <c>PlatformDiagnosticsEndpointAttribute</c>, and <c>BusinessScopeMiddleware</c>
    /// therefore applies the ordinary membership requirement - which the administrator, a member of no
    /// business, does not meet. Either answer would mean "there is nothing here to write"; this one
    /// also means no write could be reached without a new, classified endpoint.</para>
    /// </summary>
    [Fact]
    public async Task There_is_no_write_on_this_surface()
    {
        var arranged = await ArrangeAsync();
        var client = _factory.AsAdministrator();
        var before = await FingerprintAsync();

        foreach (var response in new[]
        {
            await client.PostAsync(MasterProductsUrl, content: null),
            await client.PutAsync($"{MasterProductsUrl}/{arranged.ColaId}", content: null),
            await client.DeleteAsync($"{MasterProductsUrl}/{arranged.ColaId}"),
        })
        {
            Assert.True(
                response.StatusCode is HttpStatusCode.Forbidden
                    or HttpStatusCode.MethodNotAllowed
                    or HttpStatusCode.NotFound,
                $"A write reached something: {response.StatusCode}.");
        }

        Assert.Equal(before, await FingerprintAsync());
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
    public async Task Neither_response_carries_any_business_data()
    {
        var arranged = await ArrangeAsync();
        var client = _factory.AsAdministrator();

        var listing = await (await client.GetAsync(MasterProductsUrl)).Content.ReadAsStringAsync();
        var detail = await (await client.GetAsync($"{MasterProductsUrl}/{arranged.ColaId}"))
            .Content.ReadAsStringAsync();

        foreach (var body in new[] { listing, detail })
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

    #endregion
}
