using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Inventory.Application.Nayax;
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
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The exact JSON shape <c>GET /api/dashboard/summary</c> publishes to each role (issue #527),
/// through the real request pipeline: authentication, the delegated scope, the
/// <c>Dashboard.View</c> capability policy, <c>BusinessScopeMiddleware</c>, the controller and
/// relational SQLite with the real migration history.
///
/// Three members of one business hold the three roles, and the business holds one completed sale
/// and one costed product, so the figures a caller may not see really exist in the database. What
/// these tests assert is the <em>property set</em> of the response, because the contract of this
/// slice is absence: a caller without <c>Dashboard.ViewFinancials</c> must not receive
/// <c>salesThisWeek</c> or <c>inventory.inventoryValueAtCost</c> at all - not as <c>null</c>, not
/// as <c>0</c>, which would both read as a real, known figure.
/// </summary>
public sealed class DashboardSummaryRedactionApiTests : IClassFixture<DashboardSummaryRedactionApiTests.ApiFactory>
{
    private const string Uri = "/api/dashboard/summary";

    /// <summary>The week-to-date revenue the seeded completed sale produces.</summary>
    private const decimal SeededSales = 1234.56m;

    /// <summary>The inventory valuation the seeded product produces.</summary>
    private const decimal SeededInventoryValue = 777.77m;

    private readonly ApiFactory _factory;

    public DashboardSummaryRedactionApiTests(ApiFactory factory) => _factory = factory;

    /// <summary>The roles that hold <c>Dashboard.ViewFinancials</c> in the agreed table of #328.</summary>
    public static TheoryData<string> RolesThatSeeFinancials => new() { "Manager", "Owner" };

    /// <summary>
    /// The redacted shape, asserted as the complete set of properties an Operator receives. The
    /// financial figures are absent, and everything else the summary reports is untouched.
    /// </summary>
    [Fact]
    public async Task An_operator_is_answered_the_summary_without_its_financial_figures()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.OperatorObjectId).GetAsync(Uri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);

        Assert.Equal(
            ["asOfUtc", "businessDate", "inventory", "needsOrdering", "needsRefill"],
            PropertyNames(json));
        Assert.Equal(
            ["isInventoryValueComplete", "productCount", "productsWithUnknownCost", "unitsInStorage"],
            PropertyNames(json.GetProperty("inventory")));
    }

    /// <summary>
    /// The figures themselves never reach an Operator, in any property, under any name: the seeded
    /// week's revenue and the seeded valuation are both absent from the response body.
    /// </summary>
    [Fact]
    public async Task An_operators_response_body_contains_neither_financial_figure()
    {
        await _factory.SeedAsync();

        var body = await (await _factory.Authenticated(ApiFactory.OperatorObjectId).GetAsync(Uri))
            .Content.ReadAsStringAsync();

        Assert.DoesNotContain("salesThisWeek", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("inventoryValueAtCost", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            SeededSales.ToString(CultureInfo.InvariantCulture), body, StringComparison.Ordinal);
        Assert.DoesNotContain(
            SeededInventoryValue.ToString(CultureInfo.InvariantCulture), body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The non-financial cards are not merely present for an Operator, they are the same figures a
    /// Manager is given: redaction removes the financial figures and changes nothing else.
    /// </summary>
    [Fact]
    public async Task An_operator_is_given_the_same_non_financial_figures_as_a_manager()
    {
        await _factory.SeedAsync();

        var redacted = await ReadJsonAsync(
            await _factory.Authenticated(ApiFactory.OperatorObjectId).GetAsync(Uri));
        var full = await ReadJsonAsync(await _factory.Authenticated(ApiFactory.ManagerObjectId).GetAsync(Uri));

        Assert.Equal(full.GetProperty("needsRefill").ToString(), redacted.GetProperty("needsRefill").ToString());
        Assert.Equal(full.GetProperty("needsOrdering").ToString(), redacted.GetProperty("needsOrdering").ToString());
        foreach (var property in PropertyNames(redacted.GetProperty("inventory")))
        {
            Assert.Equal(
                full.GetProperty("inventory").GetProperty(property).ToString(),
                redacted.GetProperty("inventory").GetProperty(property).ToString());
        }
    }

    /// <summary>
    /// The full shape, for each role that holds <c>Dashboard.ViewFinancials</c>: every property the
    /// endpoint has always published, including both financial figures with their real values.
    /// </summary>
    [Theory]
    [MemberData(nameof(RolesThatSeeFinancials))]
    public async Task A_role_that_holds_the_financial_capability_is_answered_the_whole_summary(string role)
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.ObjectIdOf(role)).GetAsync(Uri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);

        Assert.Equal(
            ["asOfUtc", "businessDate", "inventory", "needsOrdering", "needsRefill", "salesThisWeek"],
            PropertyNames(json));
        Assert.Equal(
            [
                "inventoryValueAtCost", "isInventoryValueComplete", "productCount",
                "productsWithUnknownCost", "unitsInStorage",
            ],
            PropertyNames(json.GetProperty("inventory")));
        Assert.Equal(
            [
                "changeAmount", "changePercent", "comparisonNote", "comparisonPeriod",
                "comparisonSales", "comparisonTransactionCount", "isComparisonAvailable", "period",
                "sales", "transactionCount",
            ],
            PropertyNames(json.GetProperty("salesThisWeek")));

        Assert.Equal(SeededSales, json.GetProperty("salesThisWeek").GetProperty("sales").GetDecimal());
        Assert.Equal(
            SeededInventoryValue,
            json.GetProperty("inventory").GetProperty("inventoryValueAtCost").GetDecimal());
        Assert.True(json.GetProperty("inventory").GetProperty("isInventoryValueComplete").GetBoolean());
    }

    private static IEnumerable<string> PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal);

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string DirectoryTenantId = "99999999-9999-9999-9999-999999999999";
        public const string OwnerObjectId = "11111111-1111-1111-1111-111111111111";
        public const string ManagerObjectId = "22222222-2222-2222-2222-222222222222";
        public const string OperatorObjectId = "33333333-3333-3333-3333-333333333333";

        private const int SeededBusinessId = 1;

        private static readonly DateTime CreatedAt = new(2026, 10, 11, 4, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private bool _seeded;

        public ApiFactory() => _connection.Open();

        public static string ObjectIdOf(string role) => role switch
        {
            "Owner" => OwnerObjectId,
            "Manager" => ManagerObjectId,
            "Operator" => OperatorObjectId,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role."),
        };

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
                Id = SeededBusinessId,
                Name = "Vending Co",
                TimeZoneId = "Australia/Sydney",
                CreatedAtUtc = CreatedAt,
            });

            db.BusinessMemberships.AddRange(
                Membership(OwnerObjectId, BusinessRole.Owner),
                Membership(ManagerObjectId, BusinessRole.Manager),
                Membership(OperatorObjectId, BusinessRole.Operator));

            // One costed product, so the valuation is a known figure rather than an unavailable one.
            db.Products.Add(new Product
            {
                BusinessId = SeededBusinessId,
                Name = "Coke",
                QuantityInStock = 12,
                LowStockThreshold = 0,
                RestockTo = 0,
                InventoryValue = SeededInventoryValue,
                IsActive = true,
            });

            // One completed sale inside the current business week, whichever week the test runs in.
            db.NayaxSales.Add(new NayaxSales
            {
                BusinessId = SeededBusinessId,
                TransactionID = 1,
                MachineID = 10,
                SettlementValue = SeededSales,
                PaymentMethod = "Credit",
                MachineAuthorizationTime = DateTime.UtcNow,
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
            });

            await db.SaveChangesAsync();
            _seeded = true;
        }

        private static BusinessMembership Membership(string objectId, BusinessRole role) =>
            new()
            {
                BusinessId = SeededBusinessId,
                DirectoryTenantId = DirectoryTenantId,
                ObjectId = objectId,
                Role = role,
                CreatedAtUtc = CreatedAt,
            };

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Testing applies pending migrations at startup, so the schema under these tests is the
            // migration history rather than a model snapshot.
            builder.UseEnvironment("Testing");

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
            {
                services.AddAuthentication(TestActorAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestActorAuthenticationHandler>(
                        TestActorAuthenticationHandler.SchemeName, _ => { });

                // An empty live fleet, so the refill and ordering cards are answered without a Nayax
                // connection. This slice is about which figures the response carries, not about the
                // fleet read, which has its own tests.
                var nayax = new Mock<INayaxLynxClient>();
                nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
                services.AddScoped(_ => nayax.Object);
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
    /// request headers, so membership resolution, the capability policy and the scope requirement
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
