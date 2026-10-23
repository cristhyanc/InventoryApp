using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The product search's letter case and literal-wildcard handling (issue #827), through the real
/// request pipeline for <strong>both</strong> endpoints <c>EfProductCatalogStore.Filtered</c>
/// serves: <c>GET /api/products</c> (All Products) and <c>GET /api/products/alerts/low-stock</c>
/// (Needs Ordering).
///
/// <para>Relational SQLite rather than the EF InMemory provider, deliberately: InMemory evaluates a
/// LINQ <c>Where</c> with ordinary C# string comparison, which is already case-insensitive-by-default
/// for nothing in particular and would not have caught the bug this issue fixes - SQLite's real
/// translation of <c>Contains</c> to <c>instr(...)</c>, case-sensitive, is what a search typed in a
/// different case from the stored name or SKU used to find nothing against.</para>
/// </summary>
public sealed class ProductSearchCaseInsensitivityApiTests
    : IClassFixture<ProductSearchCaseInsensitivityApiTests.ApiFactory>
{
    private const string AllProducts = "/api/products";
    private const string LowStockAlerts = "/api/products/alerts/low-stock";

    private readonly ApiFactory _factory;

    public ProductSearchCaseInsensitivityApiTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// "Coca Cola" / "CC-330" is found by its name or its SKU typed in any letter case, on both
    /// endpoints - the product is seeded at its low-stock threshold so it alerts on Needs Ordering
    /// too, and "Chips" is seeded alongside it, comfortably stocked, so a match proves the search
    /// narrowed the catalogue rather than the endpoint returning everything regardless.
    /// </summary>
    [Theory]
    [InlineData(AllProducts, "coca")]
    [InlineData(AllProducts, "COCA")]
    [InlineData(AllProducts, "CoCa")]
    [InlineData(AllProducts, "cc-330")]
    [InlineData(AllProducts, "CC-330")]
    [InlineData(LowStockAlerts, "coca")]
    [InlineData(LowStockAlerts, "COCA")]
    [InlineData(LowStockAlerts, "CoCa")]
    [InlineData(LowStockAlerts, "cc-330")]
    [InlineData(LowStockAlerts, "CC-330")]
    public async Task A_search_in_any_letter_case_finds_the_product_by_name_or_sku(string path, string search)
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated().GetAsync($"{path}?search={search}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([ApiFactory.ThresholdProductName], await ProductNamesAsync(response));
    }

    /// <summary>
    /// A literal <c>%</c> or <c>_</c> a person types matches only a product that actually contains
    /// that character - never every product, which is what an unescaped wildcard would do here,
    /// since neither seeded product's name or SKU contains either character.
    /// </summary>
    [Theory]
    [InlineData(AllProducts, "%")]
    [InlineData(AllProducts, "_")]
    [InlineData(LowStockAlerts, "%")]
    [InlineData(LowStockAlerts, "_")]
    public async Task A_literal_wildcard_character_matches_nothing_rather_than_everything(string path, string search)
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated().GetAsync($"{path}?search={Uri.EscapeDataString(search)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await ProductNamesAsync(response));
    }

    /// <summary>
    /// A literal underscore is not a stand-in for the SKU's dash: unescaped, <c>_</c> as a
    /// single-character wildcard would match "CC-330" by matching the dash. Escaped, it is a
    /// character the SKU does not contain, so the search finds nothing.
    /// </summary>
    [Fact]
    public async Task A_literal_underscore_does_not_stand_in_for_the_skus_dash()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated().GetAsync($"{AllProducts}?search=CC_330");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await ProductNamesAsync(response));
    }

    private static async Task<IReadOnlyList<string>> ProductNamesAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.EnumerateArray()
            .Select(product => product.GetProperty("name").GetString()!)
            .ToList();
    }

    /// <summary>
    /// Hosts the real API over one in-memory SQLite database holding a single business with two
    /// products: "Coca Cola" ("CC-330"), seeded at its low-stock threshold so it alerts on Needs
    /// Ordering as well as appearing on All Products, and "Chips", comfortably stocked, so a search
    /// match is a real narrowing rather than an unfiltered listing.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string ThresholdProductName = "Coca Cola";
        public const string ThresholdProductSku = "CC-330";

        private const string DirectoryTenantId = "77777777-7777-7777-7777-777777777777";
        private const string OwnerObjectId = "33333333-3333-3333-3333-333333333333";
        private const int BusinessId = 1;
        private const long ThresholdProductId = 100;
        private const long StockedProductId = 200;

        private static readonly DateTime CreatedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private bool _seeded;

        public ApiFactory() => _connection.Open();

        public HttpClient Authenticated()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.DirectoryTenantIdHeader, DirectoryTenantId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ObjectIdHeader, OwnerObjectId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ScopeHeader, "access_as_user");
            return client;
        }

        public async Task SeedAsync()
        {
            // Resolving Services builds and starts the host, which applies the migrations this
            // context then writes through.
            _ = Services;
            await using var db = TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
            if (_seeded)
            {
                return;
            }

            db.Businesses.Add(new Business
            {
                Id = BusinessId,
                Name = "Vending Co",
                TimeZoneId = "Australia/Sydney",
                Status = BusinessStatus.Active,
                CreatedAtUtc = CreatedAt,
            });

            db.BusinessMemberships.Add(new BusinessMembership
            {
                BusinessId = BusinessId,
                DirectoryTenantId = DirectoryTenantId,
                ObjectId = OwnerObjectId,
                Role = BusinessRole.Owner,
                IsActive = true,
                CreatedAtUtc = CreatedAt,
                StatusChangedAtUtc = CreatedAt,
            });

            // At its low-stock threshold on storage stock alone, so it alerts on Needs Ordering too.
            db.Products.Add(new Product
            {
                Id = ThresholdProductId,
                BusinessId = BusinessId,
                Name = ThresholdProductName,
                Sku = ThresholdProductSku,
                QuantityInStock = 5,
                LowStockThreshold = 5,
                RestockTo = 24,
            });

            // Comfortably stocked, so a search match proves the listing was narrowed rather than
            // returning everything regardless.
            db.Products.Add(new Product
            {
                Id = StockedProductId,
                BusinessId = BusinessId,
                Name = "Chips",
                Sku = "CH-100",
                QuantityInStock = 40,
                LowStockThreshold = 5,
                RestockTo = 24,
            });

            await db.SaveChangesAsync();
            _seeded = true;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Testing applies pending migrations at startup, so the schema under these tests is the
            // migration history rather than a model snapshot.
            builder.UseEnvironment("Testing");

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

            builder.ConfigureTestServices(services =>
                services.AddAuthentication(TestActorAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestActorAuthenticationHandler>(
                        TestActorAuthenticationHandler.SchemeName, _ => { }));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _connection.Dispose();
            }
        }
    }

    /// <summary>
    /// Stands in for Entra: the caller's <c>(tid, oid)</c> pair and delegated scope come from request
    /// headers, so membership resolution, the scope requirement and the capability policies are
    /// exercised exactly as they are in production without a real token.
    /// </summary>
    private sealed class TestActorAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestActor";
        public const string DirectoryTenantIdHeader = "X-Test-Directory-Tenant-Id";
        public const string ObjectIdHeader = "X-Test-Object-Id";
        public const string ScopeHeader = "X-Test-Scope";

        public TestActorAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(ObjectIdHeader, out var objectId))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new("tid", Request.Headers[DirectoryTenantIdHeader].ToString()),
                new("oid", objectId.ToString()),
            };
            if (Request.Headers.TryGetValue(ScopeHeader, out var scope))
            {
                claims.Add(new Claim("scp", scope.ToString()));
            }

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
        }
    }
}
