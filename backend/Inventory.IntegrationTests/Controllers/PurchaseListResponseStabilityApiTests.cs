using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using InventoryApi.Auth.E2ETesting;
using InventoryApi.Tests.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// Issue #1289's response-stability criterion: <c>GET api/purchases</c> and
/// <c>GET api/purchases/{id}</c> must answer byte-identical JSON before and after
/// <c>EfPurchaseStore.ListAsync</c>/<c>FindByIdAsync</c> stopped tracking what they load and started
/// split-querying the list, for a purchase with several lines, a supplier and a document - the shape
/// the criterion names. These run over the real HTTP pipeline, as
/// <c>PurchaseWithoutDocumentApiTests</c> does, because the request/response contract is what is
/// being proved, not the mapper in isolation (that is
/// <c>InventoryApi.Tests.DTOs.PurchaseResponseJsonContractTests</c>, which this complements).
/// </summary>
public sealed class PurchaseListResponseStabilityApiTests
    : IClassFixture<PurchaseListResponseStabilityApiTests.ApiFactory>
{
    private const string PurchasesUri = "/api/purchases";

    private readonly ApiFactory _factory;

    public PurchaseListResponseStabilityApiTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task The_list_and_the_single_purchase_answer_byte_identical_json_for_a_fully_populated_purchase()
    {
        var productId1 = await _factory.ProductIdAsync();
        var productId2 = await _factory.SecondProductIdAsync();
        var supplierId = await _factory.SupplierIdAsync();
        var client = _factory.BusinessA();

        var created = await client.PostAsync(
            PurchasesUri, Form(supplierId, productId1, productId2, withFile: true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var purchaseId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("purchase").GetProperty("id").GetInt32();

        var listResponse = await client.GetAsync(PurchasesUri);
        var singleResponse = await client.GetAsync($"{PurchasesUri}/{purchaseId}");

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, singleResponse.StatusCode);
        var listJson = await listResponse.Content.ReadAsStringAsync();
        var singleJson = await singleResponse.Content.ReadAsStringAsync();

        // The single purchase, normalised, is exactly the list's own (and only) entry: proof that
        // the no-tracking, split-query list read and the no-tracking single read describe the same
        // purchase identically, field for field, for one with several lines, a supplier and a
        // document.
        using var listDocument = JsonDocument.Parse(listJson);
        var listedPurchase = Assert.Single(listDocument.RootElement.EnumerateArray());
        Assert.Equal(listedPurchase.GetRawText(), JsonDocument.Parse(singleJson).RootElement.GetRawText());

        var purchase = listedPurchase.GetProperty("purchase");
        Assert.Equal("Weekly restock", purchase.GetProperty("title").GetString());
        Assert.Equal(supplierId, purchase.GetProperty("supplierId").GetInt32());
        Assert.Equal(E2ETestFixture.SupplierAName, purchase.GetProperty("supplier").GetProperty("name").GetString());
        Assert.Equal(2, purchase.GetProperty("items").GetArrayLength());
        Assert.Equal("scan.jpg", purchase.GetProperty("fileName").GetString());
        Assert.NotEqual(JsonValueKind.Null, purchase.GetProperty("storedFileName").ValueKind);
    }

    private static MultipartFormDataContent Form(int supplierId, long productId1, long productId2, bool withFile)
    {
        var content = new MultipartFormDataContent();
        if (withFile) content.Add(FilePart("scan.jpg", 3), "file", "scan.jpg");

        content.Add(new StringContent("Weekly restock"), "title");
        content.Add(new StringContent("2026-04-01T10:00:00Z"), "purchaseDate");
        content.Add(new StringContent(supplierId.ToString()), "supplierId");
        content.Add(new StringContent(Items(productId1, productId2)), "items");
        return content;
    }

    private static string Items(long productId1, long productId2) =>
        $"[{{\"productId\":{productId1},\"quantity\":24,\"unitCost\":1.15}}," +
        $"{{\"productId\":{productId2},\"quantity\":10,\"unitCost\":2.00}}]";

    private static ByteArrayContent FilePart(string fileName, int byteCount)
    {
        var part = new ByteArrayContent(Enumerable.Range(1, byteCount).Select(value => (byte)value).ToArray());
        part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return part;
    }

    /// <summary>
    /// The API hosted the way the end-to-end harness hosts it, with document storage replaced by an
    /// in-memory double, the same composition <c>PurchaseWithoutDocumentApiTests.ApiFactory</c> uses.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        public ApiFactory()
        {
            E2ETestHostContentRoot.Pin();
            _connection.Open();
        }

        public HttpClient BusinessA() => As(E2ETestActors.BusinessAOwner.Key);

        public async Task<long> ProductIdAsync()
        {
            await using var db = Database();
            return (await db.Products.AsNoTracking().SingleAsync(product => product.Name == E2ETestFixture.PurchaseProductName)).Id;
        }

        /// <summary>A second product this test's purchase needs for a multi-line body (issue #1289).</summary>
        public async Task<long> SecondProductIdAsync()
        {
            await using var unrestricted = Database();
            var existing = await unrestricted.Products.AsNoTracking()
                .SingleOrDefaultAsync(product => product.Name == "Issue 1289 Second Product");
            if (existing is not null) return existing.Id;

            var businessId = (await unrestricted.Businesses.AsNoTracking()
                .SingleAsync(business => business.Name == E2ETestFixture.BusinessAName)).Id;

            await using var db = ScopedDatabase(businessId);
            var product = new Product
            {
                Name = "Issue 1289 Second Product",
                Sku = "ISSUE-1289-2",
                Unit = "unit",
                IsActive = true,
            };
            db.Products.Add(product);
            await db.SaveChangesAsync();
            return product.Id;
        }

        public async Task<int> SupplierIdAsync()
        {
            await using var db = Database();
            return (await db.Suppliers.AsNoTracking().SingleAsync(supplier => supplier.Name == E2ETestFixture.SupplierAName)).Id;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(E2ETestEnvironment.EnvironmentName);
            builder.UseSetting("Database:AllowAutomaticMigrationUnsafeOutsideDevelopment", "true");

            builder.ConfigureServices(services =>
            {
                var dbContextOptions = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (dbContextOptions is not null)
                {
                    services.Remove(dbContextOptions);
                }

                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            });

            builder.ConfigureTestServices(services =>
                services.AddSingleton<Inventory.Application.Documents.IDocumentStorage>(
                    new PurchaseWithoutDocumentApiTests.InMemoryDocumentStorage()));
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

        private AppDbContext Database()
        {
            _ = Services;
            return TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        }

        private AppDbContext ScopedDatabase(int businessId) =>
            TestAppDbContext.For(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options, businessId);
    }
}
