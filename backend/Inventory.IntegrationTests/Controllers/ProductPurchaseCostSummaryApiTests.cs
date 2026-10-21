using System.Globalization;
using System.Net;
using System.Text.Json;
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
/// <c>GET api/products/purchase-cost-summary</c> over the real HTTP pipeline (issue #788, B of
/// #786): authentication, the required scope, the <c>Dashboard.ViewFinancials</c> capability policy,
/// <c>BusinessScopeMiddleware</c>, the controller, the EF adapter and relational SQLite with the real
/// migration history.
///
/// <para>Both synthetic E2E businesses hold purchase history, so "another business's products are
/// never returned" is proved through the pipeline that resolves the business rather than through a
/// hand-written business id - including when business A <em>names</em> business B's product id in the
/// query, which is the one way a caller could try to reach across the boundary here.</para>
///
/// <para>The decisive assertion is <see cref="The_summary_matches_the_products_own_price_history"/>:
/// the bulk answer is compared field by field against what
/// <c>GET api/products/{id}/price-history</c> answers for the same product over the same fixture, a
/// fixture that deliberately contains a same-date tie and a genuine $0 line. The two endpoints must
/// agree exactly, because they are two presentations of one calculation; if this ever diverges, the
/// product screens and a product's own price history are telling an operator two different things
/// about what they last paid.</para>
///
/// <para>The 403 direction belongs with the agreed role matrix and is asserted there
/// (<c>InventoryApi.Tests.Auth.CapabilityAuthorizationApiTests</c>): the E2E host's two actors are
/// both Owners, so a role without the capability cannot be expressed in this fixture.</para>
/// </summary>
public sealed class ProductPurchaseCostSummaryApiTests
    : IClassFixture<ProductPurchaseCostSummaryApiTests.ApiFactory>
{
    private const string SummaryUri = "/api/products/purchase-cost-summary";

    private readonly ApiFactory _factory;

    public ProductPurchaseCostSummaryApiTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// No ids: every product of the caller's business that has at least one recorded purchase line,
    /// and nothing else. The correction product has no purchase history and is absent rather than
    /// reported with a zero cost, and business B's product is absent because it is not this
    /// business's.
    /// </summary>
    [Fact]
    public async Task Without_ids_every_product_with_purchase_history_is_summarised_and_no_other()
    {
        await _factory.SeedPurchaseHistoryAsync();

        var summaries = await SummariesAsync(_factory.BusinessA());

        long[] expected =
        [
            await _factory.ReorderProductIdAsync(),
            await _factory.WaterProductIdAsync(),
            await _factory.RetiredProductIdAsync(),
        ];
        Assert.Equal(
            expected.Order(),
            summaries.Select(summary => summary.GetProperty("productId").GetInt64()));
    }

    /// <summary>
    /// A retired product's purchase history is still history, so it is included: the facts a tool
    /// reaching into the past needs do not disappear when a product is taken out of use.
    /// </summary>
    [Fact]
    public async Task A_retired_products_purchase_history_is_included()
    {
        await _factory.SeedPurchaseHistoryAsync();
        var retiredProductId = await _factory.RetiredProductIdAsync();

        var summary = Assert.Single(
            await SummariesAsync(_factory.BusinessA(), retiredProductId),
            candidate => candidate.GetProperty("productId").GetInt64() == retiredProductId);

        Assert.Equal(ApiFactory.RetiredProductCost, summary.GetProperty("lastUnitCost").GetDecimal());
        Assert.False(await _factory.IsActiveAsync(retiredProductId));
    }

    /// <summary>
    /// The repeated <c>productIds</c> values narrow the answer to exactly those products, and a
    /// requested product with no purchase history is still absent rather than answered with nulls.
    /// </summary>
    [Fact]
    public async Task The_product_ids_filter_narrows_the_answer_to_the_named_products()
    {
        await _factory.SeedPurchaseHistoryAsync();
        var reorderProductId = await _factory.ReorderProductIdAsync();

        var summaries = await SummariesAsync(
            _factory.BusinessA(), reorderProductId, await _factory.CorrectionProductIdAsync());

        var summary = Assert.Single(summaries);
        Assert.Equal(reorderProductId, summary.GetProperty("productId").GetInt64());
        Assert.Equal(ApiFactory.ReorderProductCost, summary.GetProperty("lastUnitCost").GetDecimal());
    }

    /// <summary>
    /// The business boundary, in the one direction this endpoint could be pushed across: business A
    /// names business B's product id explicitly. The tenant query filters simply do not see that
    /// row for A, so the id matches nothing - A is answered an empty array, not B's costs and not an
    /// error that would confirm the id exists somewhere.
    /// </summary>
    [Fact]
    public async Task Another_businesss_product_is_not_returned_even_when_its_id_is_named()
    {
        await _factory.SeedPurchaseHistoryAsync();
        var otherProductId = await _factory.OtherBusinessProductIdAsync();

        var named = await SummariesAsync(_factory.BusinessA(), otherProductId);
        var unfiltered = await SummariesAsync(_factory.BusinessA());

        Assert.Empty(named);
        Assert.DoesNotContain(
            otherProductId, unfiltered.Select(summary => summary.GetProperty("productId").GetInt64()));
    }

    /// <summary>
    /// The other direction, which is what makes the test above an isolation claim rather than an
    /// endpoint that is broken for everybody: business B reaches its own product's costs by exactly
    /// the id business A was answered nothing for, and sees none of A's products.
    /// </summary>
    [Fact]
    public async Task The_other_business_reaches_its_own_products_costs_by_the_same_id()
    {
        await _factory.SeedPurchaseHistoryAsync();
        var otherProductId = await _factory.OtherBusinessProductIdAsync();

        var summaries = await SummariesAsync(_factory.BusinessB());

        var summary = Assert.Single(summaries);
        Assert.Equal(otherProductId, summary.GetProperty("productId").GetInt64());
        Assert.Equal(ApiFactory.OtherBusinessCost, summary.GetProperty("lastUnitCost").GetDecimal());
        Assert.Equal(E2ETestFixture.SupplierBName, summary.GetProperty("lastSupplierName").GetString());
    }

    /// <summary>
    /// The equality the issue turns on, over a fixture built to make it fail if anything here
    /// selected rows for itself: four lines for one product, two of them on the same latest date so
    /// the latest tie-break decides, and one of them a genuine $0 so the cheapest is a zero.
    /// </summary>
    [Fact]
    public async Task The_summary_matches_the_products_own_price_history()
    {
        await _factory.SeedPurchaseHistoryAsync();
        var waterProductId = await _factory.WaterProductIdAsync();
        var client = _factory.BusinessA();

        var summary = Assert.Single(await SummariesAsync(client, waterProductId));
        var comparison = await PriceHistoryAsync(client, waterProductId);

        var latest = comparison.GetProperty("latest");
        var lowest = comparison.GetProperty("lowest");
        Assert.Equal(latest.GetProperty("unitCost").GetDecimal(), summary.GetProperty("lastUnitCost").GetDecimal());
        Assert.Equal(
            latest.GetProperty("supplierName").GetString(), summary.GetProperty("lastSupplierName").GetString());
        Assert.Equal(
            latest.GetProperty("purchaseDate").GetDateTime(), summary.GetProperty("lastPurchaseDate").GetDateTime());
        Assert.Equal(lowest.GetProperty("unitCost").GetDecimal(), summary.GetProperty("lowestUnitCost").GetDecimal());
        Assert.Equal(
            lowest.GetProperty("supplierName").GetString(), summary.GetProperty("lowestSupplierName").GetString());
        Assert.Equal(
            lowest.GetProperty("purchaseDate").GetDateTime(), summary.GetProperty("lowestPurchaseDate").GetDateTime());
    }

    /// <summary>
    /// The same fixture read as absolute values, so the test above cannot pass by both endpoints
    /// being wrong in the same way. The latest cost is the same-date line with the higher purchase
    /// item id, and the cheapest is the genuine $0 line - reported as <c>0</c>, with the null
    /// supplier of the purchase it came from, and neither filtered out nor rounded away.
    /// </summary>
    [Fact]
    public async Task A_genuine_zero_cost_is_the_cheapest_cost_and_the_latest_tie_breaks_to_the_newer_line()
    {
        await _factory.SeedPurchaseHistoryAsync();

        var summary = Assert.Single(
            await SummariesAsync(_factory.BusinessA(), await _factory.WaterProductIdAsync()));

        Assert.Equal(ApiFactory.WaterLatestCost, summary.GetProperty("lastUnitCost").GetDecimal());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("lastSupplierName").ValueKind);
        Assert.Equal(ApiFactory.WaterLatestDate, summary.GetProperty("lastPurchaseDate").GetDateTime());
        Assert.Equal(0m, summary.GetProperty("lowestUnitCost").GetDecimal());
        Assert.Equal(E2ETestFixture.SupplierAName, summary.GetProperty("lowestSupplierName").GetString());
        Assert.Equal(ApiFactory.WaterZeroCostDate, summary.GetProperty("lowestPurchaseDate").GetDateTime());
    }

    /// <summary>
    /// The published keys, in the order the contract carries them. A client reads these names, so a
    /// rename or a reordering is a contract change rather than a refactor.
    /// </summary>
    [Fact]
    public async Task The_summary_publishes_exactly_the_agreed_keys()
    {
        await _factory.SeedPurchaseHistoryAsync();

        var summary = Assert.Single(
            await SummariesAsync(_factory.BusinessA(), await _factory.ReorderProductIdAsync()));

        Assert.Equal(
            [
                "productId", "lastUnitCost", "lastSupplierName", "lastPurchaseDate",
                "lowestUnitCost", "lowestSupplierName", "lowestPurchaseDate",
            ],
            summary.EnumerateObject().Select(property => property.Name));
    }

    private static async Task<IReadOnlyList<JsonElement>> SummariesAsync(
        HttpClient client, params long[] productIds)
    {
        var query = string.Join(
            "&", productIds.Select(id => $"productIds={id.ToString(CultureInfo.InvariantCulture)}"));
        var response = await client.GetAsync(query.Length == 0 ? SummaryUri : $"{SummaryUri}?{query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.EnumerateArray().Select(element => element.Clone()).ToList();
    }

    private static async Task<JsonElement> PriceHistoryAsync(HttpClient client, long productId)
    {
        var response = await client.GetAsync(
            $"/api/products/{productId.ToString(CultureInfo.InvariantCulture)}/price-history");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The API hosted the way the end-to-end harness hosts it - the dedicated E2E environment, its
    /// two synthetic businesses with their own catalogues and suppliers, and a disposable relational
    /// database - with purchase history added for both of them.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        /// <summary>The water product's most recent cost: the later of two lines dated the same day.</summary>
        public const decimal WaterLatestCost = 1.90m;

        /// <summary>The other same-day line, deliberately dearer and recorded first.</summary>
        public const decimal WaterSameDayEarlierCost = 2.40m;

        public const decimal ReorderProductCost = 1.10m;
        public const decimal RetiredProductCost = 3.30m;
        public const decimal OtherBusinessCost = 9.99m;

        public static readonly DateTime WaterLatestDate = new(2026, 3, 5, 0, 0, 0, DateTimeKind.Unspecified);
        public static readonly DateTime WaterZeroCostDate = new(2026, 2, 5, 0, 0, 0, DateTimeKind.Unspecified);

        /// <summary>A retired product, created here rather than by the shared E2E fixture.</summary>
        public const string RetiredProductName = "E2E Retired Purchase Item";

        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        public ApiFactory()
        {
            // Hosting must not depend on the process's current directory; see E2ETestHostContentRoot.
            E2ETestHostContentRoot.Pin();
            _connection.Open();
        }

        public HttpClient BusinessA() => As(E2ETestActors.BusinessAOwner.Key);

        public HttpClient BusinessB() => As(E2ETestActors.BusinessBOwner.Key);

        public Task<long> WaterProductIdAsync() => ProductIdAsync(E2ETestFixture.PurchaseProductName);

        public Task<long> ReorderProductIdAsync() => ProductIdAsync(E2ETestFixture.ReorderProductName);

        public Task<long> CorrectionProductIdAsync() => ProductIdAsync(E2ETestFixture.CorrectionProductName);

        public Task<long> RetiredProductIdAsync() => ProductIdAsync(RetiredProductName);

        public Task<long> OtherBusinessProductIdAsync() => ProductIdAsync(E2ETestFixture.BusinessBProductName);

        public async Task<bool> IsActiveAsync(long productId)
        {
            await using var db = Database();
            return (await db.Products.AsNoTracking().SingleAsync(product => product.Id == productId)).IsActive;
        }

        /// <summary>
        /// The purchase history both businesses are read through, written once and idempotently.
        ///
        /// <para>Business A's water product gets the interesting shape: an ordinary line, a genuine
        /// $0 line from a recorded supplier, and two lines on one date whose costs differ, so the
        /// latest-cost tie-break actually decides the answer. Its reorder product gets one plain
        /// line, a retired product gets one too, and the correction product deliberately gets
        /// none.</para>
        ///
        /// <para>No stock movement and no costing history is recorded: this is a purchase-history
        /// fixture, and a read-only summary must not need either.</para>
        /// </summary>
        public async Task SeedPurchaseHistoryAsync()
        {
            await using var db = Database();
            if (await db.Receipts.AnyAsync())
            {
                return;
            }

            var businessA = await db.Products.AsNoTracking()
                .SingleAsync(product => product.Name == E2ETestFixture.PurchaseProductName);
            var reorderProduct = await db.Products.AsNoTracking()
                .SingleAsync(product => product.Name == E2ETestFixture.ReorderProductName);
            var supplierA = await db.Suppliers.AsNoTracking()
                .SingleAsync(supplier => supplier.Name == E2ETestFixture.SupplierAName);

            var retiredProduct = new Product
            {
                BusinessId = businessA.BusinessId,
                Name = RetiredProductName,
                Sku = "E2E-RETIRED",
                QuantityInStock = 0,
                LowStockThreshold = 0,
                RestockTo = 0,
                IsActive = false,
                AverageUnitCost = 0m,
                CreatedAt = E2ETestFixture.SeededAtUtc,
                UpdatedAt = E2ETestFixture.SeededAtUtc,
            };
            db.Products.Add(retiredProduct);
            await db.SaveChangesAsync();

            // One purchase per line, so each line's own date and supplier is the one the comparison
            // reads. The two same-day water lines are in one purchase and recorded dearest first, so
            // the higher purchase item id - the tie-break - is the cheaper of the two.
            db.Receipts.AddRange(
                PurchaseOf(businessA.BusinessId, "January water", new DateTime(2026, 1, 5), supplierA.Id,
                    (businessA.Id, 2.00m)),
                PurchaseOf(businessA.BusinessId, "February free case", WaterZeroCostDate, supplierA.Id,
                    (businessA.Id, 0m)),
                PurchaseOf(businessA.BusinessId, "March water, two lines", WaterLatestDate, supplierId: null,
                    (businessA.Id, WaterSameDayEarlierCost), (businessA.Id, WaterLatestCost)),
                PurchaseOf(businessA.BusinessId, "January chips", new DateTime(2026, 1, 10), supplierA.Id,
                    (reorderProduct.Id, ReorderProductCost)),
                PurchaseOf(businessA.BusinessId, "January retired item", new DateTime(2026, 1, 12), supplierA.Id,
                    (retiredProduct.Id, RetiredProductCost)));
            await db.SaveChangesAsync();

            var businessBProduct = await db.Products.AsNoTracking()
                .SingleAsync(product => product.Name == E2ETestFixture.BusinessBProductName);
            var supplierB = await db.Suppliers.AsNoTracking()
                .SingleAsync(supplier => supplier.Name == E2ETestFixture.SupplierBName);
            db.Receipts.Add(PurchaseOf(
                businessBProduct.BusinessId, "January chocolate", new DateTime(2026, 1, 20), supplierB.Id,
                (businessBProduct.Id, OtherBusinessCost)));
            await db.SaveChangesAsync();
        }

        private static Purchase PurchaseOf(
            int businessId,
            string title,
            DateTime purchaseDate,
            int? supplierId,
            params (long ProductId, decimal UnitCost)[] lines) =>
            new()
            {
                BusinessId = businessId,
                Title = title,
                SupplierId = supplierId,
                TotalAmount = lines.Sum(line => line.UnitCost),
                PurchaseDate = purchaseDate,
                FileName = "scan.jpg",
                StoredFileName = $"{title}.jpg",
                ContentType = "image/jpeg",
                FileSizeBytes = 1024,
                CreatedAt = E2ETestFixture.SeededAtUtc,
                Items = lines
                    .Select(line => new PurchaseItem
                    {
                        BusinessId = businessId,
                        ProductId = line.ProductId,
                        Quantity = 1m,
                        UnitCost = line.UnitCost,
                    })
                    .ToList(),
            };

        private async Task<long> ProductIdAsync(string productName)
        {
            await using var db = Database();
            return (await db.Products.AsNoTracking().SingleAsync(product => product.Name == productName)).Id;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(E2ETestEnvironment.EnvironmentName);

            // The E2E environment is not one of the environments that migrate automatically, so
            // this disposable database opts in with the setting that exists for exactly that case.
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

        private HttpClient As(string actorKey)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation(E2ETestActors.ActorHeaderName, actorKey);
            return client;
        }

        /// <summary>
        /// An unrestricted context for arranging and verifying both businesses' rows from outside
        /// the boundary, never over HTTP. Resolving <c>Services</c> starts the host, which is what
        /// creates the schema and seeds the fixture this then reads.
        /// </summary>
        private AppDbContext Database()
        {
            _ = Services;
            return TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        }
    }
}
