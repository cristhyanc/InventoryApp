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
/// Creating a product linked to a selected master product over the real HTTP pipeline (issue #1051,
/// parent #817): authentication, the <c>Inventory.Operate</c> capability, <c>BusinessScopeMiddleware</c>,
/// the controller, the create use case, the EF adapter and relational SQLite with the real migration
/// history.
///
/// <para><strong>What a refusal must <em>not</em> leave behind is as much under test as what a link
/// writes.</strong> Every conflict asserts that no product and no mapping exist afterwards, because
/// the operator asked for a linked product and a half-created one would be worse than none - and
/// every successful link asserts the stored row, since <c>BusinessSelection</c> and the captured
/// search text are what the collector and the Super Admin will read.</para>
///
/// <para>The master products are seeded directly: they are platform rows and no endpoint a business
/// can reach writes one. Each one carries this fixture's own marker word, and each test uses its own
/// master product, so the created products are distinguishable whatever else the shared E2E seed
/// holds.</para>
/// </summary>
public sealed class ProductMasterProductLinkApiTests
    : IClassFixture<ProductMasterProductLinkApiTests.ApiFactory>
{
    private readonly ApiFactory _factory;

    public ProductMasterProductLinkApiTests(ApiFactory factory) => _factory = factory;

    #region A valid selection

    /// <summary>
    /// The seventh and tenth criteria over HTTP: the product is created and one matched mapping is
    /// stored for it with the new <c>BusinessSelection</c> source and the search text the tracking
    /// switch itself would have captured - and both survive a reload, through the product read and
    /// the price-tracking reads that already existed.
    /// </summary>
    [Fact]
    public async Task A_selection_creates_the_product_and_links_it()
    {
        var client = await _factory.BusinessAAsync();
        var prefill = ApiFactory.Prefill(ApiFactory.Linked);
        var masterProductId = await _factory.MasterProductIdAsync(ApiFactory.Linked);

        var productId = await CreatedProductIdAsync(
            await PostAsync(client, prefill, masterProductId));

        // The stored row: the status, the master product, the source and the captured text.
        var mapping = await _factory.MappingAsync(productId);
        Assert.Equal(ProductPriceTrackingStatus.Matched, mapping.Status);
        Assert.Equal(masterProductId, mapping.MasterProductId);
        Assert.Equal(ProductPriceTrackingMatchSource.BusinessSelection, mapping.MatchSource);
        Assert.Equal(prefill.ProductName, mapping.SearchName);
        Assert.Equal(prefill.UnitLabel, mapping.UnitLabel);
        Assert.Equal(
            PriceCatalogueWaitingKey.For(prefill.ProductName, prefill.UnitLabel),
            mapping.WaitingKey);

        // And what a reload shows, through the reads that existed before this issue.
        using var product = await ReadAsync(client, $"/api/products/{Id(productId)}");
        Assert.Equal(prefill.ProductName, product.RootElement.GetProperty("name").GetString());

        using var state = await ReadAsync(client, $"/api/products/{Id(productId)}/price-tracking");
        Assert.True(state.RootElement.GetProperty("tracked").GetBoolean());
        Assert.Equal(
            (int)ProductPriceTrackingStatus.Matched,
            state.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            prefill.ProductName,
            state.RootElement.GetProperty("searchName").GetString());
        Assert.Equal(
            ApiFactory.Linked,
            state.RootElement.GetProperty("masterProduct").GetProperty("name").GetString());

        using var tracked = await ReadAsync(client, "/api/products/price-tracking");
        var entry = Assert.Single(
            tracked.RootElement.EnumerateArray()
                .Where(row => row.GetProperty("productId").GetInt64() == productId));
        Assert.Equal((int)ProductPriceTrackingStatus.Matched, entry.GetProperty("status").GetInt32());
    }

    /// <summary>
    /// The second criterion's normalisation over HTTP: the operator's own spacing and case still
    /// submit the catalogue's description, so the link is made and the mapping records the product's
    /// own text with the key that text folds to.
    /// </summary>
    [Fact]
    public async Task Whitespace_and_case_still_submit_the_same_product()
    {
        var client = await _factory.BusinessAAsync();
        var prefill = ApiFactory.Prefill(ApiFactory.Spelled);
        var masterProductId = await _factory.MasterProductIdAsync(ApiFactory.Spelled);

        var productId = await CreatedProductIdAsync(await PostAsync(
            client,
            $"  {prefill.ProductName.ToUpperInvariant()}  ",
            $" {prefill.UnitLabel.ToUpperInvariant()} ",
            masterProductId));

        var mapping = await _factory.MappingAsync(productId);
        Assert.Equal(masterProductId, mapping.MasterProductId);
        Assert.Equal(ProductPriceTrackingMatchSource.BusinessSelection, mapping.MatchSource);
        Assert.Equal(
            PriceCatalogueWaitingKey.For(prefill.ProductName, prefill.UnitLabel),
            mapping.WaitingKey);
    }

    #endregion

    #region No selection, and the opt-out

    /// <summary>
    /// The first criterion: a client that sends neither of the two new values gets the create it
    /// always got - a product, untracked, with no mapping at all.
    /// </summary>
    [Fact]
    public async Task A_create_that_sends_neither_value_behaves_as_it_always_did()
    {
        var client = await _factory.BusinessAAsync();

        var response = await client.PostAsJsonAsync(
            "/api/products",
            new
            {
                name = "E2E 1051 Unlinked Product",
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

        var productId = await CreatedProductIdAsync(response);

        Assert.Null(await _factory.FindMappingAsync(productId));

        using var state = await ReadAsync(client, $"/api/products/{Id(productId)}/price-tracking");
        Assert.False(state.RootElement.GetProperty("tracked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, state.RootElement.GetProperty("status").ValueKind);
    }

    /// <summary>
    /// The third criterion: the operator's opt-out ignores the selection, because the link is the
    /// tracking row. The product is created unlinked and untracked, and no conflict is reported.
    /// </summary>
    [Fact]
    public async Task Turning_tracking_off_ignores_the_selection()
    {
        var client = await _factory.BusinessAAsync();
        var prefill = ApiFactory.Prefill(ApiFactory.OptedOut);

        var productId = await CreatedProductIdAsync(await PostAsync(
            client,
            prefill.ProductName,
            prefill.UnitLabel,
            await _factory.MasterProductIdAsync(ApiFactory.OptedOut),
            trackSupplierPrices: false));

        Assert.Null(await _factory.FindMappingAsync(productId));

        using var state = await ReadAsync(client, $"/api/products/{Id(productId)}/price-tracking");
        Assert.False(state.RootElement.GetProperty("tracked").GetBoolean());
    }

    #endregion

    #region The three conflicts

    /// <summary>
    /// The fourth criterion: a selection naming no master product is <c>409</c> with
    /// <c>master_product_unavailable</c>, and nothing is created.
    /// </summary>
    [Fact]
    public async Task An_unknown_master_product_is_refused_and_creates_nothing()
    {
        var client = await _factory.BusinessAAsync();

        // A Name no other test of this class creates, and an id nothing in the catalogue has: what is
        // under test is that a tampered or stale selection creates no product at all.
        await AssertConflictAsync(
            MasterProductSelectionPolicy.UnavailableCode,
            await PostAsync(client, "E2E 1051 Unavailable Cola", "375ml", 987654L),
            "E2E 1051 Unavailable Cola");
    }

    /// <summary>
    /// The same answer for a master product removed from the shared catalogue after the operator
    /// looked it up, which is what makes the read inside the create authoritative.
    /// </summary>
    [Fact]
    public async Task A_master_product_deleted_after_the_lookup_is_refused()
    {
        var client = await _factory.BusinessAAsync();
        var prefill = ApiFactory.Prefill(ApiFactory.Doomed);
        var masterProductId = await _factory.MasterProductIdAsync(ApiFactory.Doomed);
        await _factory.DeleteMasterProductAsync(masterProductId);

        await AssertConflictAsync(
            MasterProductSelectionPolicy.UnavailableCode,
            await PostAsync(client, prefill, masterProductId),
            prefill.ProductName);
    }

    /// <summary>
    /// The fifth criterion: an edited Name and an edited unit label are both
    /// <c>master_product_changed</c>, and neither creates anything - the frontend's "Choose again" or
    /// "Create without link" answer is what follows, and both are the client's to make.
    /// </summary>
    [Theory]
    [InlineData(" Twin Pack", null)]
    [InlineData(null, "1.25l")]
    [InlineData(null, "375 ml")]
    public async Task An_edited_name_or_unit_label_is_refused(string? nameSuffix, string? unit)
    {
        var client = await _factory.BusinessAAsync();
        var prefill = ApiFactory.Prefill(ApiFactory.Edited);
        var name = prefill.ProductName + nameSuffix;

        await AssertConflictAsync(
            MasterProductSelectionPolicy.ChangedCode,
            await PostAsync(
                client,
                name,
                unit ?? prefill.UnitLabel,
                await _factory.MasterProductIdAsync(ApiFactory.Edited)),
            name);
    }

    /// <summary>
    /// The master product corrected between the lookup and the create: the submitted values were its
    /// prefill as it <em>was</em>, so the create is refused rather than linked to a description
    /// nobody is looking at.
    /// </summary>
    [Fact]
    public async Task A_master_product_edited_after_the_lookup_is_refused()
    {
        var client = await _factory.BusinessAAsync();
        var prefill = ApiFactory.Prefill(ApiFactory.Corrected);
        var masterProductId = await _factory.MasterProductIdAsync(ApiFactory.Corrected);
        await _factory.RenameMasterProductAsync(masterProductId, $"{ApiFactory.Corrected} No Sugar");

        await AssertConflictAsync(
            MasterProductSelectionPolicy.ChangedCode,
            await PostAsync(client, prefill, masterProductId),
            prefill.ProductName);
    }

    /// <summary>
    /// The sixth criterion: a pair a Super Admin refused or undid is never matched again, and
    /// picking the product by hand on a create is not a way around it.
    /// </summary>
    [Theory]
    [InlineData(ProductMatchSuggestionStatus.Rejected)]
    [InlineData(ProductMatchSuggestionStatus.Undone)]
    public async Task A_refused_pair_is_refused(ProductMatchSuggestionStatus status)
    {
        var client = await _factory.BusinessAAsync();
        var master = status == ProductMatchSuggestionStatus.Rejected
            ? ApiFactory.Rejected
            : ApiFactory.Undone;
        var prefill = ApiFactory.Prefill(master);
        var masterProductId = await _factory.MasterProductIdAsync(master);

        await _factory.SeedRefusalAsync(
            PriceCatalogueWaitingKey.For(prefill.ProductName, prefill.UnitLabel),
            masterProductId,
            status);

        await AssertConflictAsync(
            MasterProductSelectionPolicy.RefusedCode,
            await PostAsync(client, prefill, masterProductId),
            prefill.ProductName);
    }

    #endregion

    #region Who may do it, and whose row it is

    /// <summary>
    /// A signed-in caller whose business does not resolve creates nothing, linked or not: the same
    /// <c>403</c> the create has always given them, decided before the endpoint.
    /// </summary>
    [Fact]
    public async Task An_unresolved_business_may_not_create_a_linked_product()
    {
        var client = await _factory.WithoutMembershipAsync();
        var prefill = ApiFactory.Prefill(ApiFactory.Linked);
        var mappingsBefore = await _factory.MappingCountAsync();

        var response = await PostAsync(
            client,
            prefill,
            await _factory.MasterProductIdAsync(ApiFactory.Linked));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(mappingsBefore, await _factory.MappingCountAsync());
    }

    /// <summary>
    /// Two businesses may each link their own new product to one master product - which is what a
    /// shared catalogue is for - and each mapping is stamped with its own business. Neither business
    /// learns anything about the other: the other's product is not in its tracked list.
    /// </summary>
    [Fact]
    public async Task Each_business_links_its_own_product_and_sees_only_that()
    {
        var masterProductId = await _factory.MasterProductIdAsync(ApiFactory.Shared);
        var prefill = ApiFactory.Prefill(ApiFactory.Shared);

        var businessA = await _factory.BusinessAAsync();
        var businessB = await _factory.BusinessBAsync();

        var theirs = await CreatedProductIdAsync(await PostAsync(businessB, prefill, masterProductId));
        var ours = await CreatedProductIdAsync(await PostAsync(businessA, prefill, masterProductId));

        var theirMapping = await _factory.MappingAsync(theirs);
        var ourMapping = await _factory.MappingAsync(ours);
        Assert.Equal(masterProductId, theirMapping.MasterProductId);
        Assert.Equal(masterProductId, ourMapping.MasterProductId);
        Assert.NotEqual(theirMapping.BusinessId, ourMapping.BusinessId);

        using var tracked = await ReadAsync(businessA, "/api/products/price-tracking");
        var productIds = tracked.RootElement.EnumerateArray()
            .Select(row => row.GetProperty("productId").GetInt64())
            .ToArray();

        Assert.Contains(ours, productIds);
        Assert.DoesNotContain(theirs, productIds);

        // And the other business's product is not readable either, which is the ordinary tenant
        // answer rather than anything this issue added.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await businessA.GetAsync($"/api/products/{Id(theirs)}/price-tracking")).StatusCode);
    }

    #endregion

    private static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        MasterProductPrefillText prefill,
        long? selectedMasterProductId,
        bool? trackSupplierPrices = null) =>
        PostAsync(
            client,
            prefill.ProductName,
            prefill.UnitLabel,
            selectedMasterProductId,
            trackSupplierPrices);

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string name,
        string? unit,
        long? selectedMasterProductId,
        bool? trackSupplierPrices = null) =>
        client.PostAsJsonAsync(
            "/api/products",
            new
            {
                name,
                sku = (string?)null,
                description = (string?)null,
                quantityInStock = 0,
                lowStockThreshold = 0,
                restockTo = 0,
                unit,
                categoryId = (long?)null,
                isActive = true,
                initialUnitCost = (decimal?)null,
                selectedMasterProductId,
                trackSupplierPrices,
            });

    private static async Task<long> CreatedProductIdAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return created.RootElement.GetProperty("id").GetInt64();
    }

    private static async Task<JsonDocument> ReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// One conflict, asserted whole: the status, the stable code, a sentence to show, and that the
    /// create wrote nothing - no product of that name, and no mapping for one.
    /// </summary>
    private async Task AssertConflictAsync(
        string code,
        HttpResponseMessage response,
        string submittedName)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        Assert.False(
            string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("message").GetString()));
        Assert.False(
            string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("detail").GetString()));

        // Nothing was created: no product of the submitted name exists in either business. Each
        // conflict case submits a Name no accepted create of this class uses, so the count is the
        // whole statement.
        Assert.Equal(0, await _factory.ProductsNamedAsync(submittedName));
    }

    /// <summary>
    /// The real API host over one in-memory SQLite database with the real migration history, with
    /// this fixture's own master products seeded into the shared catalogue.
    ///
    /// <para>Both synthetic E2E businesses and the actor with no membership are used, because a link
    /// is a tenant-owned row written beside a platform read: each business must end with its own
    /// mapping and the unresolved caller with none.</para>
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        /// <summary>The word every master product this fixture seeds carries.</summary>
        public const string Marker = "Zedco1051";

        /// <summary>The master product of the happy path.</summary>
        public const string Linked = "Linked Cola";

        /// <summary>The one whose prefill is submitted back in another spelling.</summary>
        public const string Spelled = "Spelled Cola";

        /// <summary>The one the operator opts out of tracking.</summary>
        public const string OptedOut = "Opted Out Cola";

        /// <summary>The one whose submitted Name or unit label is edited.</summary>
        public const string Edited = "Edited Cola";

        /// <summary>The one corrected in the catalogue after the lookup.</summary>
        public const string Corrected = "Corrected Cola";

        /// <summary>The one deleted from the catalogue after the lookup.</summary>
        public const string Doomed = "Doomed Cola";

        /// <summary>The one whose pair a Super Admin refused.</summary>
        public const string Rejected = "Rejected Cola";

        /// <summary>The one whose pair a Super Admin undid.</summary>
        public const string Undone = "Undone Cola";

        /// <summary>The one both businesses link their own product to.</summary>
        public const string Shared = "Shared Cola";

        /// <summary>Every master product this fixture seeds, one per test that needs its own.</summary>
        private static readonly string[] Catalogue =
        [
            Linked,
            Spelled,
            OptedOut,
            Edited,
            Corrected,
            Doomed,
            Rejected,
            Undone,
            Shared,
        ];

        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        private readonly SemaphoreSlim _seeding = new(1, 1);

        private bool _seeded;

        public ApiFactory()
        {
            // Hosting must not depend on the process's current directory; see E2ETestHostContentRoot.
            E2ETestHostContentRoot.Pin();
            _connection.Open();
        }

        /// <summary>
        /// What #1049's formatter prefills a product form with for one of this fixture's master
        /// products. The tests submit exactly this, which is the whole point of the comparison.
        /// </summary>
        public static MasterProductPrefillText Prefill(string name) =>
            MasterProductPrefill.For(Marker, name, 375m, MasterProductSizeUnit.Millilitre);

        public Task<HttpClient> BusinessAAsync() => AsAsync(E2ETestActors.BusinessAOwner.Key);

        public Task<HttpClient> BusinessBAsync() => AsAsync(E2ETestActors.BusinessBOwner.Key);

        /// <summary>A signed-in caller whose business does not resolve.</summary>
        public Task<HttpClient> WithoutMembershipAsync() => AsAsync(E2ETestActors.NoMembership.Key);

        /// <summary>The key of one of this fixture's seeded master products.</summary>
        public async Task<long> MasterProductIdAsync(string name)
        {
            await SeedAsync();
            await using var db = Database();

            return await db.MasterProducts
                .AsNoTracking()
                .Where(master => master.Brand == Marker && master.Name == name)
                .Select(master => master.Id)
                .SingleAsync();
        }

        /// <summary>The mapping stored for one product, which a linked create must have written.</summary>
        public async Task<ProductPriceTracking> MappingAsync(long productId) =>
            await FindMappingAsync(productId)
                ?? throw new InvalidOperationException($"No mapping for product {productId}.");

        /// <summary>The mapping stored for one product, or <c>null</c> when it has none.</summary>
        public async Task<ProductPriceTracking?> FindMappingAsync(long productId)
        {
            await SeedAsync();
            await using var db = Database();

            return await db.ProductPriceTrackings
                .AsNoTracking()
                .SingleOrDefaultAsync(tracking => tracking.ProductId == productId);
        }

        /// <summary>How many mappings exist at all, across both businesses.</summary>
        public async Task<int> MappingCountAsync()
        {
            await SeedAsync();
            await using var db = Database();

            return await db.ProductPriceTrackings.CountAsync();
        }

        /// <summary>How many products of any business carry that name.</summary>
        public async Task<int> ProductsNamedAsync(string name)
        {
            await SeedAsync();
            await using var db = Database();

            return await db.Products.CountAsync(product => product.Name == name);
        }

        /// <summary>
        /// Removes a master product from the shared catalogue, which is how a test arranges "the row
        /// you chose is not there any more". No endpoint a business can reach does it.
        /// </summary>
        public async Task DeleteMasterProductAsync(long masterProductId)
        {
            await SeedAsync();
            await using var db = Database();

            db.MasterProducts.Remove(
                await db.MasterProducts.SingleAsync(master => master.Id == masterProductId));

            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Corrects a master product's name directly, for the "edited between the lookup and the
        /// create" case. The Super Admin edit of #1024 is the real writer and has its own tests.
        /// </summary>
        public async Task RenameMasterProductAsync(long masterProductId, string name)
        {
            await SeedAsync();
            await using var db = Database();

            var master = await db.MasterProducts.SingleAsync(row => row.Id == masterProductId);
            master.Name = name;
            master.NormalisedKey = MasterProductPolicy.NormalisedKey(
                master.Brand,
                name,
                master.SizeValue,
                master.SizeUnit);
            master.UpdatedAtUtc = DateTime.UtcNow;

            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Records a Super Admin's refusal of one <c>(waiting key, master product)</c> pair, which no
        /// endpoint a business can reach writes: the review of #928 does, and it has its own tests.
        /// </summary>
        public async Task SeedRefusalAsync(
            string waitingKey,
            long masterProductId,
            ProductMatchSuggestionStatus status)
        {
            await SeedAsync();
            await using var db = Database();

            db.ProductMatchSuggestions.Add(new ProductMatchSuggestion
            {
                WaitingKey = waitingKey,
                MasterProductId = masterProductId,
                Confidence = 0.9m,
                Reason = "Seeded by the fixture.",
                CollectorRun = "fixture",
                Status = status,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });

            await db.SaveChangesAsync();
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

        private async Task<HttpClient> AsAsync(string actorKey)
        {
            await SeedAsync();

            var client = CreateClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation(E2ETestActors.ActorHeaderName, actorKey);
            return client;
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

                foreach (var name in Catalogue)
                {
                    db.MasterProducts.Add(new MasterProduct
                    {
                        Brand = Marker,
                        Name = name,
                        SizeValue = 375m,
                        SizeUnit = MasterProductSizeUnit.Millilitre,
                        NormalisedKey = MasterProductPolicy.NormalisedKey(
                            Marker,
                            name,
                            375m,
                            MasterProductSizeUnit.Millilitre),
                        CreatedAtUtc = DateTime.UtcNow,
                        UpdatedAtUtc = DateTime.UtcNow,
                    });
                }

                await db.SaveChangesAsync();

                _seeded = true;
            }
            finally
            {
                _ = _seeding.Release();
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
