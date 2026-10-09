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
/// The product GST rule and supplier GST default contracts over the real HTTP pipeline (issue #430):
/// a genuine JSON body, the framework's own model binding, authentication, the business-scope
/// middleware and relational SQLite.
///
/// It is hosted rather than called in process for two reasons the acceptance criteria turn on.
///
/// First, the rule values are bound from JSON, where a C# enum is no constraint at all:
/// <c>{"gstRule": 999}</c> deserializes to a <see cref="GstClassification"/> no policy describes,
/// and an in-process controller call hands the value over already typed, so it could never show
/// that an undefined integer gets this far - or that it is refused once it does.
///
/// Second, tenant scoping has to be proved through the pipeline that resolves the business, not
/// through a hand-written business id. Both synthetic E2E businesses are used, so one business
/// naming the other's product or supplier is shown to be answered <c>404</c> by the query filters
/// themselves, with nothing changed on either side.
///
/// The purchase rows of both businesses are compared across every request: configuring a rule is
/// configuration, and must never reclassify or otherwise touch a purchase (AGENTS.md § Purchase GST
/// classification).
/// </summary>
public sealed class ProductAndSupplierGstRuleApiTests
    : IClassFixture<ProductAndSupplierGstRuleApiTests.ApiFactory>
{
    private readonly ApiFactory _factory;

    public ProductAndSupplierGstRuleApiTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_product_rule_round_trips_over_the_wire_and_changes_no_purchase()
    {
        var productId = await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName);
        var purchasesBefore = await _factory.PurchaseRowsAsync();

        Assert.Equal(GstClassification.Unknown, await ProductRuleAsync(_factory.BusinessA(), productId));

        var saved = await _factory.BusinessA().PutAsync(
            ProductRuleUri(productId), Json("""{"gstRule":1}"""));

        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        Assert.Equal(GstClassification.Taxable, await ProductRuleAsync(_factory.BusinessA(), productId));
        Assert.Equal(purchasesBefore, await _factory.PurchaseRowsAsync());
    }

    [Fact]
    public async Task Supplier_defaults_round_trip_over_the_wire_and_change_no_purchase()
    {
        var supplierId = await _factory.SupplierIdAsync(E2ETestFixture.SupplierAName);
        var purchasesBefore = await _factory.PurchaseRowsAsync();

        var saved = await _factory.BusinessA().PutAsync(
            SupplierDefaultsUri(supplierId),
            Json("""{"productLineGstDefault":2,"deliveryGstDefault":1,"packageGstDefault":0}"""));

        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        var defaults = await SupplierDefaultsAsync(_factory.BusinessA(), supplierId);
        Assert.Equal((int)GstClassification.GstFree, defaults.GetProperty("productLineGstDefault").GetInt32());
        Assert.Equal((int)GstClassification.Taxable, defaults.GetProperty("deliveryGstDefault").GetInt32());
        Assert.Equal((int)GstClassification.Unknown, defaults.GetProperty("packageGstDefault").GetInt32());
        Assert.Equal(supplierId, defaults.GetProperty("supplierId").GetInt32());
        Assert.Equal(purchasesBefore, await _factory.PurchaseRowsAsync());
    }

    /// <summary>
    /// The hole this closes: an arbitrary JSON number binds to the enum, so the value has to be
    /// refused by the application rather than by the type system. Nothing is stored, including the
    /// rule the row already had.
    /// </summary>
    [Theory]
    [InlineData("999")]
    [InlineData("-1")]
    [InlineData("3")]
    public async Task An_undefined_rule_value_is_refused_and_stores_nothing(string value)
    {
        var productId = await _factory.ProductIdAsync(E2ETestFixture.CorrectionProductName);
        var supplierId = await _factory.SupplierIdAsync(E2ETestFixture.SupplierAName);
        await _factory.BusinessA().PutAsync(ProductRuleUri(productId), Json("""{"gstRule":2}"""));
        await _factory.BusinessA().PutAsync(
            SupplierDefaultsUri(supplierId),
            Json("""{"productLineGstDefault":1,"deliveryGstDefault":2,"packageGstDefault":1}"""));
        var purchasesBefore = await _factory.PurchaseRowsAsync();

        var product = await _factory.BusinessA().PutAsync(
            ProductRuleUri(productId), Json($$"""{"gstRule":{{value}}}"""));
        var supplier = await _factory.BusinessA().PutAsync(
            SupplierDefaultsUri(supplierId),
            Json($$"""{"productLineGstDefault":0,"deliveryGstDefault":{{value}},"packageGstDefault":0}"""));

        Assert.Equal(HttpStatusCode.BadRequest, product.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, supplier.StatusCode);
        Assert.Equal(GstRules.UnsupportedRuleMessage, await product.Content.ReadAsStringAsync());
        Assert.Equal(GstRules.UnsupportedRuleMessage, await supplier.Content.ReadAsStringAsync());
        Assert.Equal(
            GstClassification.GstFree, await ProductRuleAsync(_factory.BusinessA(), productId));
        var defaults = await SupplierDefaultsAsync(_factory.BusinessA(), supplierId);
        Assert.Equal((int)GstClassification.Taxable, defaults.GetProperty("productLineGstDefault").GetInt32());
        Assert.Equal((int)GstClassification.GstFree, defaults.GetProperty("deliveryGstDefault").GetInt32());
        Assert.Equal((int)GstClassification.Taxable, defaults.GetProperty("packageGstDefault").GetInt32());
        Assert.Equal(purchasesBefore, await _factory.PurchaseRowsAsync());
    }

    /// <summary>
    /// Tenant isolation, through the real pipeline: business B names business A's product and
    /// supplier. The query filters simply do not see those rows for B, so both reads and both
    /// writes are answered <c>404</c>, and A's configuration is untouched afterwards.
    /// </summary>
    [Fact]
    public async Task One_business_cannot_read_or_set_another_businesss_rules()
    {
        var productId = await _factory.ProductIdAsync(E2ETestFixture.ReorderProductName);
        var supplierId = await _factory.SupplierIdAsync(E2ETestFixture.SupplierAName);
        await _factory.BusinessA().PutAsync(ProductRuleUri(productId), Json("""{"gstRule":1}"""));
        await _factory.BusinessA().PutAsync(
            SupplierDefaultsUri(supplierId),
            Json("""{"productLineGstDefault":1,"deliveryGstDefault":1,"packageGstDefault":1}"""));

        var readProduct = await _factory.BusinessB().GetAsync(ProductRuleUri(productId));
        var readSupplier = await _factory.BusinessB().GetAsync(SupplierDefaultsUri(supplierId));
        var writeProduct = await _factory.BusinessB().PutAsync(
            ProductRuleUri(productId), Json("""{"gstRule":2}"""));
        var writeSupplier = await _factory.BusinessB().PutAsync(
            SupplierDefaultsUri(supplierId),
            Json("""{"productLineGstDefault":2,"deliveryGstDefault":2,"packageGstDefault":2}"""));

        Assert.Equal(HttpStatusCode.NotFound, readProduct.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, readSupplier.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, writeProduct.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, writeSupplier.StatusCode);
        Assert.Equal(GstClassification.Taxable, await ProductRuleAsync(_factory.BusinessA(), productId));
        var defaults = await SupplierDefaultsAsync(_factory.BusinessA(), supplierId);
        Assert.Equal((int)GstClassification.Taxable, defaults.GetProperty("productLineGstDefault").GetInt32());
    }

    /// <summary>
    /// The business owner is resolved from the authenticated actor, never from the request
    /// (AGENTS.md § Tenant ownership and data isolation). A body that tries to name one is accepted
    /// as the rule it also carries, and the extra member changes nothing: the rule lands on
    /// business B's own supplier, and business A's identically configured supplier is unaffected.
    /// </summary>
    [Fact]
    public async Task A_submitted_business_id_is_ignored()
    {
        var businessASupplierId = await _factory.SupplierIdAsync(E2ETestFixture.SupplierAName);
        var businessBSupplierId = await _factory.SupplierIdAsync(E2ETestFixture.SupplierBName);
        await _factory.BusinessA().PutAsync(
            SupplierDefaultsUri(businessASupplierId),
            Json("""{"productLineGstDefault":1,"deliveryGstDefault":0,"packageGstDefault":0}"""));

        var response = await _factory.BusinessB().PutAsync(
            SupplierDefaultsUri(businessBSupplierId),
            Json("""
            {"businessId":1,"supplierId":4321,"productLineGstDefault":2,
             "deliveryGstDefault":0,"packageGstDefault":0}
            """));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var businessB = await SupplierDefaultsAsync(_factory.BusinessB(), businessBSupplierId);
        Assert.Equal(businessBSupplierId, businessB.GetProperty("supplierId").GetInt32());
        Assert.Equal((int)GstClassification.GstFree, businessB.GetProperty("productLineGstDefault").GetInt32());
        var businessA = await SupplierDefaultsAsync(_factory.BusinessA(), businessASupplierId);
        Assert.Equal((int)GstClassification.Taxable, businessA.GetProperty("productLineGstDefault").GetInt32());
    }

    private static string ProductRuleUri(long productId) => $"/api/products/{productId}/gst-rule";

    private static string SupplierDefaultsUri(int supplierId) => $"/api/suppliers/{supplierId}/gst-defaults";

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<GstClassification> ProductRuleAsync(HttpClient client, long productId)
    {
        var response = await client.GetAsync(ProductRuleUri(productId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(productId, document.RootElement.GetProperty("productId").GetInt64());
        return (GstClassification)document.RootElement.GetProperty("gstRule").GetInt32();
    }

    private static async Task<JsonElement> SupplierDefaultsAsync(HttpClient client, int supplierId)
    {
        var response = await client.GetAsync(SupplierDefaultsUri(supplierId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The API hosted the way the end-to-end harness hosts it - the dedicated E2E environment, its
    /// two synthetic businesses with their own products and suppliers, and a disposable relational
    /// database.
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

        public async Task<long> ProductIdAsync(string productName)
        {
            await using var db = Database();
            return (await db.Products.AsNoTracking().SingleAsync(product => product.Name == productName)).Id;
        }

        public async Task<int> SupplierIdAsync(string supplierName)
        {
            await using var db = Database();
            return (await db.Suppliers.AsNoTracking().SingleAsync(supplier => supplier.Name == supplierName)).Id;
        }

        /// <summary>
        /// Every persisted purchase and purchase line of both businesses, with its GST
        /// classification and provenance, as one comparable value - so configuring a rule can be
        /// shown to have reclassified and changed nothing.
        ///
        /// It seeds the classified purchase first, because comparing two empty sets would prove
        /// nothing: the E2E fixture seeds no purchase, so there has to be a purchase whose
        /// classification could have moved.
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
        /// One already-classified purchase for the business that owns the seeded catalogue, written
        /// once and idempotently. It is a GST classification fixture only - a row whose line and
        /// both charges carry a <c>Manual</c> classification that configuring a rule must leave
        /// exactly where it is - and deliberately records no stock movement or costing history.
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
