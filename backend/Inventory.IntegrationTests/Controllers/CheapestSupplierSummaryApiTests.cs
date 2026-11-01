using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Suppliers;
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
/// The cheapest-supplier summary over the real HTTP pipeline (issue #930, parent #817):
/// authentication, the required scope, the <c>Dashboard.View</c> capability policy,
/// <c>BusinessScopeMiddleware</c>, the controller, the EF adapter and relational SQLite with the real
/// migration history.
///
/// <para>Both synthetic E2E businesses are used, because the decisive claims are about a boundary:
/// an id of the other business is <strong>ignored</strong> rather than reported, and nothing in the
/// answer says who else tracks the same shared master product. The shared catalogue's rows - the
/// master product, the supplier listings, the collector run and the observed prices - are arranged
/// directly, because writing them is #928's, #937's and #941's work and no endpoint of this issue
/// can.</para>
///
/// <para>The capability refusal itself belongs with the agreed role matrix in
/// <c>CapabilityAuthorizationApiTests</c>, where all three roles exist; the E2E host's two actors are
/// both Owners.</para>
/// </summary>
public sealed class CheapestSupplierSummaryApiTests
    : IClassFixture<CheapestSupplierSummaryApiTests.ApiFactory>
{
    private readonly ApiFactory _factory;

    public CheapestSupplierSummaryApiTests(ApiFactory factory) => _factory = factory;

    #region What one request may ask for

    /// <summary>The criterion: an empty list is refused with <c>400</c>.</summary>
    [Fact]
    public async Task An_empty_list_of_product_ids_is_refused()
    {
        var response = await _factory.BusinessA().PostAsJsonAsync(SummaryUri, new { productIds = Array.Empty<long>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>And so is a request for more than the agreed maximum.</summary>
    [Fact]
    public async Task More_than_the_maximum_number_of_product_ids_is_refused()
    {
        var tooMany = Enumerable
            .Range(1, CheapestSupplierSummaryRequestPolicy.MaxProductIds + 1)
            .Select(id => (long)id)
            .ToArray();

        var response = await _factory.BusinessA().PostAsJsonAsync(SummaryUri, new { productIds = tooMany });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Exactly the maximum is accepted: the bound is a limit, not a margin.</summary>
    [Fact]
    public async Task Exactly_the_maximum_number_of_product_ids_is_accepted()
    {
        var full = Enumerable
            .Range(1, CheapestSupplierSummaryRequestPolicy.MaxProductIds)
            .Select(id => (long)id)
            .ToArray();

        var response = await _factory.BusinessA().PostAsJsonAsync(SummaryUri, new { productIds = full });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The criterion: a repeated id is one question and is answered once.</summary>
    [Fact]
    public async Task A_repeated_product_id_is_answered_once()
    {
        var client = _factory.BusinessA();
        var productId = await _factory.MatchedProductAsync(client);

        var summary = await SummaryAsync(client, [productId, productId, productId]);

        Assert.Equal(
            [productId],
            Products(summary).Select(product => product.GetProperty("productId").GetInt64()));
    }

    #endregion

    #region The comparison

    /// <summary>
    /// The business outcome: the cheapest of the business's own linked suppliers per vending unit,
    /// with every supplier's offer beside it. The figures are the policy's own arithmetic - pack
    /// price divided by pack size, unrounded - so this test states them as that division rather than
    /// as a number somebody worked out.
    /// </summary>
    [Fact]
    public async Task The_summary_names_the_cheapest_linked_supplier_per_unit()
    {
        var client = _factory.BusinessA();
        var productId = await _factory.MatchedProductAsync(client);

        var summary = await SummaryAsync(client, [productId]);
        var product = Assert.Single(Products(summary));

        Assert.Equal((int)ProductPriceTrackingStatus.Matched, product.GetProperty("trackingStatus").GetInt32());
        Assert.Equal(
            ApiFactory.CheapPackPrice / ApiFactory.CheapPackSize,
            product.GetProperty("cheapestUnitPrice").GetDecimal());

        var cheapest = Assert.Single(
            Suppliers(product).Where(supplier => supplier.GetProperty("isCheapest").GetBoolean()));
        Assert.Equal(ApiFactory.CheapSupplierName, cheapest.GetProperty("supplierName").GetString());
        Assert.Equal(ApiFactory.CheapPackSize, cheapest.GetProperty("packQuantity").GetInt32());
        Assert.Equal(ApiFactory.CheapPackPrice, cheapest.GetProperty("packPrice").GetDecimal());
        Assert.True(cheapest.GetProperty("isSpecial").GetBoolean());
        Assert.Equal(ApiFactory.CheapListingUrl, cheapest.GetProperty("productUrl").GetString());
    }

    /// <summary>
    /// The criterion: a hidden supplier (issue #934's "Not used" switch) and a supplier the business
    /// holds no copy of take no part at all - not as an offer, and not as a row.
    /// </summary>
    [Fact]
    public async Task Hidden_and_unlinked_suppliers_are_not_in_the_answer()
    {
        var client = _factory.BusinessA();
        var productId = await _factory.MatchedProductAsync(client);

        var product = Assert.Single(Products(await SummaryAsync(client, [productId])));
        var names = Suppliers(product)
            .Select(supplier => supplier.GetProperty("supplierName").GetString())
            .ToList();

        Assert.DoesNotContain(ApiFactory.HiddenSupplierName, names);
        Assert.DoesNotContain(ApiFactory.UnlinkedSupplierName, names);
        Assert.DoesNotContain(ApiFactory.UncopiedPresetName, names);

        // And the hidden supplier's price - the cheapest in the catalogue - decided nothing.
        Assert.Equal(
            ApiFactory.CheapPackPrice / ApiFactory.CheapPackSize,
            product.GetProperty("cheapestUnitPrice").GetDecimal());
    }

    /// <summary>
    /// The criterion's "Woolworths: no match": a linked, not-hidden supplier with no listing of this
    /// product is published as itself, with no price, rather than left out of the popup.
    /// </summary>
    [Fact]
    public async Task A_supplier_with_no_listing_is_published_with_no_price()
    {
        var client = _factory.BusinessA();
        var productId = await _factory.MatchedProductAsync(client);

        var product = Assert.Single(Products(await SummaryAsync(client, [productId])));
        var unmatched = Assert.Single(
            Suppliers(product),
            supplier => supplier.GetProperty("supplierName").GetString() == ApiFactory.NoListingSupplierName);

        Assert.False(unmatched.GetProperty("hasListing").GetBoolean());
        Assert.False(unmatched.GetProperty("hasPrice").GetBoolean());
        Assert.Equal(JsonValueKind.Null, unmatched.GetProperty("unitPrice").ValueKind);
    }

    /// <summary>
    /// The criterion: the displayed and compared basis follows <c>Business.GstRegistered</c>, and
    /// both figures are published either way so the popup can show both.
    /// </summary>
    [Theory]
    [InlineData(true, (int)SupplierPriceBasis.ExclusiveOfGst)]
    [InlineData(false, (int)SupplierPriceBasis.InclusiveOfGst)]
    public async Task The_display_basis_follows_the_businesss_gst_registration(
        bool gstRegistered,
        int expectedBasis)
    {
        var client = _factory.BusinessA();
        var productId = await _factory.MatchedProductAsync(client);
        await _factory.SetGstRegisteredAsync(gstRegistered);

        try
        {
            var summary = await SummaryAsync(client, [productId]);

            Assert.Equal(expectedBasis, summary.GetProperty("displayBasis").GetInt32());

            var cheapest = Assert.Single(
                Suppliers(Products(summary)[0]).Where(supplier => supplier.GetProperty("isCheapest").GetBoolean()));
            var incGst = ApiFactory.CheapPackPrice / ApiFactory.CheapPackSize;
            var exGst = incGst / 1.1m;

            Assert.Equal(exGst, cheapest.GetProperty("unitPriceExGst").GetDecimal());
            Assert.Equal(incGst, cheapest.GetProperty("unitPriceIncGst").GetDecimal());
            Assert.Equal(
                gstRegistered ? exGst : incGst,
                cheapest.GetProperty("unitPrice").GetDecimal());
        }
        finally
        {
            await _factory.SetGstRegisteredAsync(null);
        }
    }

    /// <summary>
    /// The criterion: two suppliers at one price are one answer with both in it, and neither is
    /// quietly preferred.
    /// </summary>
    [Fact]
    public async Task Every_supplier_in_a_tie_is_reported_as_cheapest()
    {
        var client = _factory.BusinessA();
        var productId = await _factory.TiedProductAsync(client);

        var product = Assert.Single(Products(await SummaryAsync(client, [productId])));

        Assert.Equal(
            2,
            Suppliers(product).Count(supplier => supplier.GetProperty("isCheapest").GetBoolean()));
    }

    /// <summary>
    /// A supplier that says it is out of stock is still compared (owner decision, 1 November 2026):
    /// its offer has no ineligibility and its stock answer is published beside the price, so the
    /// page can say that stock may differ instead of dropping the price.
    /// </summary>
    [Fact]
    public async Task An_out_of_stock_offer_is_compared_and_carries_its_stock_answer()
    {
        var client = _factory.BusinessA();
        var productId = await _factory.MatchedProductAsync(client);

        var product = Assert.Single(Products(await SummaryAsync(client, [productId])));
        var outOfStock = Assert.Single(
            Suppliers(product),
            supplier => supplier.GetProperty("supplierName").GetString() == ApiFactory.OutOfStockSupplierName);

        Assert.Equal(JsonValueKind.Null, outOfStock.GetProperty("ineligibility").ValueKind);
        Assert.Equal((int)SupplierStockStatus.OutOfStock, outOfStock.GetProperty("stockStatus").GetInt32());
        Assert.False(outOfStock.GetProperty("isCheapest").GetBoolean());
        Assert.True(outOfStock.GetProperty("hasPrice").GetBoolean());
    }

    /// <summary>
    /// The criterion: a match the collector made and nobody has checked is published as such, so the
    /// popup can say "Matched automatically" (#941).
    /// </summary>
    [Fact]
    public async Task An_automatic_match_is_published_as_auto_matched()
    {
        var client = _factory.BusinessA();
        var productId = await _factory.MatchedProductAsync(client, ProductPriceTrackingStatus.AutoMatched);

        var product = Assert.Single(Products(await SummaryAsync(client, [productId])));

        Assert.Equal(
            (int)ProductPriceTrackingStatus.AutoMatched,
            product.GetProperty("trackingStatus").GetInt32());
    }

    /// <summary>
    /// A product the business does not track reads as exactly that - no status and no supplier rows
    /// - rather than as a product with no price.
    /// </summary>
    [Fact]
    public async Task An_untracked_product_is_published_with_no_status_and_no_suppliers()
    {
        var client = _factory.BusinessA();
        var productId = await _factory.ReorderProductIdAsync();

        var product = Assert.Single(Products(await SummaryAsync(client, [productId])));

        Assert.Equal(JsonValueKind.Null, product.GetProperty("trackingStatus").ValueKind);
        Assert.Empty(Suppliers(product));
        Assert.Equal(JsonValueKind.Null, product.GetProperty("cheapestUnitPrice").ValueKind);
    }

    #endregion

    #region The boundary

    /// <summary>
    /// The criterion: an id of another business is ignored, never reported. The other business's
    /// product is tracked and matched to the same master product at the time, so what is absent is
    /// absent because of the boundary rather than because there was nothing to say.
    /// </summary>
    [Fact]
    public async Task A_product_of_another_business_is_ignored_rather_than_reported()
    {
        var mine = await _factory.MatchedProductAsync(_factory.BusinessA());
        var theirs = await _factory.MatchedOtherBusinessProductAsync(_factory.BusinessB());

        var summary = await SummaryAsync(_factory.BusinessA(), [mine, theirs]);

        Assert.Equal(
            [mine],
            Products(summary).Select(product => product.GetProperty("productId").GetInt64()));
    }

    /// <summary>
    /// And a product that exists in no business at all is the same answer: absent, never an error
    /// that would confirm an id exists somewhere.
    /// </summary>
    [Fact]
    public async Task An_unknown_product_id_is_ignored()
    {
        var summary = await SummaryAsync(_factory.BusinessA(), [999999]);

        Assert.Empty(Products(summary));
    }

    /// <summary>
    /// The criterion about the platform-table read: nothing in the answer identifies another
    /// business, and nothing says anything about the shared catalogue beyond this business's own
    /// offers - not the master product's brand, not the supplier's own product id, and not the keys
    /// of either.
    /// </summary>
    [Fact]
    public async Task The_answer_identifies_no_other_business_and_no_catalogue_keys()
    {
        var client = _factory.BusinessA();
        var productId = await _factory.MatchedProductAsync(client);
        await _factory.MatchedOtherBusinessProductAsync(_factory.BusinessB());

        var response = await client.PostAsJsonAsync(SummaryUri, new { productIds = new[] { productId } });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(E2ETestFixture.BusinessBName, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFactory.MasterProductBrand, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFactory.CheapListingSku, body, StringComparison.Ordinal);
        Assert.DoesNotContain("masterProduct", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("listingId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("presetId", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The published keys, in the order the contract carries them. A client reads these names, so a
    /// rename or a reordering is a contract change rather than a refactor.
    /// </summary>
    [Fact]
    public async Task The_summary_publishes_exactly_the_agreed_keys()
    {
        var client = _factory.BusinessA();
        var productId = await _factory.MatchedProductAsync(client);

        var summary = await SummaryAsync(client, [productId]);

        Assert.Equal(
            ["displayBasis", "products"],
            summary.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            ["productId", "trackingStatus", "cheapestUnitPrice", "suppliers"],
            Products(summary)[0].EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            [
                "supplierId", "supplierName", "hasListing", "hasPrice", "packQuantity", "packPrice",
                "packPriceBasis", "unitPrice", "unitPriceExGst", "unitPriceIncGst", "isSpecial",
                "promotionEndsAtUtc", "stockStatus", "lastCheckedAtUtc", "productUrl",
                "ineligibility", "isCheapest",
            ],
            Suppliers(Products(summary)[0])[0].EnumerateObject().Select(property => property.Name));
    }

    #endregion

    private const string SummaryUri = "/api/cheapest-suppliers/summary";

    private static async Task<JsonElement> SummaryAsync(HttpClient client, long[] productIds)
    {
        var response = await client.PostAsJsonAsync(SummaryUri, new { productIds });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static IReadOnlyList<JsonElement> Products(JsonElement summary) =>
        [.. summary.GetProperty("products").EnumerateArray()];

    private static IReadOnlyList<JsonElement> Suppliers(JsonElement product) =>
        [.. product.GetProperty("suppliers").EnumerateArray()];

    /// <summary>
    /// The API hosted the way the end-to-end harness hosts it - the dedicated E2E environment, its
    /// two synthetic businesses and a disposable relational database - with this business's own
    /// linked suppliers and the shared catalogue's rows arranged directly.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        /// <summary>The supplier whose 24-pack special is the cheapest offer in the fixture.</summary>
        public const string CheapSupplierName = "E2E JB Metro";

        /// <summary>A linked supplier whose single is dearer per unit.</summary>
        public const string DearSupplierName = "E2E Coles";

        /// <summary>A linked supplier whose cheapest-looking price is out of stock.</summary>
        public const string OutOfStockSupplierName = "E2E BIG W";

        /// <summary>A linked supplier that does not list the product at all.</summary>
        public const string NoListingSupplierName = "E2E Woolworths";

        /// <summary>A linked supplier the business has switched out of use (issue #934).</summary>
        public const string HiddenSupplierName = "E2E Hidden Grocer";

        /// <summary>A supplier the business typed in itself, so it has no catalogue listings.</summary>
        public const string UnlinkedSupplierName = "E2E Corner Shop";

        /// <summary>A platform supplier record the business holds no copy of.</summary>
        public const string UncopiedPresetName = "E2E Uncopied Wholesaler";

        /// <summary>The master product the fixture's products are matched to.</summary>
        public const string MasterProductName = "E2E Cheapest Classic";

        /// <summary>Its brand, which no business endpoint may publish.</summary>
        public const string MasterProductBrand = "E2E Cheapest Brand";

        /// <summary>The cheapest listing's supplier product id, which is equally unpublishable.</summary>
        public const string CheapListingSku = "E2E-CHEAPEST-SKU";

        public const string CheapListingUrl = "https://example.test/jb-metro/cola";

        public const int CheapPackSize = 24;

        /// <summary>The cheapest pack price, GST-inclusive as observed.</summary>
        public const decimal CheapPackPrice = 22.80m;

        /// <summary>
        /// When the seeded prices were observed: an hour before the fixture is built, so they stay
        /// inside <see cref="SupplierPriceFreshnessPolicy.DailyCheckLimitHours"/>
        /// whatever day the suite runs. A fixed date expired 30 hours after it was written.
        /// </summary>
        private static readonly DateTime ObservedAtUtc = DateTime.UtcNow.AddHours(-1);

        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly SemaphoreSlim _seedLock = new(1, 1);

        private bool _seeded;
        private long _matchedProductId;
        private long _otherBusinessProductId;
        private long _tiedProductId;

        public ApiFactory()
        {
            E2ETestHostContentRoot.Pin();
            _connection.Open();
        }

        public HttpClient BusinessA() => As(E2ETestActors.BusinessAOwner.Key);

        public HttpClient BusinessB() => As(E2ETestActors.BusinessBOwner.Key);

        /// <summary>One of business A's own products that nothing in the catalogue is matched to.</summary>
        public Task<long> ReorderProductIdAsync() => ProductIdAsync(E2ETestFixture.ReorderProductName);

        /// <summary>
        /// Business A's tracked product, matched to the fixture's master product, with every
        /// supplier arrangement this file is about. Seeded once and reused, because every test here
        /// is a read.
        /// </summary>
        public async Task<long> MatchedProductAsync(
            HttpClient client,
            ProductPriceTrackingStatus status = ProductPriceTrackingStatus.Matched)
        {
            await SeedAsync(client);
            await SetStatusAsync(_matchedProductId, status);

            return _matchedProductId;
        }

        /// <summary>Business B's own tracked product, matched to the same shared master product.</summary>
        public async Task<long> MatchedOtherBusinessProductAsync(HttpClient client)
        {
            await SeedAsync(BusinessA());

            if (_otherBusinessProductId == 0)
            {
                _otherBusinessProductId = await NewProductAsync(client, "E2E Other Cheapest Cola", "375mL");
                await TrackAsync(client, _otherBusinessProductId);
                await MatchToMasterAsync(_otherBusinessProductId, ProductPriceTrackingStatus.Matched);
            }

            return _otherBusinessProductId;
        }

        /// <summary>
        /// Business A's second tracked product, matched to a master product two linked suppliers
        /// price identically per unit.
        /// </summary>
        public async Task<long> TiedProductAsync(HttpClient client)
        {
            await SeedAsync(client);

            return _tiedProductId;
        }

        /// <summary>
        /// Records the business's GST registration, which is what decides the basis it is compared
        /// on. <see langword="null"/> is the state the E2E business is seeded in - nothing recorded
        /// - which the summary treats as not registered.
        /// </summary>
        public async Task SetGstRegisteredAsync(bool? gstRegistered)
        {
            await using var db = Database();

            var business = await db.Businesses.SingleAsync(
                candidate => candidate.Name == E2ETestFixture.BusinessAName);
            business.GstRegistered = gstRegistered;
            await db.SaveChangesAsync();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(E2ETestEnvironment.EnvironmentName);
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
                _seedLock.Dispose();
            }
        }

        private async Task SeedAsync(HttpClient client)
        {
            await _seedLock.WaitAsync();
            try
            {
                if (_seeded)
                {
                    return;
                }

                await SeedSuppliersAsync();

                _matchedProductId = await NewProductAsync(client, "E2E Cheapest Cola", "375mL");
                await TrackAsync(client, _matchedProductId);
                await MatchToMasterAsync(_matchedProductId, ProductPriceTrackingStatus.Matched);
                await SeedOffersAsync();

                _tiedProductId = await NewProductAsync(client, "E2E Tied Cola", "600mL");
                await TrackAsync(client, _tiedProductId);
                await SeedTiedOffersAsync(_tiedProductId);

                _seeded = true;
            }
            finally
            {
                _seedLock.Release();
            }
        }

        /// <summary>
        /// The platform supplier records and business A's own suppliers: four linked and in use, one
        /// linked and hidden, one the business typed in itself, and one platform record it holds no
        /// copy of.
        /// </summary>
        private async Task SeedSuppliersAsync()
        {
            await using var db = Database();

            var businessId = await db.Businesses
                .Where(business => business.Name == E2ETestFixture.BusinessAName)
                .Select(business => business.Id)
                .SingleAsync();

            foreach (var (name, hidden) in new[]
            {
                (CheapSupplierName, false),
                (DearSupplierName, false),
                (OutOfStockSupplierName, false),
                (NoListingSupplierName, false),
                (HiddenSupplierName, true),
            })
            {
                var preset = NewPreset(name);
                db.SupplierPresets.Add(preset);
                await db.SaveChangesAsync();

                db.Suppliers.Add(new Supplier
                {
                    BusinessId = businessId,
                    Name = name,
                    SupplierPresetId = preset.Id,
                    IsHidden = hidden,
                });
            }

            db.SupplierPresets.Add(NewPreset(UncopiedPresetName));
            db.Suppliers.Add(new Supplier { BusinessId = businessId, Name = UnlinkedSupplierName });

            await db.SaveChangesAsync();
        }

        /// <summary>
        /// The offers the main product is compared from: the cheapest 24-pack special, a dearer
        /// single, an out-of-stock offer that would otherwise win, an offer from the hidden
        /// supplier that would win outright, and one from a platform record this business holds no
        /// copy of.
        /// </summary>
        private async Task SeedOffersAsync()
        {
            await using var db = Database();

            var masterProductId = await db.ProductPriceTrackings
                .Where(tracking => tracking.ProductId == _matchedProductId)
                .Select(tracking => tracking.MasterProductId!.Value)
                .SingleAsync();

            await AddOfferAsync(db, masterProductId, CheapSupplierName, CheapListingSku, CheapPackSize, CheapPackPrice, isPromotion: true, url: CheapListingUrl);
            await AddOfferAsync(db, masterProductId, DearSupplierName, "E2E-DEAR-SKU", 1, 1.27m);
            await AddOfferAsync(db, masterProductId, OutOfStockSupplierName, "E2E-OOS-SKU", 1, 2.00m, stockStatus: SupplierStockStatus.OutOfStock);
            await AddOfferAsync(db, masterProductId, HiddenSupplierName, "E2E-HIDDEN-SKU", 30, 3.00m);
            await AddOfferAsync(db, masterProductId, UncopiedPresetName, "E2E-UNCOPIED-SKU", 30, 3.00m);
        }

        /// <summary>Two linked suppliers at one unit price, through different pack sizes.</summary>
        private async Task SeedTiedOffersAsync(long productId)
        {
            await MatchToMasterAsync(productId, ProductPriceTrackingStatus.Matched, "E2E Tied Classic");

            await using var db = Database();

            var masterProductId = await db.ProductPriceTrackings
                .Where(tracking => tracking.ProductId == productId)
                .Select(tracking => tracking.MasterProductId!.Value)
                .SingleAsync();

            await AddOfferAsync(db, masterProductId, CheapSupplierName, "E2E-TIED-A-SKU", 10, 12.50m);
            await AddOfferAsync(db, masterProductId, DearSupplierName, "E2E-TIED-B-SKU", 2, 2.50m);
        }

        private async Task AddOfferAsync(
            AppDbContext db,
            long masterProductId,
            string supplierPresetName,
            string sku,
            int unitsPerPack,
            decimal packPrice,
            bool isPromotion = false,
            SupplierStockStatus stockStatus = SupplierStockStatus.InStock,
            string? url = null)
        {
            var presetId = await db.SupplierPresets
                .Where(preset => preset.Name == supplierPresetName)
                .Select(preset => preset.Id)
                .SingleAsync();

            var listing = new SupplierListing
            {
                SupplierPresetId = presetId,
                MasterProductId = masterProductId,
                SupplierProductId = sku,
                Url = url,
                UnitsPerPack = unitsPerPack,
                MatchStatus = SupplierListingMatchStatus.Confirmed,
                CreatedAtUtc = E2ETestFixture.SeededAtUtc,
                UpdatedAtUtc = E2ETestFixture.SeededAtUtc,
            };
            db.SupplierListings.Add(listing);
            await db.SaveChangesAsync();

            var run = await db.PriceCollectorRuns.FirstOrDefaultAsync();
            if (run is null)
            {
                run = new PriceCollectorRun
                {
                    StartedAtUtc = ObservedAtUtc,
                    CompletedAtUtc = ObservedAtUtc,
                    Status = PriceCollectorRunStatus.Succeeded,
                };
                db.PriceCollectorRuns.Add(run);
                await db.SaveChangesAsync();
            }

            var observation = new SupplierPriceObservation
            {
                SupplierListingId = listing.Id,
                PriceCollectorRunId = run.Id,
                PackPrice = packPrice,
                PriceBasis = SupplierPriceBasis.InclusiveOfGst,
                SupplierGstRate = 0.1000m,
                IsPromotion = isPromotion,
                StockStatus = stockStatus,
                ObservedAtUtc = ObservedAtUtc,
                SourceUrl = url,
                CreatedAtUtc = ObservedAtUtc,
            };
            db.SupplierPriceObservations.Add(observation);
            await db.SaveChangesAsync();

            listing.LatestObservationId = observation.Id;
            listing.LastCheckedAtUtc = ObservedAtUtc;
            listing.LastPriceChangeAtUtc = ObservedAtUtc;
            await db.SaveChangesAsync();
        }

        private static SupplierPreset NewPreset(string name) => new()
        {
            Name = name,
            NormalisedName = SupplierPresetPolicy.NormalisedName(name),
            IsEnabled = true,
            ServesNationwide = true,
            PricesIncludeGst = true,
            CreatedAtUtc = E2ETestFixture.SeededAtUtc,
            UpdatedAtUtc = E2ETestFixture.SeededAtUtc,
        };

        /// <summary>
        /// Matches a tracked product to a master product, creating it the first time. No endpoint
        /// can do this: deciding what a product <em>is</em> belongs to the matcher (#941) and the
        /// Super Admin review (#928).
        /// </summary>
        private async Task MatchToMasterAsync(
            long productId,
            ProductPriceTrackingStatus status,
            string masterProductName = MasterProductName)
        {
            await using var db = Database();

            var master = await db.MasterProducts
                .SingleOrDefaultAsync(candidate => candidate.Name == masterProductName);
            if (master is null)
            {
                master = new MasterProduct
                {
                    Brand = MasterProductBrand,
                    Name = masterProductName,
                    SizeValue = 375m,
                    SizeUnit = MasterProductSizeUnit.Millilitre,
                    NormalisedKey = MasterProductPolicy.NormalisedKey(
                        MasterProductBrand,
                        masterProductName,
                        375m,
                        MasterProductSizeUnit.Millilitre),
                    CreatedAtUtc = E2ETestFixture.SeededAtUtc,
                    UpdatedAtUtc = E2ETestFixture.SeededAtUtc,
                };
                db.MasterProducts.Add(master);
                await db.SaveChangesAsync();
            }

            var mapping = await db.ProductPriceTrackings.SingleAsync(
                tracking => tracking.ProductId == productId);
            mapping.MasterProductId = master.Id;
            mapping.Status = status;
            mapping.MatchSource = status == ProductPriceTrackingStatus.AutoMatched
                ? ProductPriceTrackingMatchSource.CollectorAutomaticMatch
                : ProductPriceTrackingMatchSource.SuperAdmin;
            await db.SaveChangesAsync();
        }

        /// <summary>Moves the main product's mapping between the two matched states.</summary>
        private async Task SetStatusAsync(long productId, ProductPriceTrackingStatus status)
        {
            await using var db = Database();

            var mapping = await db.ProductPriceTrackings.SingleAsync(
                tracking => tracking.ProductId == productId);
            mapping.Status = status;
            mapping.MatchSource = status == ProductPriceTrackingStatus.AutoMatched
                ? ProductPriceTrackingMatchSource.CollectorAutomaticMatch
                : ProductPriceTrackingMatchSource.SuperAdmin;
            await db.SaveChangesAsync();
        }

        private static async Task TrackAsync(HttpClient client, long productId)
        {
            var response = await client.PutAsJsonAsync(
                $"/api/products/{productId.ToString(CultureInfo.InvariantCulture)}/price-tracking",
                new { tracked = true });

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        private async Task<long> NewProductAsync(HttpClient client, string name, string unitLabel)
        {
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

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.GetProperty("id").GetInt64();
        }

        private async Task<long> ProductIdAsync(string name)
        {
            await using var db = Database();

            return await db.Products
                .Where(product => product.Name == name)
                .Select(product => product.Id)
                .FirstAsync();
        }

        private HttpClient As(string actorKey)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation(E2ETestActors.ActorHeaderName, actorKey);
            return client;
        }

        /// <summary>
        /// An unrestricted context for arranging both businesses' rows and the shared catalogue's
        /// from outside the boundary, never over HTTP. Resolving <c>Services</c> starts the host,
        /// which is what creates the schema and seeds the fixture this then reads.
        /// </summary>
        private AppDbContext Database()
        {
            _ = Services;
            return TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        }
    }
}
