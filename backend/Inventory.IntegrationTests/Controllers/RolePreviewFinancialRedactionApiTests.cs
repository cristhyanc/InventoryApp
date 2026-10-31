using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using InventoryApi.Auth;
using InventoryApi.Auth.PlatformAdmin;
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
/// What a previewed Operator is <em>shown</em> (issue #720), on the four endpoints whose responses
/// carry role-dependent financial figures: the dashboard summary (issue #527), the machine list
/// (#698), the site list (#697) and the global stock history (#700).
///
/// <para>This is the criterion that a frontend-only preview could not have met. The figures an
/// Operator must not see are withheld by the API, inside responses every role may request, so a
/// preview that only hid menu items would still have sent them. Because the redaction resolves
/// through <c>CurrentCapabilities</c>, narrowing that one answer is all it takes - no mapper,
/// controller or use case has a rule about previews.</para>
///
/// <para><strong>What makes each assertion a claim rather than a coincidence.</strong> The
/// administrator previewing an Operator is compared against a <em>real</em> Operator of the same
/// business over the same seeded sales, fleet, commission agreement, fee rate and movement, on a
/// fixed clock: the two bodies must be byte-for-byte identical. And the same administrator without
/// the header must be given the figures, so "withheld" is distinguishable from "there was nothing to
/// show".</para>
/// </summary>
public sealed class RolePreviewFinancialRedactionApiTests
    : IClassFixture<RolePreviewFinancialRedactionApiTests.ApiFactory>
{
    private const string DashboardSummary = "/api/dashboard/summary";
    private const string Machines = "/api/machines";
    private const string Sites = "/api/sites";
    private const string StockHistory = "/api/stock-history";

    /// <summary>
    /// The figures <c>Dashboard.ViewFinancials</c> governs on a site row (issue #697, with issue
    /// #1012's two additive periods under the same rule).
    /// </summary>
    private static readonly string[] SiteRevenueFields =
    [
        "todayRevenue", "currentWeekRevenue", "previousComparableWeekRevenue", "lastWeekRevenue",
        "monthToDateRevenue",
    ];

    /// <summary>The figures it governs on a machine row (issue #698).</summary>
    private static readonly string[] MachineFinancialFields =
    [
        "todayGrossRevenue", "currentWeekGrossRevenue", "lastWeekGrossRevenue",
        "twoWeeksAgoGrossRevenue", "previousComparableWeekGrossRevenue", "monthToDateGrossRevenue",
        "todayDirectProfit", "currentWeekDirectProfit", "lastWeekDirectProfit",
        "twoWeeksAgoDirectProfit", "previousComparableWeekDirectProfit", "monthToDateDirectProfit",
        "profitabilityStatus",
    ];

    private readonly ApiFactory _factory;

    public RolePreviewFinancialRedactionApiTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// The criterion, for all four endpoints at once and over the whole payload: an administrator
    /// previewing an Operator is answered exactly what an Operator of that business is answered -
    /// every key, every nested level, every value - and that is not what they are answered without
    /// the header.
    /// </summary>
    [Theory]
    [InlineData(DashboardSummary)]
    [InlineData(Machines)]
    [InlineData(Sites)]
    [InlineData(StockHistory)]
    public async Task A_previewed_operator_is_answered_exactly_what_an_operator_is_answered(string route)
    {
        await _factory.SeedAsync();

        var previewed = await BodyAsync(_factory.Previewing("Operator"), route);
        var theirOwn = await BodyAsync(_factory.AsAdministrator(), route);
        var anOperator = await BodyAsync(
            _factory.Authenticated(ApiFactory.OperatorObjectId), route);

        Assert.Equal(anOperator, previewed);
        Assert.NotEqual(theirOwn, previewed);
    }

    /// <summary>
    /// The dashboard criterion in its own terms (issue #527): the two figures are <strong>absent
    /// from the JSON</strong> for a previewed Operator - not <c>null</c> and not <c>0</c> - and
    /// present for the same caller without the header.
    /// </summary>
    [Fact]
    public async Task The_dashboard_summary_withholds_the_weeks_sales_and_the_valuation()
    {
        await _factory.SeedAsync();

        var previewed = await JsonAsync(_factory.Previewing("Operator"), DashboardSummary);
        var theirOwn = await JsonAsync(_factory.AsAdministrator(), DashboardSummary);

        Assert.DoesNotContain("salesThisWeek", Properties(previewed));
        Assert.DoesNotContain("inventoryValueAtCost", Properties(previewed.GetProperty("inventory")));

        Assert.Contains("salesThisWeek", Properties(theirOwn));
        Assert.Equal(
            ApiFactory.InventoryValueAtCost,
            theirOwn.GetProperty("inventory").GetProperty("inventoryValueAtCost").GetDecimal());
        Assert.Equal(
            ApiFactory.TodaysSaleValue,
            theirOwn.GetProperty("salesThisWeek").GetProperty("sales").GetDecimal());

        // The sections an Operator exists to use are untouched by the preview.
        Assert.Equal(
            theirOwn.GetProperty("needsRefill").GetRawText(),
            previewed.GetProperty("needsRefill").GetRawText());
    }

    /// <summary>
    /// The machine-list criterion (issue #698): every revenue, profit and profitability-status
    /// field carries no number for a previewed Operator, and the machine is still identified - the
    /// fleet stays usable for restocking, which is the whole reason the endpoint is not simply
    /// refused.
    /// </summary>
    [Fact]
    public async Task The_machine_list_withholds_every_revenue_and_profit_figure()
    {
        await _factory.SeedAsync();

        var machine = Single(await JsonAsync(_factory.Previewing("Operator"), Machines));

        AssertNull(machine, MachineFinancialFields);
        Assert.Equal(ApiFactory.MachineId, machine.GetProperty("machineID").GetInt64());
        Assert.NotEqual(JsonValueKind.Null, machine.GetProperty("machineName").ValueKind);
    }

    [Fact]
    public async Task The_same_machine_list_carries_the_figures_without_the_header()
    {
        await _factory.SeedAsync();

        var machine = Single(await JsonAsync(_factory.AsAdministrator(), Machines));

        Assert.Equal(ApiFactory.TodaysSaleValue, machine.GetProperty("todayGrossRevenue").GetDecimal());
        Assert.Equal(
            ApiFactory.TodaysSaleValue,
            machine.GetProperty("currentWeekGrossRevenue").GetDecimal());
    }

    /// <summary>The site-list criterion (issue #697), in both directions.</summary>
    [Fact]
    public async Task The_site_list_withholds_every_revenue_figure()
    {
        await _factory.SeedAsync();

        var previewed = Single(await JsonAsync(_factory.Previewing("Operator"), Sites));
        var theirOwn = Single(await JsonAsync(_factory.AsAdministrator(), Sites));

        AssertNull(previewed, SiteRevenueFields);
        Assert.Equal(ApiFactory.TodaysSaleValue, theirOwn.GetProperty("todayRevenue").GetDecimal());
        Assert.Equal(
            ApiFactory.PreviousComparableWeekSaleValue,
            theirOwn.GetProperty("previousComparableWeekRevenue").GetDecimal());
        // Issue #1012's two additive periods are real amounts for the administrator's own request,
        // so the previewed nulls above are withheld figures and not empty periods.
        Assert.Equal(
            ApiFactory.PreviousComparableWeekSaleValue,
            theirOwn.GetProperty("lastWeekRevenue").GetDecimal());
        Assert.Equal(
            ApiFactory.TodaysSaleValue + ApiFactory.PreviousComparableWeekSaleValue,
            theirOwn.GetProperty("monthToDateRevenue").GetDecimal());
        // The restocking figures are the same for both, so the preview removed the revenue and
        // nothing else.
        Assert.Equal(
            theirOwn.GetProperty("machineCount").GetRawText(),
            previewed.GetProperty("machineCount").GetRawText());
    }

    /// <summary>The stock-history criterion (issue #700), in both directions.</summary>
    [Fact]
    public async Task The_stock_history_withholds_the_resulting_inventory_value()
    {
        await _factory.SeedAsync();

        var previewed = Movement(await JsonAsync(_factory.Previewing("Operator"), StockHistory));
        var theirOwn = Movement(await JsonAsync(_factory.AsAdministrator(), StockHistory));

        Assert.Equal(JsonValueKind.Null, previewed.GetProperty("inventoryValueAfter").ValueKind);
        Assert.Equal(
            ApiFactory.InventoryValueAfter,
            theirOwn.GetProperty("inventoryValueAfter").GetDecimal());
        // The movement's own costs are not the business's valuation and stay visible, exactly as
        // they do for a real Operator.
        Assert.Equal(ApiFactory.MovementUnitCost, previewed.GetProperty("unitCost").GetDecimal());
    }

    /// <summary>
    /// Withheld rather than renamed, zeroed or reported somewhere else: none of the four seeded
    /// amounts appears anywhere in any of the four previewed bodies, under any property name.
    /// </summary>
    [Fact]
    public async Task A_previewed_operators_bodies_carry_none_of_the_restricted_amounts()
    {
        await _factory.SeedAsync();
        var client = _factory.Previewing("Operator");

        foreach (var route in new[] { DashboardSummary, Machines, Sites, StockHistory })
        {
            var body = await BodyAsync(client, route);

            foreach (var amount in new[]
                     {
                         ApiFactory.TodaysSaleValue, ApiFactory.PreviousComparableWeekSaleValue,
                         ApiFactory.InventoryValueAtCost, ApiFactory.InventoryValueAfter,
                     })
            {
                Assert.DoesNotContain(
                    amount.ToString(CultureInfo.InvariantCulture), body, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// And a preview of a role that holds the capability changes nothing: a Manager preview is
    /// answered every figure, because the intersection keeps what both sides grant.
    /// </summary>
    [Fact]
    public async Task A_manager_preview_is_still_answered_the_figures()
    {
        await _factory.SeedAsync();

        var site = Single(await JsonAsync(_factory.Previewing("Manager"), Sites));

        Assert.Equal(ApiFactory.TodaysSaleValue, site.GetProperty("todayRevenue").GetDecimal());
    }

    private static void AssertNull(JsonElement element, IEnumerable<string> fields)
    {
        foreach (var field in fields)
        {
            Assert.Equal(JsonValueKind.Null, element.GetProperty(field).ValueKind);
        }
    }

    private static JsonElement Single(JsonElement array) => Assert.Single(array.EnumerateArray());

    private static JsonElement Movement(JsonElement page) => Single(page.GetProperty("items"));

    private static IReadOnlyList<string> Properties(JsonElement element) =>
        [.. element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];

    private static async Task<string> BodyAsync(HttpClient client, string route)
    {
        var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<JsonElement> JsonAsync(HttpClient client, string route) =>
        JsonDocument.Parse(await BodyAsync(client, route)).RootElement;

    /// <summary>
    /// One business whose Owner is also the configured platform administrator, so a preview has a
    /// real base to narrow, plus a real Operator of the same business to compare against. The
    /// seeded state is the one issues #527, #697, #698 and #700 withhold figures from: two completed
    /// sales in chosen periods, a priced machine at a site, a commission agreement, a fee rate, a
    /// costed catalogue product and one stock movement.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string DirectoryTenantId = "88888888-8888-8888-8888-888888888888";

        /// <summary>The configured platform administrator, who is an active Owner here.</summary>
        public const string AdministratorObjectId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

        /// <summary>A real Operator of the same business: what a preview has to be identical to.</summary>
        public const string OperatorObjectId = "33333333-3333-3333-3333-333333333333";

        /// <summary>The seeded completed sale inside the fixed clock's business day and week.</summary>
        public const decimal TodaysSaleValue = 1234.56m;

        /// <summary>The seeded completed sale inside the previous comparable week.</summary>
        public const decimal PreviousComparableWeekSaleValue = 403.21m;

        /// <summary>The business's inventory at cost: 7 units at 61.23, which the dashboard withholds.</summary>
        public const decimal InventoryValueAtCost = 428.61m;

        /// <summary>The valuation the seeded movement left behind, which stock history withholds.</summary>
        public const decimal InventoryValueAfter = 591.37m;

        /// <summary>The movement's own cost, which every role may see.</summary>
        public const decimal MovementUnitCost = 1.52m;

        public const long MachineId = 55;

        private const int BusinessId = 1;
        private const long ProductId = 100;
        private const long SiteId = 42;
        private const int StockAdjustmentId = 500;
        private const decimal AverageUnitCost = 61.23m;
        private const int UnitsInStorage = 7;

        /// <summary>
        /// 15:00 on Thursday 12 March 2026 in Sydney, the instant
        /// <c>SiteFinancialRedactionApiTests</c> uses: the business week to date runs from Monday
        /// 9 March and the previous comparable week from Monday 2 March to the same elapsed point,
        /// so each seeded sale lands in a period chosen rather than guessed.
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

        public HttpClient AsAdministrator() => Authenticated(AdministratorObjectId);

        /// <summary>The administrator's own client, previewing <paramref name="role"/>.</summary>
        public HttpClient Previewing(string role)
        {
            var client = AsAdministrator();
            client.DefaultRequestHeaders.Add(RolePreviewHeader.Name, role);
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
                CreatedAtUtc = CreatedAt,
            });

            db.BusinessMemberships.AddRange(
                Membership(AdministratorObjectId, BusinessRole.Owner),
                Membership(OperatorObjectId, BusinessRole.Operator));

            db.Products.Add(new Product
            {
                Id = ProductId,
                BusinessId = BusinessId,
                Name = "Coke",
                QuantityInStock = UnitsInStorage,
                LowStockThreshold = 5,
                RestockTo = 24,
                AverageUnitCost = AverageUnitCost,
                CostingQuantity = UnitsInStorage,
                InventoryValue = InventoryValueAtCost,
            });

            // One movement, carrying its own cost and the resulting business valuation.
            db.StockAdjustments.Add(new StockAdjustment
            {
                Id = StockAdjustmentId,
                BusinessId = BusinessId,
                ProductId = ProductId,
                QuantityChange = 12,
                QuantityAfter = 30,
                UnitCost = MovementUnitCost,
                TotalCost = 18.24m,
                CostingQuantityAfter = 397,
                AverageUnitCostAfter = 1.49m,
                InventoryValueAfter = InventoryValueAfter,
                Reason = StockAdjustmentReason.Restock,
                Source = StockAdjustmentSource.Manual,
                CreatedAt = CreatedAt,
                EffectiveAt = CreatedAt,
            });

            // The effective-dated configuration behind the site estimate and the machine's direct
            // profit, so a caller who may see them is given real figures.
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
        /// One machine at the seeded site holding one priced, partly missing selection, so the
        /// machine and site endpoints report real stock and pricing without this host making a Nayax
        /// call or needing a Nayax connection.
        /// </summary>
        private static INayaxLynxClient Fleet()
        {
            var nayax = new Mock<INayaxLynxClient>();
            nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([new NayaxMachine { MachineID = MachineId, CustomerID = SiteId, MachineName = "Lobby" }]);
            nayax.Setup(x => x.GetMachineAsync(MachineId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new NayaxMachine { MachineID = MachineId, CustomerID = SiteId, MachineName = "Lobby" });
            nayax.Setup(x => x.GetMachineProductsAsync(MachineId, It.IsAny<CancellationToken>()))
                .ReturnsAsync([new NayaxMachineProduct
                {
                    NayaxProductID = ProductId,
                    RetailPrice = 5m,
                    PAR = 10,
                    MissingStockByMDB = 4,
                    VendOutAlertThreshold = 2,
                    MDBCode = 3,
                }]);
            return nayax.Object;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            // Testing applies pending migrations at startup, so the schema under these tests is
            // the migration history rather than a model snapshot.
            builder.UseEnvironment("Testing");

            // The one configuration this feature needs: the Owner below is also the platform
            // administrator, which is what makes a preview honoured at all.
            builder.UseSetting($"{PlatformAdminOptions.SectionName}:DirectoryTenantId", DirectoryTenantId);
            builder.UseSetting($"{PlatformAdminOptions.SectionName}:ObjectId", AdministratorObjectId);

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
                // Australia/Sydney zone, so every revenue period is fixed rather than whichever
                // business day the suite happens to run on.
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
    /// request headers, so membership resolution, the scope requirement, the capability policies and
    /// the platform-admin policy the role preview re-evaluates are all exercised exactly as they are
    /// in production without a real token.
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
