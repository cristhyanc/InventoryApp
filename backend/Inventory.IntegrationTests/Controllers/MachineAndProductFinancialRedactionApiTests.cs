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
/// The machine and product endpoints' role-dependent financial figures (issue #698), exercised
/// through the real request pipeline: authentication, the required scope, the
/// <c>Inventory.Operate</c> capability policy, <c>BusinessScopeMiddleware</c>, the controllers and
/// relational SQLite with the real migration history.
///
/// <para>Issue #527 established that a role without <c>Dashboard.ViewFinancials</c> - today an
/// Operator - is not shown the business's revenue or the value of its inventory, and #681 closed the
/// same figure's own endpoint. These five endpoints were the remaining way to read them directly:
/// <c>GET /api/machines</c> and <c>GET /api/machines/{id}</c> carry six gross-revenue figures, six
/// direct-profit figures and the profitability status, and <c>GET /api/products</c>,
/// <c>GET /api/products/{id}</c> and <c>GET /api/machines/{id}/products</c> carry
/// <c>inventoryValue</c>, which sums to the valuation the dashboard withholds. What this has to
/// prove is therefore a property set <em>and</em> its values: every restricted field is JSON
/// <c>null</c> for an Operator - never <c>0</c>, which would read as a real figure - while every
/// other field, including unit cost, selling price and the machine slot's own price, is
/// value-for-value the one a Manager is given.</para>
///
/// <para>Three members of one business hold the three roles against one seeded fleet, catalogue,
/// fee rate and pair of sales, so every difference between their responses is the role and nothing
/// else. The clock is pinned, so the business week and month the figures are summed over - and
/// therefore the figures themselves - are the same on every run.</para>
///
/// <para>Two machines are seeded because the two restricted kinds of figure cannot both be
/// available on one: a site-mapped machine whose completed sale carries persisted COGS has a real
/// direct profit and no profitability status, and an unmapped machine with an uncosted sale has the
/// status instead (<c>MachineProfitabilityStatusPolicy</c>). Between them the machine list carries a
/// real value in every restricted field.</para>
/// </summary>
public sealed class MachineAndProductFinancialRedactionApiTests
    : IClassFixture<MachineAndProductFinancialRedactionApiTests.ApiFactory>
{
    private const string MachinesUri = "/api/machines";
    private const string ProductsUri = "/api/products";

    private static readonly string CostedMachineUri =
        $"{MachinesUri}/{ApiFactory.CostedMachineId.ToString(CultureInfo.InvariantCulture)}";

    private static readonly string UncostedMachineUri =
        $"{MachinesUri}/{ApiFactory.UncostedMachineId.ToString(CultureInfo.InvariantCulture)}";

    private static readonly string MachineProductsUri =
        $"{CostedMachineUri}/products";

    private static readonly string ProductUri =
        $"{ProductsUri}/{ApiFactory.ProductId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Every <c>MachineResponse</c> field <c>Dashboard.ViewFinancials</c> governs: the six periods'
    /// gross revenue, the six matching direct-profit figures and the profitability status.
    /// </summary>
    private static readonly string[] RestrictedMachineFields =
    [
        "todayGrossRevenue", "currentWeekGrossRevenue", "lastWeekGrossRevenue",
        "twoWeeksAgoGrossRevenue", "previousComparableWeekGrossRevenue", "monthToDateGrossRevenue",
        "todayDirectProfit", "currentWeekDirectProfit", "lastWeekDirectProfit",
        "twoWeeksAgoDirectProfit", "previousComparableWeekDirectProfit", "monthToDateDirectProfit",
        "profitabilityStatus",
    ];

    /// <summary>
    /// The one restricted <c>ProductResponse</c> field. Unit cost and selling price are deliberately
    /// not here: the recorded decision keeps <c>averageUnitCost</c>, <c>unitPrice</c>,
    /// <c>machinePrice</c> and the slot's suggested price fields visible to an Operator.
    /// </summary>
    private static readonly string[] RestrictedProductFields = ["inventoryValue"];

    private readonly ApiFactory _factory;

    public MachineAndProductFinancialRedactionApiTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task An_operator_is_answered_the_machine_list_with_every_financial_figure_null()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.OperatorObjectId).GetAsync(MachinesUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var machines = await ReadJsonAsync(response);
        Assert.Equal(2, machines.GetArrayLength());
        foreach (var machine in machines.EnumerateArray())
        {
            AssertNull(machine, RestrictedMachineFields);

            // The machine is still identified, which is what keeps the fleet usable for restocking.
            Assert.NotEqual(JsonValueKind.Null, machine.GetProperty("machineID").ValueKind);
            Assert.NotEqual(JsonValueKind.Null, machine.GetProperty("machineName").ValueKind);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_operator_is_answered_machine_detail_with_every_financial_figure_null(bool costed)
    {
        await _factory.SeedAsync();

        var response = await _factory
            .Authenticated(ApiFactory.OperatorObjectId)
            .GetAsync(costed ? CostedMachineUri : UncostedMachineUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNull(await ReadJsonAsync(response), RestrictedMachineFields);
    }

    /// <summary>
    /// Withheld, not zeroed: the figures a role that may see them is given include a real revenue, a
    /// real direct profit and a real profitability status, and - on the periods with no sale - a
    /// genuine <c>0</c>. An Operator is answered <c>null</c> for all of them, so a client cannot read
    /// "no sales this week" where the answer is "not yours to see".
    /// </summary>
    [Theory]
    [InlineData(ApiFactory.ManagerObjectId)]
    [InlineData(ApiFactory.OwnerObjectId)]
    public async Task A_role_that_holds_the_financial_capability_is_answered_the_machine_figures(string objectId)
    {
        await _factory.SeedAsync();
        var client = _factory.Authenticated(objectId);

        var costed = await ReadJsonAsync(await client.GetAsync(CostedMachineUri));
        var uncosted = await ReadJsonAsync(await client.GetAsync(UncostedMachineUri));

        Assert.Equal(ApiFactory.CostedSaleValue, costed.GetProperty("todayGrossRevenue").GetDecimal());
        Assert.Equal(ApiFactory.CostedSaleValue, costed.GetProperty("currentWeekGrossRevenue").GetDecimal());
        Assert.Equal(ApiFactory.CostedSaleValue, costed.GetProperty("monthToDateGrossRevenue").GetDecimal());
        Assert.Equal(0m, costed.GetProperty("lastWeekGrossRevenue").GetDecimal());
        Assert.NotEqual(JsonValueKind.Null, costed.GetProperty("todayDirectProfit").ValueKind);
        Assert.True(costed.GetProperty("todayDirectProfit").GetDecimal() > 0m);

        Assert.Equal(ApiFactory.UncostedSaleValue, uncosted.GetProperty("todayGrossRevenue").GetDecimal());
        Assert.Equal(
            "Unavailable: one or more completed sales have no persisted COGS.",
            uncosted.GetProperty("profitabilityStatus").GetString());
    }

    /// <summary>
    /// Redaction withholds the thirteen figures and changes nothing else: every other key of every
    /// machine in the list is byte-identical to the one a Manager is given over the same fleet.
    /// </summary>
    [Fact]
    public async Task An_operator_is_given_the_same_non_financial_machine_fields_as_a_manager()
    {
        await _factory.SeedAsync();

        var asOperator = await ReadJsonAsync(
            await _factory.Authenticated(ApiFactory.OperatorObjectId).GetAsync(MachinesUri));
        var asManager = await ReadJsonAsync(
            await _factory.Authenticated(ApiFactory.ManagerObjectId).GetAsync(MachinesUri));

        AssertIdenticalExcept(asManager, asOperator, RestrictedMachineFields);
    }

    /// <summary>
    /// Not renamed, not nested, not reported elsewhere in the payload: no seeded figure appears
    /// anywhere in an Operator's machine responses under any name.
    /// </summary>
    [Fact]
    public async Task An_operators_machine_responses_carry_no_financial_figure()
    {
        await _factory.SeedAsync();
        var client = _factory.Authenticated(ApiFactory.OperatorObjectId);

        var bodies = new[]
        {
            await (await client.GetAsync(MachinesUri)).Content.ReadAsStringAsync(),
            await (await client.GetAsync(CostedMachineUri)).Content.ReadAsStringAsync(),
            await (await client.GetAsync(UncostedMachineUri)).Content.ReadAsStringAsync(),
        };

        foreach (var body in bodies)
        {
            Assert.DoesNotContain(Literal(ApiFactory.CostedSaleValue), body, StringComparison.Ordinal);
            Assert.DoesNotContain(Literal(ApiFactory.UncostedSaleValue), body, StringComparison.Ordinal);
            Assert.DoesNotContain(Literal(ApiFactory.CostOfGoodsSold), body, StringComparison.Ordinal);
            Assert.DoesNotContain("COGS", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_operator_is_answered_the_product_catalogue_without_its_inventory_value(bool list)
    {
        await _factory.SeedAsync();

        var response = await _factory
            .Authenticated(ApiFactory.OperatorObjectId)
            .GetAsync(list ? ProductsUri : ProductUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);
        var product = list ? Single(json) : json;
        AssertNull(product, RestrictedProductFields);

        // Unit cost, selling price and the stock figures stay visible: the recorded decision keeps
        // them, and an Operator restocks from them.
        Assert.Equal(ApiFactory.AverageUnitCost, product.GetProperty("averageUnitCost").GetDecimal());
        Assert.Equal(ApiFactory.UnitPrice, product.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(ApiFactory.CostingQuantity, product.GetProperty("costingQuantity").GetInt32());
        Assert.Equal(ApiFactory.QuantityInStock, product.GetProperty("quantityInStock").GetInt32());
    }

    [Fact]
    public async Task An_operator_is_answered_a_machines_product_list_without_its_inventory_value()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.OperatorObjectId).GetAsync(MachineProductsUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var slot = Single(await ReadJsonAsync(response));
        AssertNull(slot, RestrictedProductFields);

        // The slot's own live selling price is a price, not a financial figure, and stays visible.
        Assert.Equal(ApiFactory.MachinePrice, slot.GetProperty("machinePrice").GetDecimal());
    }

    [Theory]
    [InlineData(ApiFactory.ManagerObjectId)]
    [InlineData(ApiFactory.OwnerObjectId)]
    public async Task A_role_that_holds_the_financial_capability_is_answered_the_inventory_value(string objectId)
    {
        await _factory.SeedAsync();
        var client = _factory.Authenticated(objectId);

        Assert.Equal(
            ApiFactory.InventoryValue,
            Single(await ReadJsonAsync(await client.GetAsync(ProductsUri)))
                .GetProperty("inventoryValue").GetDecimal());
        Assert.Equal(
            ApiFactory.InventoryValue,
            (await ReadJsonAsync(await client.GetAsync(ProductUri))).GetProperty("inventoryValue").GetDecimal());
        Assert.Equal(
            ApiFactory.InventoryValue,
            Single(await ReadJsonAsync(await client.GetAsync(MachineProductsUri)))
                .GetProperty("inventoryValue").GetDecimal());
    }

    /// <summary>
    /// The same whole-payload comparison as the machine list, for both product views: one field is
    /// withheld and the rest of the catalogue - including every nested category, supplier and stock
    /// movement - is exactly what a Manager reads.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_operator_is_given_the_same_non_financial_product_fields_as_a_manager(bool machineSlot)
    {
        await _factory.SeedAsync();
        var uri = machineSlot ? MachineProductsUri : ProductsUri;

        var asOperator = await ReadJsonAsync(await _factory.Authenticated(ApiFactory.OperatorObjectId).GetAsync(uri));
        var asManager = await ReadJsonAsync(await _factory.Authenticated(ApiFactory.ManagerObjectId).GetAsync(uri));

        AssertIdenticalExcept(asManager, asOperator, RestrictedProductFields);
    }

    [Fact]
    public async Task An_operators_product_responses_carry_no_inventory_value()
    {
        await _factory.SeedAsync();
        var client = _factory.Authenticated(ApiFactory.OperatorObjectId);

        foreach (var uri in new[] { ProductsUri, ProductUri, MachineProductsUri })
        {
            var body = await (await client.GetAsync(uri)).Content.ReadAsStringAsync();

            Assert.DoesNotContain(Literal(ApiFactory.InventoryValue), body, StringComparison.Ordinal);
        }
    }

    private static void AssertNull(JsonElement element, IEnumerable<string> fields)
    {
        foreach (var field in fields)
        {
            Assert.Equal(JsonValueKind.Null, element.GetProperty(field).ValueKind);
        }
    }

    /// <summary>
    /// Every key of <paramref name="redacted"/> matches <paramref name="full"/> except
    /// <paramref name="withheld"/>, which must be null on the redacted side and carry a real value
    /// on at least one element of the full side - so a passing comparison cannot be two empty
    /// payloads agreeing with each other.
    /// </summary>
    private static void AssertIdenticalExcept(
        JsonElement full, JsonElement redacted, IReadOnlyCollection<string> withheld)
    {
        var fullElements = Elements(full);
        var redactedElements = Elements(redacted);
        Assert.Equal(fullElements.Count, redactedElements.Count);
        Assert.NotEmpty(fullElements);

        var withheldValuesSeen = 0;
        for (var index = 0; index < fullElements.Count; index++)
        {
            var expected = fullElements[index];
            var actual = redactedElements[index];
            Assert.Equal(
                expected.EnumerateObject().Select(property => property.Name),
                actual.EnumerateObject().Select(property => property.Name));

            foreach (var property in expected.EnumerateObject())
            {
                if (withheld.Contains(property.Name, StringComparer.Ordinal))
                {
                    Assert.Equal(JsonValueKind.Null, actual.GetProperty(property.Name).ValueKind);
                    if (property.Value.ValueKind != JsonValueKind.Null)
                    {
                        withheldValuesSeen++;
                    }

                    continue;
                }

                Assert.Equal(property.Value.GetRawText(), actual.GetProperty(property.Name).GetRawText());
            }
        }

        Assert.True(withheldValuesSeen > 0, "The unredacted payload carried no withheld figure to compare.");
    }

    private static List<JsonElement> Elements(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? [.. element.EnumerateArray()] : [element];

    private static JsonElement Single(JsonElement array) => Assert.Single(array.EnumerateArray());

    private static string Literal(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string DirectoryTenantId = "88888888-8888-8888-8888-888888888888";
        public const string OwnerObjectId = "11111111-1111-1111-1111-111111111111";
        public const string ManagerObjectId = "22222222-2222-2222-2222-222222222222";
        public const string OperatorObjectId = "33333333-3333-3333-3333-333333333333";

        /// <summary>The site-mapped machine whose completed sale carries persisted COGS.</summary>
        public const long CostedMachineId = 55;

        /// <summary>The unmapped machine whose completed sale has no COGS.</summary>
        public const long UncostedMachineId = 56;

        public const long ProductId = 100;

        public const decimal InventoryValue = 777.77m;
        public const decimal AverageUnitCost = 1.23m;
        public const decimal UnitPrice = 3.57m;
        public const decimal MachinePrice = 4.65m;
        public const int CostingQuantity = 7;
        public const int QuantityInStock = 12;

        public const decimal CostedSaleValue = 123.45m;
        public const decimal UncostedSaleValue = 67.89m;
        public const decimal CostOfGoodsSold = 40.08m;

        private const int BusinessId = 1;
        private const long SiteId = 9;

        /// <summary>
        /// 15 October 2026, 15:00 in Australia/Sydney. The seeded sale is three hours earlier on the
        /// same business day, so it falls inside today, the current business week and the month to
        /// date on every run, and inside no other period the dashboard reports.
        /// </summary>
        private static readonly DateTime NowUtc = new(2026, 10, 15, 4, 0, 0, DateTimeKind.Utc);

        private static readonly DateTime SaleUtc = new(2026, 10, 15, 1, 0, 0, DateTimeKind.Utc);

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
                UnitPrice = UnitPrice,
                AverageUnitCost = AverageUnitCost,
                CostingQuantity = CostingQuantity,
                InventoryValue = InventoryValue,
                QuantityInStock = QuantityInStock,
                LowStockThreshold = 5,
                RestockTo = 24,
                IsActive = true,
                CreatedAt = CreatedAt,
                UpdatedAt = CreatedAt,
            });

            // An effective processing fee rate, so the costed machine's direct profit is a real
            // figure rather than the missing-rate fallback status.
            db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate
            {
                BusinessId = BusinessId,
                EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                FeeExGst = 0.03m,
                CreatedAt = CreatedAt,
            });

            db.NayaxSales.AddRange(
                CompletedCardSale(1, CostedMachineId, CostedSaleValue, CostOfGoodsSold),
                CompletedCardSale(2, UncostedMachineId, UncostedSaleValue, null));

            await db.SaveChangesAsync();
            _seeded = true;
        }

        private static NayaxSales CompletedCardSale(
            long transactionId, long machineId, decimal settlementValue, decimal? costOfGoodsSold) =>
            new()
            {
                BusinessId = BusinessId,
                TransactionID = transactionId,
                MachineID = machineId,
                NayaxProductId = ProductId,
                SettlementValue = settlementValue,
                PaymentMethod = "Credit",
                MachineAuthorizationTime = SaleUtc,
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                CostOfGoodsSold = costOfGoodsSold,
                UnitCostAtSale = costOfGoodsSold,
                CostingStatus = costOfGoodsSold.HasValue
                    ? SaleCostingStatus.Costed
                    : SaleCostingStatus.Pending,
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
        /// The live fleet: a site-mapped machine and an unmapped one, and one slot on the mapped
        /// machine dispensing the seeded catalogue product.
        /// </summary>
        private static INayaxLynxClient Fleet()
        {
            var costed = new NayaxMachine
            {
                MachineID = CostedMachineId,
                MachineName = "Pavillion Left",
                MachineNumber = "PL-1",
                CustomerID = SiteId,
                ActorID = SiteId,
            };
            var uncosted = new NayaxMachine
            {
                MachineID = UncostedMachineId,
                MachineName = "Warehouse Spare",
                MachineNumber = "WS-1",
            };

            var nayax = new Mock<INayaxLynxClient>();
            nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([costed, uncosted]);
            nayax.Setup(x => x.GetMachineAsync(CostedMachineId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(costed);
            nayax.Setup(x => x.GetMachineAsync(UncostedMachineId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(uncosted);
            nayax.Setup(x => x.GetMachineProductsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([new NayaxMachineProduct
                {
                    NayaxProductID = ProductId,
                    MachineID = CostedMachineId,
                    MDBCode = 13,
                    PAR = 10,
                    MissingStockByMDB = 4,
                    RetailPrice = MachinePrice,
                    CommissionValue = 0.5m,
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

                // The real Australia/Sydney business calendar over a pinned instant, so the business
                // day, week and month the figures are summed over are the same on every run.
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
