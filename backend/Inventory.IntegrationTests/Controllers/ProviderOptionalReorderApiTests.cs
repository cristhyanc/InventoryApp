using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Inventory.Domain.FinancialConfiguration;
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
/// Reorder and the Dashboard summary for a business with no usable Nayax connection (issue #726,
/// the #328 release gate), through the real request pipeline: authentication, the required scope,
/// the capability policies, <c>BusinessScopeMiddleware</c>, the controllers, the real
/// <c>NayaxLynxClient</c> and relational SQLite with the real migration history.
///
/// <para>The host deliberately does <strong>not</strong> stub <c>INayaxLynxClient</c>, which is what
/// makes these tests meaningful: the seeded business simply has no <c>BusinessNayaxConnection</c>
/// row, so every fleet read fails closed inside <c>NayaxRequestCredentialProvider</c> - before the
/// connection is read, before a token could be decrypted and without a request leaving the API -
/// exactly as a business that has never connected Nayax behaves in production. Before this change
/// both endpoints answered <c>409 nayax_not_connected</c>, taking two screens made of local stock
/// data down with the fleet read.</para>
///
/// <para>The serialized shape is the point. The use-case decisions are covered over fixed fixtures
/// in <c>InventoryApi.Tests.Application.Dashboard.GetDashboardSummaryProviderOptionalTests</c> and
/// <c>InventoryApi.Tests.Application.Reorder.CalculateReorderNeedsProviderOptionalTests</c>; what
/// only a hosted test can show is that the union really serializes as the withheld object rather
/// than as an empty one or a refill card full of zeros.</para>
/// </summary>
public sealed class ProviderOptionalReorderApiTests : IClassFixture<ProviderOptionalReorderApiTests.ApiFactory>
{
    private const string DashboardSummary = "/api/dashboard/summary";
    private const string LowStockAlerts = "/api/products/alerts/low-stock";

    /// <summary>The withheld section's whole property set, in ordinal order.</summary>
    private static readonly string[] UnavailableSectionProperties = ["available", "reason"];

    private readonly ApiFactory _factory;

    public ProviderOptionalReorderApiTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// The release-gate criterion: the summary is answered, the fleet section says why it is
    /// missing, and nothing about the refill card's figures is published as a zero.
    /// </summary>
    [Fact]
    public async Task The_summary_reports_the_refill_section_as_unavailable_rather_than_refusing()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId).GetAsync(DashboardSummary);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await ReadJsonAsync(response);
        var needsRefill = summary.GetProperty("needsRefill");
        Assert.Equal(UnavailableSectionProperties, Properties(needsRefill));
        Assert.False(needsRefill.GetProperty("available").GetBoolean());
        Assert.Equal("NayaxNotConnected", needsRefill.GetProperty("reason").GetString());
    }

    /// <summary>
    /// The other half of the contract: Needs Ordering is a real count, labelled with the mode it
    /// was counted in. One of the two seeded products is at its low-stock threshold on storage stock
    /// alone, and both were evaluated.
    /// </summary>
    [Fact]
    public async Task The_summary_reports_the_storage_only_ordering_count_with_its_mode()
    {
        await _factory.SeedAsync();

        var summary = await ReadJsonAsync(
            await _factory.Authenticated(ApiFactory.OwnerObjectId).GetAsync(DashboardSummary));

        var needsOrdering = summary.GetProperty("needsOrdering");
        Assert.Equal("StorageOnly", needsOrdering.GetProperty("mode").GetString());
        Assert.Equal(1, needsOrdering.GetProperty("productsNeedingOrdering").GetInt32());
        Assert.Equal(2, needsOrdering.GetProperty("productsEvaluated").GetInt32());
    }

    /// <summary>
    /// A missing provider degrades one section, not the page: the week's sales and the inventory
    /// card are the business's own local figures and are published unchanged.
    /// </summary>
    [Fact]
    public async Task The_local_sections_of_the_summary_are_unaffected()
    {
        await _factory.SeedAsync();

        var summary = await ReadJsonAsync(
            await _factory.Authenticated(ApiFactory.OwnerObjectId).GetAsync(DashboardSummary));

        Assert.Equal(
            ApiFactory.WeekToDateSaleValue,
            summary.GetProperty("salesThisWeek").GetProperty("sales").GetDecimal());
        var inventory = summary.GetProperty("inventory");
        Assert.Equal(
            ApiFactory.InventoryValueAtCost, inventory.GetProperty("inventoryValueAtCost").GetDecimal());
        Assert.Equal(2, inventory.GetProperty("productCount").GetInt32());
        Assert.Equal(45, inventory.GetProperty("unitsInStorage").GetInt32());
    }

    /// <summary>
    /// The reorder-alert listing answers the storage-only alerts instead of
    /// <c>409 nayax_not_connected</c>: the product at its threshold, with the machines' need taken
    /// as 0 and the authoritative policy's own order quantity.
    /// </summary>
    [Fact]
    public async Task The_low_stock_endpoint_returns_the_storage_only_alerts()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId).GetAsync(LowStockAlerts);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var alerts = await ReadJsonAsync(response);
        var alert = Assert.Single(alerts.EnumerateArray().ToList());
        Assert.Equal(ApiFactory.ThresholdProductName, alert.GetProperty("name").GetString());
        Assert.Equal(0, alert.GetProperty("machineReplenishmentNeed").GetInt32());
        Assert.Equal(19m, alert.GetProperty("needToOrder").GetDecimal());
        Assert.True(alert.GetProperty("isReorderAlert").GetBoolean());
    }

    private static IEnumerable<string> Properties(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal);

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    /// <summary>
    /// Hosts the real API over one in-memory SQLite database holding a single business that has
    /// never connected Nayax: one product sitting on its low-stock threshold, one comfortably
    /// stocked, one completed sale this week, and no <c>BusinessNayaxConnection</c> row at all.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string DirectoryTenantId = "99999999-9999-9999-9999-999999999999";
        public const string OwnerObjectId = "11111111-1111-1111-1111-111111111111";

        /// <summary>The product sitting exactly on its low-stock threshold, so it is the one alert.</summary>
        public const string ThresholdProductName = "Coke";

        /// <summary>The seeded products' total AVCO inventory value.</summary>
        public const decimal InventoryValueAtCost = 1000m;

        /// <summary>The seeded completed sale's settlement value.</summary>
        public const decimal WeekToDateSaleValue = 1234.56m;

        private const int BusinessId = 1;
        private const long ThresholdProductId = 100;
        private const long StockedProductId = 200;

        private static readonly DateTime CreatedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private bool _seeded;

        public ApiFactory() => _connection.Open();

        /// <summary>An unrestricted context for arranging state outside the request boundary.</summary>
        public AppDbContext Database()
        {
            // Resolving Services builds and starts the host, which is what applies the migrations
            // this context then reads and writes through.
            _ = Services;
            return TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        }

        public HttpClient Authenticated(string objectId)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.DirectoryTenantIdHeader, DirectoryTenantId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ObjectIdHeader, objectId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ScopeHeader, "access_as_user");
            return client;
        }

        public async Task SeedAsync()
        {
            await using var db = Database();
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

            // At its low-stock threshold on storage stock alone, so it alerts with no machine need:
            // 24 (restock target) - 5 (in storage) = 19 to order.
            db.Products.Add(new Product
            {
                Id = ThresholdProductId,
                BusinessId = BusinessId,
                Name = ThresholdProductName,
                QuantityInStock = 5,
                LowStockThreshold = 5,
                RestockTo = 24,
                InventoryValue = 777.77m,
            });

            // Comfortably stocked, so the alert above is a selection and not everything in the
            // catalogue, and productsEvaluated is a real "of two".
            db.Products.Add(new Product
            {
                Id = StockedProductId,
                BusinessId = BusinessId,
                Name = "Chips",
                QuantityInStock = 40,
                LowStockThreshold = 5,
                RestockTo = 24,
                InventoryValue = 222.23m,
            });

            // A completed sale a few hours ago, so the week's revenue is a real local figure the
            // summary still has to publish.
            db.NayaxSales.Add(new NayaxSales
            {
                BusinessId = BusinessId,
                TransactionID = 1,
                MachineID = 55,
                SettlementValue = WeekToDateSaleValue,
                PaymentMethod = "Credit",
                MachineAuthorizationTime = DateTime.UtcNow.AddHours(-3),
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
            });

            await db.SaveChangesAsync();
            _seeded = true;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Testing applies pending migrations at startup, so the schema under these tests is
            // the migration history rather than a model snapshot.
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

            // Authentication only. The Nayax client is deliberately the real one: these tests are
            // about what a business with no stored connection is answered.
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
    /// Stands in for Entra: the caller's <c>(tid, oid)</c> pair and delegated scope come from
    /// request headers, so membership resolution, the scope requirement and the capability policies
    /// are exercised exactly as they are in production without a real token.
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
