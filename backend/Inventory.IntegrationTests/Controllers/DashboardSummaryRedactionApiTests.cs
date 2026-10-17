using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Inventory.Application.Nayax;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Providers;
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
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The Dashboard summary's role-dependent response shape (issue #527), exercised through the real
/// request pipeline: authentication, the required scope, the <c>Dashboard.View</c> capability
/// policy, <c>BusinessScopeMiddleware</c>, the controller and relational SQLite with the real
/// migration history.
///
/// <para>What it has to prove is a property set, not a value: a caller without
/// <c>Dashboard.ViewFinancials</c> receives a response in which <c>salesThisWeek</c> and
/// <c>inventory.inventoryValueAtCost</c> are <strong>absent from the JSON</strong> - not
/// <c>null</c>, not <c>0</c> - while everything else is exactly what a Manager and an Owner are
/// given. Three members of one business hold the three roles against one seeded fleet, catalogue
/// and sale, so every difference between the three responses is the role and nothing else.</para>
///
/// <para>The valuation is asserted as its seeded figure, which is deterministic; the week's revenue
/// is asserted as a property set only, because the business week this host resolves depends on the
/// real clock. The figures themselves are covered over a fixed clock in
/// <c>InventoryApi.Tests.Application.Dashboard.GetDashboardSummaryRedactionTests</c>.</para>
/// </summary>
public sealed class DashboardSummaryRedactionApiTests : IClassFixture<DashboardSummaryRedactionApiTests.ApiFactory>
{
    private const string Uri = "/api/dashboard/summary";

    /// <summary>Every property the full summary publishes, in ordinal order.</summary>
    private static readonly string[] FullSummaryProperties =
        ["asOfUtc", "businessDate", "inventory", "needsOrdering", "needsRefill", "salesThisWeek"];

    /// <summary>The same set without the one financial card, in ordinal order.</summary>
    private static readonly string[] RedactedSummaryProperties =
        ["asOfUtc", "businessDate", "inventory", "needsOrdering", "needsRefill"];

    private static readonly string[] FullInventoryProperties =
    [
        "inventoryValueAtCost", "isInventoryValueComplete", "productCount", "productsWithUnknownCost",
        "unitsInStorage",
    ];

    private static readonly string[] RedactedInventoryProperties =
        ["isInventoryValueComplete", "productCount", "productsWithUnknownCost", "unitsInStorage"];

    private static readonly string[] SalesThisWeekProperties =
    [
        "changeAmount", "changePercent", "comparisonNote", "comparisonPeriod", "comparisonSales",
        "comparisonTransactionCount", "isComparisonAvailable", "period", "sales", "transactionCount",
    ];

    private readonly ApiFactory _factory;

    public DashboardSummaryRedactionApiTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// The acceptance criterion: an Operator holds <c>Dashboard.View</c> and reaches the endpoint,
    /// and the response simply does not have the two financial properties.
    /// </summary>
    [Fact]
    public async Task An_operator_is_answered_the_summary_without_its_financial_figures()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.OperatorObjectId).GetAsync(Uri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);
        Assert.Equal(RedactedSummaryProperties, Properties(json));
        Assert.Equal(RedactedInventoryProperties, Properties(json.GetProperty("inventory")));
    }

    /// <summary>
    /// Absent rather than renamed, nulled or zeroed: neither property name, and neither figure,
    /// appears anywhere in an Operator's response body under any name.
    /// </summary>
    [Fact]
    public async Task An_operators_response_body_carries_neither_financial_figure()
    {
        await _factory.SeedAsync();

        var body = await (await _factory.Authenticated(ApiFactory.OperatorObjectId).GetAsync(Uri))
            .Content.ReadAsStringAsync();

        Assert.DoesNotContain("salesThisWeek", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("inventoryValueAtCost", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            ApiFactory.InventoryValueAtCost.ToString(CultureInfo.InvariantCulture), body, StringComparison.Ordinal);
        Assert.DoesNotContain(
            ApiFactory.WeekToDateSaleValue.ToString(CultureInfo.InvariantCulture), body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A role that holds <c>Dashboard.ViewFinancials</c> is answered the contract unchanged: the
    /// full property set at every level, and the real seeded valuation.
    /// </summary>
    [Theory]
    [InlineData(ApiFactory.ManagerObjectId)]
    [InlineData(ApiFactory.OwnerObjectId)]
    public async Task A_role_that_holds_the_financial_capability_is_answered_the_whole_summary(string objectId)
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(objectId).GetAsync(Uri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);
        Assert.Equal(FullSummaryProperties, Properties(json));
        Assert.Equal(FullInventoryProperties, Properties(json.GetProperty("inventory")));
        Assert.Equal(SalesThisWeekProperties, Properties(json.GetProperty("salesThisWeek")));
        Assert.Equal(
            ApiFactory.InventoryValueAtCost,
            json.GetProperty("inventory").GetProperty("inventoryValueAtCost").GetDecimal());
    }

    /// <summary>
    /// Redaction removes two figures and changes nothing else. The refill and ordering cards and
    /// every remaining inventory figure are value-for-value the ones a Manager is given over the
    /// same fleet, catalogue and sale.
    /// </summary>
    [Fact]
    public async Task An_operator_is_given_the_same_non_financial_figures_as_a_manager()
    {
        await _factory.SeedAsync();

        var asOperator = await ReadJsonAsync(await _factory.Authenticated(ApiFactory.OperatorObjectId).GetAsync(Uri));
        var asManager = await ReadJsonAsync(await _factory.Authenticated(ApiFactory.ManagerObjectId).GetAsync(Uri));

        Assert.Equal(
            asManager.GetProperty("needsRefill").GetRawText(),
            asOperator.GetProperty("needsRefill").GetRawText());
        Assert.Equal(
            asManager.GetProperty("needsOrdering").GetRawText(),
            asOperator.GetProperty("needsOrdering").GetRawText());
        foreach (var property in RedactedInventoryProperties)
        {
            Assert.Equal(
                asManager.GetProperty("inventory").GetProperty(property).GetRawText(),
                asOperator.GetProperty("inventory").GetProperty(property).GetRawText());
        }

        // The seeded fleet really has something to report, so the comparison above is not two
        // empty cards agreeing with each other.
        Assert.Equal(1, asOperator.GetProperty("needsRefill").GetProperty("machinesNeedingRefill").GetInt32());
        Assert.Equal(1, asOperator.GetProperty("inventory").GetProperty("productCount").GetInt32());
    }

    private static IEnumerable<string> Properties(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal);

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string DirectoryTenantId = "99999999-9999-9999-9999-999999999999";
        public const string OwnerObjectId = "11111111-1111-1111-1111-111111111111";
        public const string ManagerObjectId = "22222222-2222-2222-2222-222222222222";
        public const string OperatorObjectId = "33333333-3333-3333-3333-333333333333";

        /// <summary>The seeded product's AVCO inventory value.</summary>
        public const decimal InventoryValueAtCost = 777.77m;

        /// <summary>The seeded completed sale's settlement value.</summary>
        public const decimal WeekToDateSaleValue = 1234.56m;

        private const int BusinessId = 1;
        private const long ProductId = 100;
        private const long MachineId = 55;

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

            db.BusinessMemberships.AddRange(
                Membership(OwnerObjectId, BusinessRole.Owner),
                Membership(ManagerObjectId, BusinessRole.Manager),
                Membership(OperatorObjectId, BusinessRole.Operator));

            db.Products.Add(new Product
            {
                Id = ProductId,
                BusinessId = BusinessId,
                Name = "Coke",
                QuantityInStock = 12,
                LowStockThreshold = 5,
                RestockTo = 24,
                InventoryValue = InventoryValueAtCost,
            });

            // The product's Nayax link (issue #732), without which the machine selection resolves to
            // no local product at all (issue #733). Every imported product has one in production.
            db.ProductExternalIds.Add(new ProductExternalId
            {
                BusinessId = BusinessId,
                Provider = ExternalProvider.Nayax,
                ExternalId = NayaxExternalId.From(ProductId),
                ProductId = ProductId,
            });

            // A completed sale a few hours ago, so a caller who may see the week's revenue has a
            // real figure to be given and a caller who may not has one to be denied.
            db.NayaxSales.Add(new NayaxSales
            {
                BusinessId = BusinessId,
                TransactionID = 1,
                MachineID = MachineId,
                SettlementValue = WeekToDateSaleValue,
                PaymentMethod = "Credit",
                MachineAuthorizationTime = DateTime.UtcNow.AddHours(-3),
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
            });

            await db.SaveChangesAsync();
            _seeded = true;
        }

        private static BusinessMembership Membership(string objectId, BusinessRole role) =>
            new()
            {
                BusinessId = BusinessId,
                DirectoryTenantId = DirectoryTenantId,
                ObjectId = objectId,
                Role = role,
                IsActive = true,
                CreatedAtUtc = CreatedAt,
                StatusChangedAtUtc = CreatedAt,
            };

        /// <summary>
        /// One machine whose only selection is empty, so the refill and ordering cards report a
        /// real alert without this host making a Nayax call or needing a Nayax connection.
        /// </summary>
        private static INayaxLynxClient Fleet()
        {
            var nayax = new Mock<INayaxLynxClient>();
            nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([new NayaxMachine { MachineID = MachineId }]);
            nayax.Setup(x => x.GetMachineProductsAsync(MachineId, It.IsAny<CancellationToken>()))
                .ReturnsAsync([new NayaxMachineProduct
                {
                    NayaxProductID = ProductId, PAR = 10, MissingStockByMDB = 10, VendOutAlertThreshold = 2,
                }]);
            return nayax.Object;
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

            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(TestActorAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestActorAuthenticationHandler>(
                        TestActorAuthenticationHandler.SchemeName, _ => { });
                services.AddSingleton(Fleet());
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
    }

    /// <summary>
    /// Stands in for Entra: the caller's <c>(tid, oid)</c> pair and delegated scope come from
    /// request headers, so membership resolution, the scope requirement and the capability policy
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
