using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using InventoryApi.Tests.Application.Time;
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
/// The site endpoints' role-dependent financial figures (issue #697), exercised through the real
/// request pipeline: authentication, the required scope, the <c>Inventory.Operate</c> capability
/// policy, <c>BusinessScopeMiddleware</c>, the controller and relational SQLite with the real
/// migration history.
///
/// <para>What it has to prove is about the <strong>raw JSON</strong>, because that is what an
/// Operator calling the API directly receives whatever the Angular application chooses to render
/// (issue #699): on <c>GET /api/sites</c> the three revenue figures, and on
/// <c>GET /api/sites/{id}/products</c> the estimated card profit, carry no number at all for a
/// caller without <c>Dashboard.ViewFinancials</c> - they are <c>null</c>, never <c>0</c> - while
/// stock, MDB codes, unit cost and site price are value-for-value what a Manager and an Owner are
/// given. Three members of one business hold the three roles against one seeded fleet, catalogue,
/// commission agreement, fee rate and pair of completed sales, so every difference between the
/// three responses is the role and nothing else.</para>
///
/// <para>The host runs on a fixed clock, so the business day, the week to
/// date and the previous comparable week the revenue figures are summed over are the same every
/// run and all three figures are a non-zero amount for a caller who may see them. A redacted
/// <c>previousComparableWeekRevenue</c> is therefore distinguishable from the honest <c>0</c> an
/// empty period would report.</para>
/// </summary>
public sealed class SiteFinancialRedactionApiTests : IClassFixture<SiteFinancialRedactionApiTests.ApiFactory>
{
    private const string SitesUri = "/api/sites";
    private const string SiteProductsUri = "/api/sites/42/products";

    /// <summary>The revenue figures <c>Dashboard.ViewFinancials</c> governs on a site row.</summary>
    private static readonly string[] SiteRevenueProperties =
        ["todayRevenue", "currentWeekRevenue", "previousComparableWeekRevenue"];

    private readonly ApiFactory _factory;

    public SiteFinancialRedactionApiTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// The acceptance criterion for <c>GET /api/sites</c>: an Operator holds
    /// <c>Inventory.Operate</c> and reaches the endpoint, and not one of the three revenue
    /// properties carries a number.
    /// </summary>
    [Fact]
    public async Task An_operator_is_answered_sites_with_no_revenue_figure()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.OperatorObjectId).GetAsync(SitesUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var site = Assert.Single((await ReadJsonAsync(response)).EnumerateArray());
        foreach (var property in SiteRevenueProperties)
        {
            Assert.Equal(JsonValueKind.Null, site.GetProperty(property).ValueKind);
        }
    }

    /// <summary>
    /// The acceptance criterion for <c>GET /api/sites/{id}/products</c>: the estimated card profit
    /// carries no number, while the unit cost and the site price - which the recorded decision
    /// deliberately leaves visible to every role - do.
    /// </summary>
    [Fact]
    public async Task An_operator_is_answered_site_products_with_no_estimated_profit()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.OperatorObjectId).GetAsync(SiteProductsUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var product = Assert.Single((await ReadJsonAsync(response)).EnumerateArray());
        Assert.Equal(JsonValueKind.Null, product.GetProperty("estimatedCardProfit").ValueKind);
        Assert.Equal(ApiFactory.AverageUnitCost, product.GetProperty("averageUnitCost").GetDecimal());
        Assert.Equal(ApiFactory.RetailPrice, product.GetProperty("sitePrice").GetDecimal());
    }

    /// <summary>
    /// Withheld rather than renamed, zeroed or reported somewhere else in the body: none of the
    /// four seeded amounts appears anywhere in either of an Operator's response bodies, under any
    /// property name.
    /// </summary>
    [Fact]
    public async Task An_operators_response_bodies_carry_none_of_the_restricted_amounts()
    {
        await _factory.SeedAsync();
        var client = _factory.Authenticated(ApiFactory.OperatorObjectId);

        var sites = await (await client.GetAsync(SitesUri)).Content.ReadAsStringAsync();
        var products = await (await client.GetAsync(SiteProductsUri)).Content.ReadAsStringAsync();

        foreach (var amount in new[]
                 {
                     ApiFactory.TodaysSaleValue, ApiFactory.PreviousComparableWeekSaleValue,
                     ApiFactory.EstimatedCardProfit,
                 })
        {
            var text = amount.ToString(CultureInfo.InvariantCulture);
            Assert.DoesNotContain(text, sites, StringComparison.Ordinal);
            Assert.DoesNotContain(text, products, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A role that holds <c>Dashboard.ViewFinancials</c> is answered the contract unchanged: every
    /// revenue figure is the seeded amount, including the previous comparable week, which is what
    /// makes the Operator's <c>null</c> above a withheld figure rather than an empty period.
    /// </summary>
    [Theory]
    [InlineData(ApiFactory.ManagerObjectId)]
    [InlineData(ApiFactory.OwnerObjectId)]
    public async Task A_role_that_holds_the_financial_capability_is_answered_every_site_revenue_figure(
        string objectId)
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(objectId).GetAsync(SitesUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var site = Assert.Single((await ReadJsonAsync(response)).EnumerateArray());
        Assert.Equal(ApiFactory.TodaysSaleValue, site.GetProperty("todayRevenue").GetDecimal());
        Assert.Equal(ApiFactory.TodaysSaleValue, site.GetProperty("currentWeekRevenue").GetDecimal());
        Assert.Equal(
            ApiFactory.PreviousComparableWeekSaleValue,
            site.GetProperty("previousComparableWeekRevenue").GetDecimal());
    }

    [Theory]
    [InlineData(ApiFactory.ManagerObjectId)]
    [InlineData(ApiFactory.OwnerObjectId)]
    public async Task A_role_that_holds_the_financial_capability_is_answered_the_estimated_profit(string objectId)
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(objectId).GetAsync(SiteProductsUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var product = Assert.Single((await ReadJsonAsync(response)).EnumerateArray());
        Assert.Equal(ApiFactory.EstimatedCardProfit, product.GetProperty("estimatedCardProfit").GetDecimal());
    }

    /// <summary>
    /// Redaction removes the revenue and the estimate and changes nothing else: every other
    /// property of both responses is byte-for-byte the one a Manager is given over the same fleet,
    /// catalogue, commission agreement, fee rate and sales - stock, alert counts, MDB codes, unit
    /// cost and site price included.
    /// </summary>
    [Fact]
    public async Task An_operator_is_given_the_same_non_financial_values_as_a_manager()
    {
        await _factory.SeedAsync();

        var operatorSite = Assert.Single(
            (await ReadJsonAsync(await _factory.Authenticated(ApiFactory.OperatorObjectId).GetAsync(SitesUri)))
            .EnumerateArray());
        var managerSite = Assert.Single(
            (await ReadJsonAsync(await _factory.Authenticated(ApiFactory.ManagerObjectId).GetAsync(SitesUri)))
            .EnumerateArray());
        var operatorProduct = Assert.Single(
            (await ReadJsonAsync(await _factory.Authenticated(ApiFactory.OperatorObjectId).GetAsync(SiteProductsUri)))
            .EnumerateArray());
        var managerProduct = Assert.Single(
            (await ReadJsonAsync(await _factory.Authenticated(ApiFactory.ManagerObjectId).GetAsync(SiteProductsUri)))
            .EnumerateArray());

        // The property sets themselves are unchanged: the restricted figures are nullable rather
        // than absent, so an Operator's row still publishes every name a Manager's does.
        Assert.Equal(Properties(managerSite), Properties(operatorSite));
        Assert.Equal(Properties(managerProduct), Properties(operatorProduct));

        foreach (var property in Properties(managerSite).Except(SiteRevenueProperties))
        {
            Assert.Equal(
                managerSite.GetProperty(property).GetRawText(), operatorSite.GetProperty(property).GetRawText());
        }

        foreach (var property in Properties(managerProduct).Where(name => name != "estimatedCardProfit"))
        {
            Assert.Equal(
                managerProduct.GetProperty(property).GetRawText(),
                operatorProduct.GetProperty(property).GetRawText());
        }

        // The seeded site really has something to report, so the comparison above is not two empty
        // rows agreeing with each other.
        Assert.Equal(1, operatorSite.GetProperty("machineCount").GetInt32());
        Assert.Equal(60m, operatorSite.GetProperty("totalStockPercentage").GetDecimal());
        Assert.Equal(ApiFactory.MdbCode, operatorProduct.GetProperty("mdbCode").GetInt32());
        Assert.Equal(6, operatorProduct.GetProperty("quantityInStock").GetInt32());
    }

    private static IReadOnlyList<string> Properties(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToList();

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string DirectoryTenantId = "99999999-9999-9999-9999-999999999999";
        public const string OwnerObjectId = "11111111-1111-1111-1111-111111111111";
        public const string ManagerObjectId = "22222222-2222-2222-2222-222222222222";
        public const string OperatorObjectId = "33333333-3333-3333-3333-333333333333";

        /// <summary>The seeded completed sale inside the fixed clock's business day and week.</summary>
        public const decimal TodaysSaleValue = 1234.56m;

        /// <summary>The seeded completed sale inside the previous comparable week.</summary>
        public const decimal PreviousComparableWeekSaleValue = 403.21m;

        /// <summary>The seeded product's AVCO unit cost, which every role may see.</summary>
        public const decimal AverageUnitCost = 2m;

        /// <summary>The seeded machine product's Nayax retail price, which every role may see.</summary>
        public const decimal RetailPrice = 5m;

        /// <summary>
        /// 5.00 retail - 0.50 commission (10% of gross) - 2.00 cost - 0.22 Nayax fee including GST.
        /// </summary>
        public const decimal EstimatedCardProfit = 2.28m;

        /// <summary>The seeded machine product's MDB code, which every role may see.</summary>
        public const int MdbCode = 3;

        private const int BusinessId = 1;
        private const long ProductId = 100;
        private const long MachineId = 55;
        private const long SiteId = 42;

        /// <summary>
        /// 15:00 on Thursday 12 March 2026 in Sydney. The business week to date runs from Monday
        /// 9 March, and the previous comparable week from Monday 2 March to the same elapsed point
        /// on Thursday 5 March, so each seeded sale lands in a period chosen rather than guessed.
        /// </summary>
        private static readonly DateTime NowUtc = new(2026, 3, 12, 4, 0, 0, DateTimeKind.Utc);

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
                TimeZoneId = FixedSydneyTime.SydneyTimeZoneId,
                Status = BusinessStatus.Active,
                // Selling through Nayax (issue #759), which is what makes the product's recorded
                // external id a Nayax product id.
                SalesProvider = ExternalProvider.Nayax,
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
                // The product's Nayax id (issue #732, on the row since #759), without which the
                // machine selection resolves to no local product at all (issue #733). Every imported
                // product has one in production.
                ExternalProductId = NayaxExternalId.From(ProductId),
                Name = "Coke",
                QuantityInStock = 12,
                LowStockThreshold = 5,
                RestockTo = 24,
                AverageUnitCost = AverageUnitCost,
            });

            // The effective-dated configuration behind the estimate, so a caller who may see it is
            // given a real figure and a caller who may not has a real one to be denied.
            db.SiteCommissionAgreements.Add(new SiteCommissionAgreement
            {
                BusinessId = BusinessId,
                SiteId = SiteId,
                EffectiveFrom = CreatedAt.Date,
                CommissionRate = 0.10m,
                Basis = CommissionBasis.GrossSales,
            });
            db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate
            {
                BusinessId = BusinessId,
                EffectiveFrom = CreatedAt.Date,
                FeeExGst = 0.20m,
            });

            db.NayaxSales.AddRange(
                CompletedSale(transactionId: 1, TodaysSaleValue, NowUtc.AddHours(-2)),
                CompletedSale(transactionId: 2, PreviousComparableWeekSaleValue, NowUtc.AddDays(-7).AddHours(-2)));

            await db.SaveChangesAsync();
            _seeded = true;
        }

        private static NayaxSales CompletedSale(long transactionId, decimal settlementValue, DateTime authorizedAtUtc) =>
            new()
            {
                BusinessId = BusinessId,
                TransactionID = transactionId,
                MachineID = MachineId,
                SettlementValue = settlementValue,
                PaymentMethod = "Credit",
                MachineAuthorizationTime = authorizedAtUtc,
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
            };

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
        /// One machine at the seeded site holding one priced, partly missing selection, so both
        /// endpoints report real stock and pricing without this host making a Nayax call or needing
        /// a Nayax connection.
        /// </summary>
        private static INayaxLynxClient Fleet()
        {
            var nayax = new Mock<INayaxLynxClient>();
            nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([new NayaxMachine { MachineID = MachineId, CustomerID = SiteId, MachineName = "Lobby" }]);
            nayax.Setup(x => x.GetMachineProductsAsync(MachineId, It.IsAny<CancellationToken>()))
                .ReturnsAsync([new NayaxMachineProduct
                {
                    NayaxProductID = ProductId,
                    RetailPrice = RetailPrice,
                    PAR = 10,
                    MissingStockByMDB = 4,
                    VendOutAlertThreshold = 2,
                    MDBCode = MdbCode,
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

                // The production business calendar reads this clock through the business's own
                // Australia/Sydney zone, so every revenue period below is fixed rather than
                // whichever business day the suite happens to run on.
                services.AddSingleton<IClock>(new FakeClock(NowUtc));
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
