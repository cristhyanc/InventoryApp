using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Application.PriceCatalogue;
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
/// The business lookup of master products over the real HTTP pipeline (issue #1049, parent #817):
/// authentication, the required scope, the <c>Inventory.Operate</c> capability policy,
/// <c>BusinessScopeMiddleware</c>, the controller, the EF adapter and relational SQLite with the real
/// migration history.
///
/// <para><strong>It is a member-facing read of a platform table, so what it does <em>not</em> publish
/// is as much under test as what it does.</strong> The field set of every entry is asserted exactly,
/// both businesses of the E2E host ask the same question and must get the same answer, and one
/// business's mapping over a master product must change neither the other's results nor its own -
/// because a master product is a fact about a product and there is nowhere in this answer for
/// anything about a business to travel.</para>
///
/// <para>The master products are seeded directly: no endpoint a business can reach writes one, and
/// the Super Admin create of #1024 is a different surface with its own tests. Every seeded
/// description carries a marker word of this fixture's own, so a search is deterministic whatever
/// else the shared E2E fixture holds.</para>
/// </summary>
public sealed class MasterProductSuggestionApiTests
    : IClassFixture<MasterProductSuggestionApiTests.ApiFactory>
{
    /// <summary>
    /// Exactly the fields the issue permits per master product: the safe identity, and the prefill.
    /// Nothing else - no normalised key, no instants, no listing count, no price, no mapping and
    /// nothing about any business.
    /// </summary>
    private static readonly string[] PermittedFields =
    [
        "brand",
        "id",
        "name",
        "productName",
        "sizeUnit",
        "sizeValue",
        "unitLabel",
    ];

    private readonly ApiFactory _factory;

    public MasterProductSuggestionApiTests(ApiFactory factory) => _factory = factory;

    #region What it answers

    /// <summary>
    /// The criterion: each entry carries the master product's id, brand, name, size value, size unit
    /// code and the two prefill values - and exactly those.
    /// </summary>
    [Fact]
    public async Task An_entry_carries_the_safe_identity_and_the_prefill_and_nothing_else()
    {
        var page = await SuggestionsAsync(await _factory.BusinessAAsync(), ApiFactory.CornChipsSearch);

        var entry = Assert.Single(Products(page));

        Assert.Equal(
            PermittedFields,
            entry.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal).ToArray());

        Assert.Equal(await _factory.CornChipsIdAsync(), entry.GetProperty("id").GetInt64());
        Assert.Equal(ApiFactory.CornChipsBrand, entry.GetProperty("brand").GetString());
        Assert.Equal(ApiFactory.CornChipsName, entry.GetProperty("name").GetString());
        Assert.Equal(45m, entry.GetProperty("sizeValue").GetDecimal());
        Assert.Equal("g", entry.GetProperty("sizeUnit").GetString());
        Assert.Equal(
            $"{ApiFactory.CornChipsBrand} {ApiFactory.CornChipsName}",
            entry.GetProperty("productName").GetString());
        Assert.Equal("45g", entry.GetProperty("unitLabel").GetString());
    }

    /// <summary>
    /// The prefill is the Domain formatter's answer and not the endpoint's own spelling of a size:
    /// a litre size keeps its decimal and drops its trailing zeros, and the unit code follows with no
    /// space.
    /// </summary>
    [Fact]
    public async Task The_prefill_is_the_domain_formatters_own_name_and_label()
    {
        var page = await SuggestionsAsync(await _factory.BusinessAAsync(), ApiFactory.Marker);

        foreach (var entry in Products(page))
        {
            var prefill = MasterProductPrefill.For(
                entry.GetProperty("brand").GetString(),
                entry.GetProperty("name").GetString(),
                entry.GetProperty("sizeValue").GetDecimal(),
                UnitOf(entry.GetProperty("sizeUnit").GetString()!));

            Assert.Equal(prefill.ProductName, entry.GetProperty("productName").GetString());
            Assert.Equal(prefill.UnitLabel, entry.GetProperty("unitLabel").GetString());
        }
    }

    /// <summary>
    /// The search matches the brand or the name, without regard to case: a person typing a brand and
    /// a person typing a product's own words find the same catalogue.
    /// </summary>
    [Theory]
    [InlineData(ApiFactory.Marker)]
    [InlineData("ZEDCO")]
    [InlineData("zedCO")]
    public async Task The_search_matches_the_brand_or_the_name_case_insensitively(string search)
    {
        var page = await SuggestionsAsync(await _factory.BusinessAAsync(), search);

        Assert.Equal(
            await _factory.ExpectedMarkerOrderAsync(),
            Products(page).Select(entry => entry.GetProperty("id").GetInt64()).ToArray());
    }

    /// <summary>
    /// A product whose brand and name hold neither the search text is not in the answer - stated
    /// explicitly, because "the search narrowed nothing" is the failure a substring match hides.
    /// </summary>
    [Fact]
    public async Task A_product_the_search_does_not_match_is_not_in_the_answer()
    {
        var page = await SuggestionsAsync(await _factory.BusinessAAsync(), ApiFactory.Marker);

        Assert.DoesNotContain(
            await _factory.UnmatchedIdAsync(),
            Products(page).Select(entry => entry.GetProperty("id").GetInt64()).ToArray());
    }

    /// <summary>
    /// The order is fixed by the server - brand, name, size, then the row's own key - which is what
    /// makes paging over it stable rather than a different slice on every request.
    /// </summary>
    [Fact]
    public async Task The_order_is_brand_then_name_then_size_then_the_rows_own_key()
    {
        var expected = await _factory.ExpectedMarkerOrderAsync();

        var first = Products(await SuggestionsAsync(await _factory.BusinessAAsync(), ApiFactory.Marker, pageSize: 2))
            .Select(entry => entry.GetProperty("id").GetInt64())
            .ToArray();
        var second = Products(
                await SuggestionsAsync(await _factory.BusinessAAsync(), ApiFactory.Marker, page: 2, pageSize: 2))
            .Select(entry => entry.GetProperty("id").GetInt64())
            .ToArray();

        Assert.Equal(expected.Take(2), first);
        Assert.Equal(expected.Skip(2).Take(2), second);
    }

    #endregion

    #region The bounds

    /// <summary>
    /// The page size is the server's decision: absent it is the default, above the maximum it is the
    /// maximum, and the size actually served is reported back so a client is never left guessing.
    /// </summary>
    [Theory]
    [InlineData(null, MasterProductSuggestionPaging.DefaultPageSize)]
    [InlineData(0, MasterProductSuggestionPaging.DefaultPageSize)]
    [InlineData(-5, MasterProductSuggestionPaging.DefaultPageSize)]
    [InlineData(3, 3)]
    [InlineData(20, MasterProductSuggestionPaging.MaxPageSize)]
    [InlineData(500, MasterProductSuggestionPaging.MaxPageSize)]
    public async Task A_requested_page_size_is_bounded_and_the_served_size_is_reported(
        int? requested,
        int served)
    {
        var page = await SuggestionsAsync(await _factory.BusinessAAsync(), ApiFactory.Marker, pageSize: requested);

        Assert.Equal(served, page.GetProperty("pageSize").GetInt32());
        Assert.True(
            Products(page).Count <= served,
            "A page must never carry more entries than the page size it reports.");
    }

    /// <summary>
    /// And a page carries whether there is more to read, so an autocomplete can say so without a
    /// count of the shared catalogue being published to a business.
    /// </summary>
    [Fact]
    public async Task A_page_says_whether_more_matches_remain()
    {
        var total = (await _factory.ExpectedMarkerOrderAsync()).Length;

        var firstOfTwo = await SuggestionsAsync(await _factory.BusinessAAsync(), ApiFactory.Marker, pageSize: 2);
        Assert.True(firstOfTwo.GetProperty("hasMore").GetBoolean());
        Assert.Equal(1, firstOfTwo.GetProperty("page").GetInt32());

        var whole = await SuggestionsAsync(await _factory.BusinessAAsync(), ApiFactory.Marker, pageSize: total);
        Assert.False(whole.GetProperty("hasMore").GetBoolean());

        var past = await SuggestionsAsync(await _factory.BusinessAAsync(), ApiFactory.Marker, page: 99, pageSize: 2);
        Assert.Empty(Products(past));
        Assert.False(past.GetProperty("hasMore").GetBoolean());
    }

    /// <summary>
    /// The search term is required and bounded: nothing, whitespace, one character, or more than the
    /// documented maximum is refused with the rule's own sentence, and nothing is read.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("z")]
    [InlineData(" z ")]
    public async Task A_search_term_that_is_absent_or_too_short_is_refused(string search)
    {
        var response = await (await _factory.BusinessAAsync()).GetAsync(Url(search));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            MasterProductSuggestionPaging.SearchTooShortMessage,
            await MessageAsync(response));
    }

    /// <summary>With no <c>q</c> parameter at all, which is the same absence.</summary>
    [Fact]
    public async Task A_request_with_no_search_term_is_refused()
    {
        var response = await (await _factory.BusinessAAsync())
            .GetAsync("/api/products/master-product-suggestions");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            MasterProductSuggestionPaging.SearchTooShortMessage,
            await MessageAsync(response));
    }

    /// <summary>
    /// And a search longer than the documented maximum is refused rather than truncated: a truncated
    /// search is a search for something else.
    /// </summary>
    [Fact]
    public async Task A_search_term_longer_than_the_maximum_is_refused()
    {
        var response = await (await _factory.BusinessAAsync())
            .GetAsync(Url(new string('z', MasterProductSuggestionPaging.MaxSearchLength + 1)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            MasterProductSuggestionPaging.SearchTooLongMessage,
            await MessageAsync(response));
    }

    /// <summary>The maximum itself is accepted, so the bound is the one that is documented.</summary>
    [Fact]
    public async Task A_search_term_of_exactly_the_maximum_length_is_accepted()
    {
        var response = await (await _factory.BusinessAAsync())
            .GetAsync(Url(new string('z', MasterProductSuggestionPaging.MaxSearchLength)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    #endregion

    #region Who may reach it

    /// <summary>
    /// An anonymous request is refused by authentication, and nothing about the shared catalogue
    /// leaves the API: "you are not signed in" is answered before any business is resolved.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_request_is_unauthorized()
    {
        var response = await _factory.CreateClient().GetAsync(Url(ApiFactory.Marker));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A signed-in caller whose business does not resolve is refused exactly as they are by the
    /// product-create endpoint beside it: the capability policy cannot pass with no resolved
    /// membership, and the answer is never an unfiltered lookup.
    /// </summary>
    [Fact]
    public async Task A_signed_in_caller_with_no_resolved_business_is_refused()
    {
        var client = await _factory.WithoutMembershipAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync(Url(ApiFactory.Marker))).StatusCode);

        // The same answer the create beside it gives that caller, which is what "behaves as the
        // existing product-create endpoint does" means here.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/products", new { name = "Nothing" })).StatusCode);
    }

    #endregion

    #region What it says about other businesses, and what it writes

    /// <summary>
    /// The decisive assertion: the two businesses of this host ask the same question and get the same
    /// answer, including after one of them has tracked a master product. A master product carries no
    /// business identity at all, so there is nothing in this answer for one business to learn about
    /// another - and nothing is filtered per business either, which is what makes the two answers
    /// equal rather than merely similar.
    /// </summary>
    [Fact]
    public async Task Both_businesses_get_the_same_answer_and_a_mapping_changes_neither()
    {
        var before = await RawAsync(await _factory.BusinessAAsync(), ApiFactory.Marker);

        var trackedProductId = await _factory.TrackedProductOfBusinessBAsync();
        try
        {
            var businessA = await RawAsync(await _factory.BusinessAAsync(), ApiFactory.Marker);
            var businessB = await RawAsync(await _factory.BusinessBAsync(), ApiFactory.Marker);

            Assert.Equal(before, businessA);
            Assert.Equal(businessA, businessB);
            Assert.DoesNotContain(
                ApiFactory.BusinessBProductName,
                businessA,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await _factory.UntrackAsync(trackedProductId);
        }
    }

    /// <summary>
    /// The issue's last criterion: a lookup writes nothing at all. The master products are the same
    /// rows with the same instants afterwards, and no mapping has appeared.
    /// </summary>
    [Fact]
    public async Task A_lookup_writes_nothing()
    {
        var before = await _factory.MasterProductStateAsync();
        var mappingsBefore = await _factory.MappingCountAsync();

        _ = await SuggestionsAsync(await _factory.BusinessAAsync(), ApiFactory.Marker);
        _ = await SuggestionsAsync(await _factory.BusinessBAsync(), ApiFactory.CornChipsSearch);

        Assert.Equal(before, await _factory.MasterProductStateAsync());
        Assert.Equal(mappingsBefore, await _factory.MappingCountAsync());
    }

    #endregion

    private static string Url(string search, int? page = null, int? pageSize = null)
    {
        var url = $"/api/products/master-product-suggestions?q={Uri.EscapeDataString(search)}";

        if (page is not null)
        {
            url += $"&page={page.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        if (pageSize is not null)
        {
            url += $"&pageSize={pageSize.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        return url;
    }

    private static MasterProductSizeUnit UnitOf(string code) =>
        Enum.GetValues<MasterProductSizeUnit>()
            .Single(unit => string.Equals(
                MasterProductPolicy.SizeUnitCode(unit),
                code,
                StringComparison.Ordinal));

    private static List<JsonElement> Products(JsonElement page) =>
        page.GetProperty("products").EnumerateArray().ToList();

    private static async Task<string> MessageAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return problem.RootElement.GetProperty("message").GetString()!;
    }

    private static async Task<string> RawAsync(HttpClient client, string search, int? pageSize = null)
    {
        var response = await client.GetAsync(Url(search, pageSize: pageSize));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<JsonElement> SuggestionsAsync(
        HttpClient client,
        string search,
        int? page = null,
        int? pageSize = null)
    {
        var response = await client.GetAsync(Url(search, page, pageSize));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.Clone();
    }

    /// <summary>
    /// The E2E host on one disposable in-memory SQLite database, with this fixture's own master
    /// products in the shared catalogue.
    ///
    /// <para>Both synthetic businesses and the actor with no membership are used, because what is
    /// under test is a read of a <em>platform</em> table from a <em>business</em> endpoint: the two
    /// resolved businesses must get the same answer, and the unresolved caller must get none.</para>
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        /// <summary>
        /// The word every master product this fixture seeds carries, so a search is this fixture's own
        /// whatever else the shared E2E seed holds.
        /// </summary>
        public const string Marker = "Zedco";

        public const string CornChipsBrand = Marker;
        public const string CornChipsName = "Tasty Cheese Corn Chips";

        /// <summary>A search that matches exactly one of them, for the field-set assertion.</summary>
        public const string CornChipsSearch = "Tasty Cheese";

        /// <summary>Business B's own product, whose name must appear in no answer.</summary>
        public const string BusinessBProductName = "E2E Suggestion Isolation Cola";

        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        private readonly SemaphoreSlim _seeding = new(1, 1);

        private bool _seeded;

        /// <summary>
        /// How many isolation products the tests have created, which is what keeps each one's name its
        /// own: a product name is unique per business.
        /// </summary>
        private int _isolationProducts;

        public ApiFactory()
        {
            // Hosting must not depend on the process's current directory; see E2ETestHostContentRoot.
            E2ETestHostContentRoot.Pin();
            _connection.Open();
        }

        public Task<HttpClient> BusinessAAsync() => AsAsync(E2ETestActors.BusinessAOwner.Key);

        public Task<HttpClient> BusinessBAsync() => AsAsync(E2ETestActors.BusinessBOwner.Key);

        /// <summary>A signed-in caller whose business does not resolve.</summary>
        public Task<HttpClient> WithoutMembershipAsync() => AsAsync(E2ETestActors.NoMembership.Key);

        /// <summary>
        /// The ids the marker search must answer, in the order the issue states: brand, then name,
        /// then size, then the row's own key.
        ///
        /// <para>Written out here as five descriptions rather than re-derived with the adapter's own
        /// <c>ORDER BY</c>, so this is the order a person reads off the issue and not a restatement of
        /// the query under test. The two <c>Classic</c> rows differ only in size, and 1.25 l sorts
        /// before 375 ml because the value is what is compared.</para>
        /// </summary>
        public Task<long[]> ExpectedMarkerOrderAsync() =>
            IdsOfAsync(
                ("Bundaberg", $"{Marker} Diet Cola", 375m),
                (CornChipsBrand, CornChipsName, 45m),
                ($"{Marker} Cola", "Classic", 1.25m),
                ($"{Marker} Cola", "Classic", 375m),
                ($"{Marker} Cola", "No Sugar", 375m));

        public async Task<long> CornChipsIdAsync() =>
            (await IdsOfAsync((CornChipsBrand, CornChipsName, 45m)))[0];

        /// <summary>The master product this fixture seeds that the marker search must not match.</summary>
        public async Task<long> UnmatchedIdAsync() =>
            (await IdsOfAsync(("Other Brand", UnmatchedName, 375m)))[0];

        /// <summary>
        /// Every seeded master product's identity and its stored instants, which is what proves a
        /// lookup changed nothing: an updated instant would be a write this read is not allowed to
        /// make.
        /// </summary>
        public async Task<string> MasterProductStateAsync()
        {
            await SeedAsync();
            await using var db = Database();

            var rows = await db.MasterProducts
                .AsNoTracking()
                .OrderBy(master => master.Id)
                .Select(master => new
                {
                    master.Id,
                    master.Brand,
                    master.Name,
                    master.SizeValue,
                    master.SizeUnit,
                    master.NormalisedKey,
                    master.CreatedAtUtc,
                    master.UpdatedAtUtc,
                })
                .ToListAsync();

            return string.Join(
                "|",
                rows.Select(row => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{row.Id}/{row.Brand}/{row.Name}/{row.SizeValue}/{row.SizeUnit}/{row.NormalisedKey}"
                        + $"/{row.CreatedAtUtc:O}/{row.UpdatedAtUtc:O}")));
        }

        /// <summary>How many price-tracking mappings exist at all, across both businesses.</summary>
        public async Task<int> MappingCountAsync()
        {
            await SeedAsync();
            await using var db = Database();

            return await db.ProductPriceTrackings.CountAsync();
        }

        /// <summary>
        /// Business B's own product, created and tracked through the endpoints a business actually
        /// uses, and matched to one of this fixture's master products from outside the boundary -
        /// because no endpoint a business can reach decides a match (#941 and #928 do).
        /// </summary>
        public async Task<long> TrackedProductOfBusinessBAsync()
        {
            await SeedAsync();

            var client = await BusinessBAsync();
            var response = await client.PostAsJsonAsync(
                "/api/products",
                new
                {
                    name = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{BusinessBProductName} {Interlocked.Increment(ref _isolationProducts)}"),
                    sku = (string?)null,
                    description = (string?)null,
                    quantityInStock = 0,
                    lowStockThreshold = 0,
                    restockTo = 0,
                    unit = "375mL",
                    categoryId = (long?)null,
                    isActive = true,
                    initialUnitCost = (decimal?)null,
                });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var productId = created.RootElement.GetProperty("id").GetInt64();

            Assert.Equal(
                HttpStatusCode.NoContent,
                (await client.PutAsJsonAsync(
                    $"/api/products/{productId.ToString(CultureInfo.InvariantCulture)}/price-tracking",
                    new { tracked = true })).StatusCode);

            await using var db = Database();
            var mapping = await db.ProductPriceTrackings
                .SingleAsync(tracking => tracking.ProductId == productId);
            mapping.MasterProductId = await CornChipsIdAsync();
            mapping.Status = ProductPriceTrackingStatus.Matched;
            mapping.MatchSource = ProductPriceTrackingMatchSource.SuperAdmin;
            await db.SaveChangesAsync();

            return productId;
        }

        /// <summary>
        /// Removes that mapping and retires the product, so the fixture is left as it was found. It is
        /// arrangement teardown rather than the feature under test.
        /// </summary>
        public async Task UntrackAsync(long productId)
        {
            await using var db = Database();

            var mapping = await db.ProductPriceTrackings
                .SingleOrDefaultAsync(tracking => tracking.ProductId == productId);
            if (mapping is not null)
            {
                db.ProductPriceTrackings.Remove(mapping);
                await db.SaveChangesAsync();
            }
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
                _seeding.Dispose();
                _connection.Dispose();
            }
        }

        /// <summary>The one description the marker search must not match.</summary>
        private const string UnmatchedName = "Pepsi Max";

        /// <summary>
        /// This fixture's master products: five the marker matches - four by brand, one by name - and
        /// one it must not. Two of them share a brand and a name and differ only in size, which is why
        /// the order ends in the size and then the row's own key.
        /// </summary>
        private static readonly (string Brand, string Name, decimal Size, MasterProductSizeUnit Unit)[]
            Catalogue =
            [
                (CornChipsBrand, CornChipsName, 45m, MasterProductSizeUnit.Gram),
                ($"{Marker} Cola", "Classic", 375m, MasterProductSizeUnit.Millilitre),
                ($"{Marker} Cola", "Classic", 1.25m, MasterProductSizeUnit.Litre),
                ($"{Marker} Cola", "No Sugar", 375m, MasterProductSizeUnit.Millilitre),
                ("Bundaberg", $"{Marker} Diet Cola", 375m, MasterProductSizeUnit.Millilitre),
                ("Other Brand", UnmatchedName, 375m, MasterProductSizeUnit.Millilitre),
            ];

        private async Task<HttpClient> AsAsync(string actorKey)
        {
            await SeedAsync();

            var client = CreateClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation(E2ETestActors.ActorHeaderName, actorKey);
            return client;
        }

        /// <summary>
        /// The keys of the named descriptions, in the order they are named: a test states the products
        /// it expects and this resolves them, so no assertion depends on database-assigned numbers.
        /// </summary>
        private async Task<long[]> IdsOfAsync(
            params (string Brand, string Name, decimal Size)[] descriptions)
        {
            await SeedAsync();
            await using var db = Database();

            var ids = new long[descriptions.Length];
            for (var index = 0; index < descriptions.Length; index++)
            {
                var (brand, name, size) = descriptions[index];
                ids[index] = await db.MasterProducts
                    .AsNoTracking()
                    .Where(master =>
                        master.Brand == brand && master.Name == name && master.SizeValue == size)
                    .Select(master => master.Id)
                    .SingleAsync();
            }

            return ids;
        }

        /// <summary>
        /// Adds this fixture's master products once. They are platform rows that belong to no business
        /// and no endpoint a business can reach writes one, so they are seeded directly.
        /// </summary>
        private async Task SeedAsync()
        {
            if (_seeded)
            {
                return;
            }

            await _seeding.WaitAsync();
            try
            {
                if (_seeded)
                {
                    return;
                }

                await using var db = Database();

                foreach (var (brand, name, size, unit) in Catalogue)
                {
                    db.MasterProducts.Add(new MasterProduct
                    {
                        Brand = brand,
                        Name = name,
                        SizeValue = size,
                        SizeUnit = unit,
                        NormalisedKey = MasterProductPolicy.NormalisedKey(brand, name, size, unit),
                        CreatedAtUtc = E2ETestFixture.SeededAtUtc,
                        UpdatedAtUtc = E2ETestFixture.SeededAtUtc,
                    });
                }

                await db.SaveChangesAsync();
                _seeded = true;
            }
            finally
            {
                _seeding.Release();
            }
        }

        /// <summary>
        /// An unrestricted context for arranging and verifying from outside the boundary, never over
        /// HTTP. Resolving <c>Services</c> starts the host, which is what creates the schema and seeds
        /// the E2E fixture this then adds to.
        /// </summary>
        private AppDbContext Database()
        {
            _ = Services;
            return TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        }
    }
}
