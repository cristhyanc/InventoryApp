using System.Net;
using System.Text;
using System.Text.Json;
using Inventory.Domain.Gst;
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
/// "Set GST rule in bulk" over the real HTTP pipeline (issue #1076): a genuine JSON body, the
/// framework's own model binding, authentication, the <c>Inventory.Operate</c> capability policy,
/// <c>BusinessScopeMiddleware</c>, the controller, the EF adapter and relational SQLite with the
/// real migration history.
///
/// <para>It is hosted rather than called in process for the two reasons
/// <see cref="ProductAndSupplierGstRuleApiTests"/> is. First, the target rule is bound from JSON,
/// where a C# enum is no constraint at all: <c>{"gstRule": 999}</c> deserializes to a
/// <see cref="GstClassification"/> no policy describes, and so does the <c>0</c> an omitted field
/// binds to - which is "no rule", a declared value that is still not a bulk target. Second, tenant
/// scoping has to be proved through the pipeline that resolves the business: both synthetic E2E
/// businesses are used, so one business's bulk change is shown to leave the other's products
/// exactly as they were without a hand-written business id anywhere.</para>
///
/// <para>The purchase, cost and stock state of both businesses is compared across every request,
/// because the whole claim of this feature is that it is <em>configuration</em>: no purchase line,
/// charge, classification, provenance, unit cost, costing quantity, inventory value or stock
/// quantity may move when a GST rule does (AGENTS.md § Purchase GST classification).</para>
///
/// <para>The capability, support-session and role-preview refusals belong with the agreed matrices
/// and are asserted there - <c>CapabilityAuthorizationApiTests</c> (including the caller whose
/// business does not resolve), <c>SupportSessionRequestApiTests</c> and <c>RolePreviewApiTests</c> -
/// because the E2E host's two actors are both Owners and a role without the capability cannot be
/// expressed in this fixture.</para>
/// </summary>
public sealed class BulkProductGstRuleApiTests
    : IClassFixture<BulkProductGstRuleApiTests.ApiFactory>
{
    private const string PreviewUri = "/api/products/gst-rule/bulk-preview";
    private const string ApplyUri = "/api/products/gst-rule/bulk";

    private readonly ApiFactory _factory;

    public BulkProductGstRuleApiTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task An_unauthenticated_caller_reaches_neither_action()
    {
        var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(PreviewUri, Json("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(ApplyUri, Json("{}"))).StatusCode);
    }

    /// <summary>
    /// The scope the dialog states, proved end to end: every product of this business with no rule,
    /// the inactive one included, and none of the other business's.
    /// </summary>
    [Fact]
    public async Task The_preview_counts_this_businesss_unconfigured_products_including_an_inactive_one()
    {
        await _factory.ResetAsync();
        await _factory.DeactivateAsync(E2ETestFixture.ReorderProductName);

        var businessA = await EligibleCountAsync(_factory.BusinessA());
        var businessB = await EligibleCountAsync(_factory.BusinessB());

        Assert.Equal(await _factory.ProductCountAsync(_factory.BusinessAId), businessA);
        Assert.Equal(await _factory.ProductCountAsync(_factory.BusinessBId), businessB);
        Assert.True(businessA > businessB, "The fixture's first business must hold more products than the second.");
    }

    [Theory]
    [InlineData(1, GstClassification.Taxable)]
    [InlineData(2, GstClassification.GstFree)]
    public async Task The_apply_configures_every_unconfigured_product_and_changes_no_purchase(
        int submitted, GstClassification expected)
    {
        await _factory.ResetAsync();
        await _factory.DeactivateAsync(E2ETestFixture.ReorderProductName);
        var purchasesBefore = await _factory.PurchaseRowsAsync();
        var costsBefore = await _factory.CostAndStockStateAsync();
        var eligible = await EligibleCountAsync(_factory.BusinessA());

        var response = await _factory.BusinessA().PostAsync(
            ApplyUri, Json($$"""{"gstRule":{{submitted}},"expectedEligibleCount":{{eligible}}}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)expected, body.RootElement.GetProperty("gstRule").GetInt32());
        Assert.Equal(eligible, body.RootElement.GetProperty("changedCount").GetInt32());

        // Every one of this business's products now carries the rule - the inactive one too - and
        // nothing else about any of them moved.
        Assert.All(
            await _factory.StoredRulesAsync(_factory.BusinessAId),
            rule => Assert.Equal(expected, rule));
        Assert.Equal(0, await EligibleCountAsync(_factory.BusinessA()));
        Assert.Equal(purchasesBefore, await _factory.PurchaseRowsAsync());
        Assert.Equal(costsBefore, await _factory.CostAndStockStateAsync());
    }

    /// <summary>
    /// The one product that was configured by hand keeps its rule, and it is not even counted:
    /// "explicit rules remain untouched" is the criterion this feature exists around.
    /// </summary>
    [Fact]
    public async Task An_explicitly_configured_product_is_neither_counted_nor_rewritten()
    {
        await _factory.ResetAsync();
        var configured = await _factory.ProductIdAsync(E2ETestFixture.CorrectionProductName);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await _factory.BusinessA().PutAsync(
                $"/api/products/{configured}/gst-rule", Json("""{"gstRule":2}"""))).StatusCode);

        var eligible = await EligibleCountAsync(_factory.BusinessA());
        var response = await _factory.BusinessA().PostAsync(
            ApplyUri, Json($$"""{"gstRule":1,"expectedEligibleCount":{{eligible}}}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(await _factory.ProductCountAsync(_factory.BusinessAId) - 1, eligible);
        Assert.Equal(GstClassification.GstFree, await _factory.StoredRuleAsync(configured));
    }

    [Fact]
    public async Task One_businesss_bulk_change_leaves_the_other_businesss_products_alone()
    {
        await _factory.ResetAsync();
        var eligible = await EligibleCountAsync(_factory.BusinessA());

        var response = await _factory.BusinessA().PostAsync(
            ApplyUri, Json($$"""{"gstRule":1,"expectedEligibleCount":{{eligible}}}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(
            await _factory.StoredRulesAsync(_factory.BusinessBId),
            rule => Assert.Equal(GstRules.None, rule));
        Assert.Equal(
            await _factory.ProductCountAsync(_factory.BusinessBId),
            await EligibleCountAsync(_factory.BusinessB()));
    }

    /// <summary>
    /// A body that tries to name a business is accepted as the rule and count it also carries, and
    /// the extra member changes nothing: the change lands on business B's own products and business
    /// A's are untouched (AGENTS.md § Tenant ownership and data isolation).
    /// </summary>
    [Fact]
    public async Task A_submitted_business_id_is_ignored()
    {
        await _factory.ResetAsync();
        var eligible = await EligibleCountAsync(_factory.BusinessB());

        var response = await _factory.BusinessB().PostAsync(
            ApplyUri,
            Json($$"""
            {"businessId":{{_factory.BusinessAId}},"productIds":[1,2,3],"gstRule":2,
             "expectedEligibleCount":{{eligible}}}
            """));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(
            await _factory.StoredRulesAsync(_factory.BusinessBId),
            rule => Assert.Equal(GstClassification.GstFree, rule));
        Assert.All(
            await _factory.StoredRulesAsync(_factory.BusinessAId),
            rule => Assert.Equal(GstRules.None, rule));
    }

    [Fact]
    public async Task Nothing_eligible_is_answered_as_zero_and_an_apply_over_it_changes_nothing()
    {
        await _factory.ResetAsync();
        var eligible = await EligibleCountAsync(_factory.BusinessA());
        await _factory.BusinessA().PostAsync(
            ApplyUri, Json($$"""{"gstRule":1,"expectedEligibleCount":{{eligible}}}"""));
        var costsBefore = await _factory.CostAndStockStateAsync();

        Assert.Equal(0, await EligibleCountAsync(_factory.BusinessA()));
        var again = await _factory.BusinessA().PostAsync(
            ApplyUri, Json("""{"gstRule":2,"expectedEligibleCount":0}"""));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        using var body = JsonDocument.Parse(await again.Content.ReadAsStringAsync());
        Assert.Equal(0, body.RootElement.GetProperty("changedCount").GetInt32());
        Assert.All(
            await _factory.StoredRulesAsync(_factory.BusinessAId),
            rule => Assert.Equal(GstClassification.Taxable, rule));
        Assert.Equal(costsBefore, await _factory.CostAndStockStateAsync());
    }

    /// <summary>
    /// The conflict, over the wire: the operator confirmed a count, a product was configured in
    /// between, and the apply refuses with <c>409</c> and the Domain's own sentence rather than
    /// writing a set nobody approved. A fresh preview is then the way forward, which is what the
    /// message asks for.
    /// </summary>
    [Fact]
    public async Task A_count_that_moved_since_the_confirmation_is_a_409_that_writes_nothing()
    {
        await _factory.ResetAsync();
        var confirmed = await EligibleCountAsync(_factory.BusinessA());
        var configured = await _factory.ProductIdAsync(E2ETestFixture.CorrectionProductName);
        await _factory.BusinessA().PutAsync(
            $"/api/products/{configured}/gst-rule", Json("""{"gstRule":1}"""));

        var response = await _factory.BusinessA().PostAsync(
            ApplyUri, Json($$"""{"gstRule":2,"expectedEligibleCount":{{confirmed}}}"""));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            BulkProductGstRulePolicy.EligibleCountChangedMessage,
            problem.RootElement.GetProperty("message").GetString());

        // Nothing was configured, and the fresh count is the one to confirm next.
        Assert.Equal(GstClassification.Taxable, await _factory.StoredRuleAsync(configured));
        Assert.Equal(confirmed - 1, await EligibleCountAsync(_factory.BusinessA()));
        Assert.Equal(
            GstRules.None,
            await _factory.StoredRuleAsync(
                await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName)));
    }

    /// <summary>
    /// The hole an enum does not close: an arbitrary JSON number binds to
    /// <see cref="GstClassification"/>, and so does the <c>0</c> an omitted <c>gstRule</c> binds to.
    /// Both are refused with <c>400</c> and configure nothing - and <c>0</c> is refused even though
    /// it is a declared classification, because "no rule" is what the eligible products already
    /// carry.
    /// </summary>
    [Theory]
    [InlineData("999")]
    [InlineData("-1")]
    [InlineData("3")]
    [InlineData("0")]
    public async Task A_target_that_is_not_taxable_or_gst_free_is_refused_and_configures_nothing(string value)
    {
        await _factory.ResetAsync();
        var eligible = await EligibleCountAsync(_factory.BusinessA());
        var purchasesBefore = await _factory.PurchaseRowsAsync();

        var response = await _factory.BusinessA().PostAsync(
            ApplyUri, Json($$"""{"gstRule":{{value}},"expectedEligibleCount":{{eligible}}}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            BulkProductGstRulePolicy.UnsupportedTargetMessage,
            problem.RootElement.GetProperty("message").GetString());
        Assert.Equal(eligible, await EligibleCountAsync(_factory.BusinessA()));
        Assert.Equal(purchasesBefore, await _factory.PurchaseRowsAsync());
    }

    [Fact]
    public async Task A_request_with_no_confirmed_count_is_refused_and_configures_nothing()
    {
        await _factory.ResetAsync();
        var eligible = await EligibleCountAsync(_factory.BusinessA());

        var response = await _factory.BusinessA().PostAsync(ApplyUri, Json("""{"gstRule":1}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            BulkProductGstRulePolicy.ExpectedEligibleCountRequiredMessage,
            problem.RootElement.GetProperty("message").GetString());
        Assert.Equal(eligible, await EligibleCountAsync(_factory.BusinessA()));
    }

    /// <summary>
    /// The single-product resource is unchanged by all of this: it still accepts "no rule" as a
    /// legitimate value for one product, which the bulk action refuses as a target.
    /// </summary>
    [Fact]
    public async Task Single_product_gst_rule_editing_still_accepts_no_rule()
    {
        await _factory.ResetAsync();
        var productId = await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName);
        await _factory.BusinessA().PutAsync(
            $"/api/products/{productId}/gst-rule", Json("""{"gstRule":1}"""));

        var cleared = await _factory.BusinessA().PutAsync(
            $"/api/products/{productId}/gst-rule", Json("""{"gstRule":0}"""));

        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        Assert.Equal(GstRules.None, await _factory.StoredRuleAsync(productId));
    }

    private static async Task<int> EligibleCountAsync(HttpClient client)
    {
        var response = await client.PostAsync(PreviewUri, Json("{}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("eligibleCount").GetInt32();
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>
    /// The API hosted the way the end-to-end harness hosts it - the dedicated E2E environment, its
    /// two synthetic businesses with their own products, and a disposable relational database.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        public ApiFactory()
        {
            // Hosting must not depend on the process's current directory; see E2ETestHostContentRoot.
            E2ETestHostContentRoot.Pin();
            _connection.Open();
        }

        public HttpClient BusinessA() => As(E2ETestActors.BusinessAOwner.Key);

        public HttpClient BusinessB() => As(E2ETestActors.BusinessBOwner.Key);

        public int BusinessAId => BusinessIdOf(E2ETestFixture.PurchaseProductName);

        public int BusinessBId => BusinessIdOf(E2ETestFixture.BusinessBProductName);

        /// <summary>
        /// Puts every product of both businesses back to "no rule" and active, and seeds the
        /// classified purchase once, so each test starts from the state the fixture ships with
        /// regardless of the order the tests ran in. The rule column is the only thing these tests
        /// change, which is what makes a reset this small enough.
        /// </summary>
        public async Task ResetAsync()
        {
            await EnsureClassifiedPurchaseAsync();

            await using var db = Database();
            foreach (var product in await db.Products.ToListAsync())
            {
                product.GstRule = GstRules.None;
                product.IsActive = true;
            }

            await db.SaveChangesAsync();
        }

        /// <summary>Takes one product out of the working catalogue, without touching anything else.</summary>
        public async Task DeactivateAsync(string productName)
        {
            await using var db = Database();

            var product = await db.Products.SingleAsync(candidate => candidate.Name == productName);
            product.IsActive = false;
            await db.SaveChangesAsync();
        }

        public async Task<long> ProductIdAsync(string productName)
        {
            await using var db = Database();
            return (await db.Products.AsNoTracking()
                .SingleAsync(product => product.Name == productName)).Id;
        }

        public async Task<int> ProductCountAsync(int businessId)
        {
            await using var db = Database();
            return await db.Products.AsNoTracking().CountAsync(product => product.BusinessId == businessId);
        }

        public async Task<GstClassification> StoredRuleAsync(long productId)
        {
            await using var db = Database();
            return (await db.Products.AsNoTracking()
                .SingleAsync(product => product.Id == productId)).GstRule;
        }

        public async Task<IReadOnlyList<GstClassification>> StoredRulesAsync(int businessId)
        {
            await using var db = Database();

            var rules = await db.Products.AsNoTracking()
                .Where(product => product.BusinessId == businessId)
                .OrderBy(product => product.Id)
                .Select(product => product.GstRule)
                .ToListAsync();

            Assert.NotEmpty(rules);
            return rules;
        }

        /// <summary>
        /// Every persisted purchase and purchase line of both businesses, with its GST
        /// classification and provenance, as one comparable value - so a bulk rule change can be
        /// shown to have reclassified and changed nothing. The same projection
        /// <see cref="ProductAndSupplierGstRuleApiTests"/> compares for the single-product write.
        /// </summary>
        public async Task<string> PurchaseRowsAsync()
        {
            await EnsureClassifiedPurchaseAsync();

            await using var db = Database();
            var purchases = await db.Receipts.AsNoTracking().OrderBy(r => r.Id)
                .Select(r => $"{r.Id}:{r.BusinessId}:{r.Title}:{r.DeliveryCost}:{r.DeliveryGstClassification}:" +
                    $"{r.DeliveryGstClassificationSource}:{r.PackageCost}:{r.PackageGstClassification}:" +
                    $"{r.PackageGstClassificationSource}")
                .ToListAsync();
            var items = await db.ReceiptItems.AsNoTracking().OrderBy(i => i.Id)
                .Select(i => $"{i.Id}:{i.BusinessId}:{i.ReceiptId}:{i.ProductId}:{i.Quantity}:{i.UnitCost}:" +
                    $"{i.GstClassification}:{i.GstClassificationSource}")
                .ToListAsync();
            return string.Join("|", purchases.Concat(items));
        }

        /// <summary>
        /// Every product's costing and stock position of both businesses, as one comparable value:
        /// a GST rule is configuration, so none of these figures may move when one is set.
        /// </summary>
        public async Task<string> CostAndStockStateAsync()
        {
            await using var db = Database();

            var products = await db.Products.AsNoTracking().OrderBy(product => product.Id)
                .Select(product =>
                    $"{product.Id}:{product.BusinessId}:{product.AverageUnitCost}:" +
                    $"{product.CostingQuantity}:{product.InventoryValue}:{product.QuantityInStock}:" +
                    $"{product.UpdatedAt:O}")
                .ToListAsync();
            var adjustments = await db.StockAdjustments.AsNoTracking().OrderBy(entry => entry.Id)
                .Select(entry =>
                    $"{entry.Id}:{entry.ProductId}:{entry.QuantityChange}:{entry.QuantityAfter}:" +
                    $"{entry.UnitCost}:{entry.TotalCost}")
                .ToListAsync();
            return string.Join("|", products.Concat(adjustments));
        }

        private int BusinessIdOf(string productName)
        {
            using var db = Database();
            return db.Products.AsNoTracking().Single(product => product.Name == productName).BusinessId;
        }

        /// <summary>
        /// One already-classified purchase for the business that owns the seeded catalogue, written
        /// once and idempotently - the same fixture <see cref="ProductAndSupplierGstRuleApiTests"/>
        /// seeds, and for the same reason: comparing two empty sets would prove nothing, so there
        /// has to be a purchase whose classification could have moved.
        /// </summary>
        private async Task EnsureClassifiedPurchaseAsync()
        {
            await using var db = Database();
            if (await db.Receipts.AnyAsync()) return;

            var product = await db.Products.AsNoTracking()
                .SingleAsync(candidate => candidate.Name == E2ETestFixture.PurchaseProductName);
            var seededAt = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);

            db.Receipts.Add(new Purchase
            {
                BusinessId = product.BusinessId,
                Title = "Seeded classified purchase",
                TotalAmount = 20.90m,
                DeliveryCost = 5.00m,
                DeliveryGstClassification = GstClassification.Taxable,
                DeliveryGstClassificationSource = GstClassificationSource.Manual,
                PackageCost = 2.70m,
                PackageGstClassification = GstClassification.GstFree,
                PackageGstClassificationSource = GstClassificationSource.Manual,
                PurchaseDate = seededAt,
                FileName = "scan.jpg",
                StoredFileName = "seeded.jpg",
                ContentType = "image/jpeg",
                FileSizeBytes = 2048,
                CreatedAt = seededAt,
                Items =
                {
                    new PurchaseItem
                    {
                        BusinessId = product.BusinessId,
                        ProductId = product.Id,
                        Quantity = 12m,
                        UnitCost = 1.10m,
                        GstClassification = GstClassification.Taxable,
                        GstClassificationSource = GstClassificationSource.Manual,
                    },
                },
            });

            await db.SaveChangesAsync();
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
