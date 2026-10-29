using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Suppliers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ApiFactory = InventoryApi.Tests.Auth.PriceCollectorTokenValidationTests.ApiFactory;
using CollectorTokens = InventoryApi.Tests.Auth.PriceCollectorTokenValidationTests.CollectorTokens;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// One collector run through the <strong>real endpoints</strong>, end to end (issue #941's own
/// validation requirement): the work list is read, a match is proposed and applied automatically, and
/// then the results for that same work item are accepted as the newly matched product's.
///
/// <para><strong>It is the test the three endpoints cannot be split into.</strong> The work list is
/// issued once at the start of a run, so a key matched <em>during</em> that run is a key whose issued
/// work item still says "waiting". Issue #937 resolves that by reading the suggestion that applied the
/// match, and this issue is what writes one - so "a known listing of the newly assigned master is
/// accepted as that product's listing rather than refused as belonging to another product" is a
/// property of the two endpoints together and of neither alone.</para>
///
/// <para>Two businesses tracking the same product, over the production pipeline: the real collector
/// bearer scheme and policy, the real business-scope middleware (which leaves a collector request
/// denied), the real query filters, the real controllers and one relational SQLite database. Only the
/// authority metadata is stubbed, exactly as <c>PriceCollectorTokenValidationTests</c> stubs it.</para>
/// </summary>
public sealed class PriceCollectorMatchApiTests : IClassFixture<ApiFactory>
{
    private const string WorkEndpoint = "/api/price-collector/work";
    private const string MatchesEndpoint = "/api/price-collector/matches";
    private const string ResultsEndpoint = "/api/price-collector/results";

    private const string SearchName = "Coca-Cola Classic";
    private const string UnitLabel = "375mL";

    /// <summary>The supplier product id of the listing the catalogue already holds, confirmed.</summary>
    private const string KnownSupplierProductId = "SKU-COLA-30";

    /// <summary>And of the pack this run finds for the first time.</summary>
    private const string NewSupplierProductId = "SKU-COLA-24";

    private static readonly DateTime SeededAt = new(2026, 10, 29, 4, 0, 0, DateTimeKind.Utc);

    private readonly ApiFactory _factory;

    public PriceCollectorMatchApiTests(ApiFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// The whole run: work list, match, results. Both businesses' mappings end up auto-matched to the
    /// master product, the listing the catalogue already held is accepted as that product's price, and
    /// the new pack attaches to the master product rather than to the key.
    /// </summary>
    [Fact]
    public async Task A_match_applied_mid_run_resolves_that_runs_own_results()
    {
        var seeded = await SeedAsync();

        // 1. The work list, which issues the run and the work item every later call cites.
        var work = await ReadAsync(await Get(WorkEndpoint));
        var runId = work.GetProperty("runId").GetInt64();

        var waiting = Assert.Single(work.GetProperty("waitingProducts").EnumerateArray());
        var waitingKey = waiting.GetProperty("waitingKey").GetString();
        var workItemId = Assert.Single(waiting.GetProperty("workItems").EnumerateArray())
            .GetProperty("workItemId")
            .GetInt64();

        Assert.Equal(PriceCatalogueWaitingKey.For(SearchName, UnitLabel), waitingKey);
        Assert.Equal(SearchName, waiting.GetProperty("searchName").GetString());

        // The master products to match against travel with it, because something is waiting.
        Assert.Contains(
            work.GetProperty("masterProductsForMatching").EnumerateArray(),
            master => master.GetProperty("id").GetInt64() == seeded.MasterProductId);

        // 2. The match, which the backend checks and then applies.
        var matched = await ReadAsync(await Post(
            MatchesEndpoint,
            new
            {
                runId,
                proposals = new[]
                {
                    new
                    {
                        waitingKey,
                        masterProductId = seeded.MasterProductId,
                        confidence = ProductMatchConfidence.High,
                        reason = "The brand, the variant and the size all agree.",
                    },
                },
            }));

        var applied = Assert.Single(matched.GetProperty("autoMatched").EnumerateArray());
        Assert.Equal(seeded.MasterProductId, applied.GetProperty("masterProductId").GetInt64());
        Assert.Empty(matched.GetProperty("tentative").EnumerateArray());
        Assert.Empty(matched.GetProperty("rejected").EnumerateArray());

        // Nothing about a business anywhere in the answer - not a name, not a count.
        var body = JsonSerializer.Serialize(matched);
        Assert.DoesNotContain(ApiFactory.BusinessName, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Second Vending", body, StringComparison.OrdinalIgnoreCase);

        // 3. The results for the very work item that was issued for the key, now matched: the known
        // listing of the master product is this product's listing, and a new pack attaches to the
        // master product.
        var observed = DateTime.UtcNow;

        var results = await ReadAsync(await Post(
            ResultsEndpoint,
            new
            {
                runId,
                rows = new[]
                {
                    Row(seeded.SupplierPresetId, KnownSupplierProductId, workItemId, observed, 42.00m),
                    Row(seeded.SupplierPresetId, NewSupplierProductId, workItemId, observed, 34.00m, 24),
                },
            }));

        var accepted = Assert.Single(results.GetProperty("accepted").EnumerateArray());
        Assert.Equal(KnownSupplierProductId, accepted.GetProperty("supplierProductId").GetString());
        Assert.Empty(results.GetProperty("rejected").EnumerateArray());

        var pending = Assert.Single(results.GetProperty("pending").EnumerateArray());
        Assert.Equal(NewSupplierProductId, pending.GetProperty("supplierProductId").GetString());

        await using var db = TestAppDbContext.Unrestricted(_factory.DatabaseOptions);

        // Both businesses' mappings, auto-matched and recorded as the collector's own match.
        var mappings = await db.ProductPriceTrackings.AsNoTracking().ToListAsync();
        Assert.Equal(2, mappings.Count);
        Assert.All(mappings, mapping =>
        {
            Assert.Equal(ProductPriceTrackingStatus.AutoMatched, mapping.Status);
            Assert.Equal(seeded.MasterProductId, mapping.MasterProductId);
            Assert.Equal(
                ProductPriceTrackingMatchSource.CollectorAutomaticMatch,
                mapping.MatchSource);
        });

        var suggestion = Assert.Single(await db.ProductMatchSuggestions.AsNoTracking().ToListAsync());
        Assert.Equal(ProductMatchSuggestionStatus.AutoApplied, suggestion.Status);
        Assert.Equal(ProductMatchSuggestionPolicy.CollectorRunFor(runId), suggestion.CollectorRun);

        var listings = await db.SupplierListings.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
        Assert.Equal(2, listings.Count);
        Assert.All(listings, listing =>
        {
            Assert.Equal(seeded.MasterProductId, listing.MasterProductId);
            Assert.Null(listing.WaitingKey);
        });

        // The known listing was already confirmed, so its reading is the price it now carries; the
        // new pack is a proposal whose reading is held.
        var known = listings.Single(listing => listing.SupplierProductId == KnownSupplierProductId);
        Assert.Equal(SupplierListingMatchStatus.Confirmed, known.MatchStatus);
        Assert.NotNull(known.LatestObservationId);

        var found = listings.Single(listing => listing.SupplierProductId == NewSupplierProductId);
        Assert.Equal(SupplierListingMatchStatus.Proposed, found.MatchStatus);
        Assert.Null(found.LatestObservationId);
        Assert.Equal(PriceCatalogueWaitingKey.For(SearchName, UnitLabel), found.OriginWaitingKey);
    }

    private static object Row(
        int supplierPresetId,
        string supplierProductId,
        long workItemId,
        DateTime observedAtUtc,
        decimal packPrice,
        int packQuantity = 30) =>
        new
        {
            workItemId,
            supplierPresetId,
            supplierProductId,
            title = "Coca-Cola Classic Soft Drink Cans 375mL",
            variant = "Classic",
            sizeValue = 375m,
            sizeUnit = MasterProductSizeUnit.Millilitre,
            barcode = "0012345678905",
            barcodeScope = BarcodeScope.Unit,
            packQuantity,
            packPrice,
            currency = "AUD",
            priceBasis = SupplierPriceBasis.InclusiveOfGst,
            isPromotion = false,
            stockStatus = SupplierStockStatus.InStock,
            observedAtUtc,
        };

    /// <summary>
    /// Two businesses that both track the same cola, one master product to match it to, one supplier
    /// the collector may search, and one confirmed listing of that master product.
    ///
    /// <para>The mappings and the listing are written directly: what is under test is the three
    /// endpoints, and arranging the state through the switch of #938 would need a member's token for
    /// each business and prove nothing extra. Every shipped supplier master record has its collection
    /// route cleared, so the only due supplier on the run is this test's own.</para>
    /// </summary>
    private async Task<SeededCatalogue> SeedAsync()
    {
        await _factory.SeedAsync();

        await using var db = TestAppDbContext.Unrestricted(_factory.DatabaseOptions);

        if (await db.MasterProducts.AnyAsync())
        {
            var existing = await db.MasterProducts.AsNoTracking().FirstAsync();
            var preset = await db.SupplierPresets
                .AsNoTracking()
                .FirstAsync(row => row.Name == "Alpha Grocer");

            return new SeededCatalogue(existing.Id, preset.Id);
        }

        foreach (var shipped in await db.SupplierPresets.ToListAsync())
        {
            shipped.PriceConnector = null;
            shipped.PriceConnectorKey = null;
        }

        var secondBusiness = new Business
        {
            Name = "Second Vending",
            TimeZoneId = "Australia/Sydney",
            Status = BusinessStatus.Active,
            CreatedAtUtc = SeededAt,
        };

        db.Businesses.Add(secondBusiness);

        var supplier = new SupplierPreset
        {
            Name = "Alpha Grocer",
            NormalisedName = SupplierPresetPolicy.NormalisedName("Alpha Grocer"),
            IsEnabled = true,
            ServesNationwide = true,
            PriceConnector = SupplierPriceConnector.BigW,
            PricesIncludeGst = true,
            CreatedAtUtc = SeededAt,
            UpdatedAtUtc = SeededAt,
        };

        var master = new MasterProduct
        {
            Brand = "Coca-Cola",
            Name = "Classic",
            SizeValue = 375m,
            SizeUnit = MasterProductSizeUnit.Millilitre,
            NormalisedKey = MasterProductPolicy.NormalisedKey(
                "Coca-Cola",
                "Classic",
                375m,
                MasterProductSizeUnit.Millilitre),
            CreatedAtUtc = SeededAt,
            UpdatedAtUtc = SeededAt,
        };

        db.SupplierPresets.Add(supplier);
        db.MasterProducts.Add(master);
        await db.SaveChangesAsync();

        // One product per business, spelled differently on purpose, and the mapping each business
        // would have after turning the switch on.
        var first = AddProduct(db, 1, SearchName, UnitLabel);
        var second = AddProduct(db, secondBusiness.Id, "coca-cola  classic", "375 ML");
        await db.SaveChangesAsync();

        db.ProductPriceTrackings.Add(Tracking(1, first.Id, SearchName, UnitLabel));
        db.ProductPriceTrackings.Add(Tracking(secondBusiness.Id, second.Id, "coca-cola  classic", "375 ML"));

        // The listing the catalogue already holds for the master product, confirmed by a person, with
        // the identity a submitted row is checked against.
        db.SupplierListings.Add(new SupplierListing
        {
            SupplierPresetId = supplier.Id,
            MasterProductId = master.Id,
            SupplierProductId = KnownSupplierProductId,
            Variant = "Classic",
            SizeValue = 375m,
            SizeUnit = MasterProductSizeUnit.Millilitre,
            Barcode = "0012345678905",
            BarcodeScope = BarcodeScope.Unit,
            UnitsPerPack = 30,
            MatchStatus = SupplierListingMatchStatus.Confirmed,
            CreatedAtUtc = SeededAt,
            UpdatedAtUtc = SeededAt,
        });

        await db.SaveChangesAsync();

        return new SeededCatalogue(master.Id, supplier.Id);
    }

    private static Product AddProduct(AppDbContext db, int businessId, string name, string unit)
    {
        var product = new Product
        {
            BusinessId = businessId,
            Name = name,
            Unit = unit,
            CreatedAt = SeededAt,
            UpdatedAt = SeededAt,
        };

        db.Products.Add(product);

        return product;
    }

    private static ProductPriceTracking Tracking(
        int businessId,
        long productId,
        string searchName,
        string unitLabel) =>
        new()
        {
            BusinessId = businessId,
            ProductId = productId,
            Status = ProductPriceTrackingStatus.Requested,
            WaitingKey = PriceCatalogueWaitingKey.For(searchName, unitLabel),
            SearchName = searchName,
            UnitLabel = unitLabel,
            CreatedAtUtc = SeededAt,
            UpdatedAtUtc = SeededAt,
        };

    private Task<HttpResponseMessage> Get(string path) => Send(path, content: null);

    private Task<HttpResponseMessage> Post(string path, object body) => Send(path, body);

    private async Task<HttpResponseMessage> Send(string path, object? content)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CollectorTokens.AppOnly());

        return content is null
            ? await client.GetAsync(path)
            : await client.PostAsJsonAsync(path, content);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(body);

        return document.RootElement.Clone();
    }

    /// <summary>What the seeded catalogue holds, for the one test above to cite.</summary>
    private sealed record SeededCatalogue(long MasterProductId, int SupplierPresetId);
}
