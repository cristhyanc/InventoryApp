using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
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
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The published surface of the Dashboard's unimported-provider-product read (issue #1352), through
/// the real request pipeline: authentication, the required scope, the <c>Imports.Run</c> capability
/// policy, <c>BusinessScopeMiddleware</c>, the controller, the Application read and relational
/// SQLite with the real migration history.
///
/// <para>Who may read it is the part worth pinning, because it is a security decision rather than a
/// shape. The alert's only action is to open Import products, so it carries the capability that
/// opens it: a Manager and an Owner are answered, and an Operator - who has no business running the
/// import - is refused with the ordinary bodiless <c>403</c> and therefore never sees the alert. The
/// read's own rules (which sales qualify, the thirty Sydney dates, the distinct count, the
/// linked-product case and tenant isolation) are asserted against a relational database in
/// <c>InventoryApi.Tests.Application.CatalogueReview.UnimportedProviderProductAttentionTests</c>.</para>
/// </summary>
public sealed class CatalogueReviewAttentionApiTests
    : IClassFixture<CatalogueReviewAttentionApiTests.ApiFactory>
{
    private const string Uri = "/api/catalogue-review/attention";

    private readonly ApiFactory _factory;

    public CatalogueReviewAttentionApiTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// The acceptance criterion: an Operator is refused, so the Dashboard alert is unreachable for
    /// that role however the frontend is composed.
    /// </summary>
    [Fact]
    public async Task An_operator_is_refused()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.OperatorObjectId).GetAsync(Uri);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A Manager and an Owner are answered the same figures: the count of distinct provider products
    /// that are selling and are not in the business's products, and the most recently sold of them by
    /// the name its own sale carried.
    /// </summary>
    [Theory]
    [InlineData(ApiFactory.ManagerObjectId)]
    [InlineData(ApiFactory.OwnerObjectId)]
    public async Task A_manager_and_an_owner_are_answered_the_count_and_its_examples(string objectId)
    {
        await _factory.SeedAsync();

        var body = await BodyAsync(objectId);

        Assert.Equal(["examples", "unimportedProductCount"], Properties(body));
        Assert.Equal(2, body.GetProperty("unimportedProductCount").GetInt32());

        var examples = body.GetProperty("examples").EnumerateArray().ToList();
        Assert.Equal(2, examples.Count);
        Assert.All(examples, example => Assert.Equal(["nayaxProductId", "productName"], Properties(example)));
        Assert.Equal(
            NayaxExternalId.From(ApiFactory.MonsterProviderProductId),
            examples[0].GetProperty("nayaxProductId").GetString());
        Assert.Equal("Monster Original", examples[0].GetProperty("productName").GetString());
    }

    /// <summary>
    /// It carries no amount. The seeded sales have deliberately distinctive settlement values, so
    /// "no figure from a sale is anywhere in this body" is an exact substring search rather than a
    /// search for a digit that could be an id or a date.
    /// </summary>
    [Fact]
    public async Task The_response_carries_no_amount_price_cost_or_quantity()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.ManagerObjectId).GetAsync(Uri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("1234.51", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("2345.62", raw, StringComparison.Ordinal);
    }

    /// <summary>An anonymous request never reaches the read.</summary>
    [Fact]
    public async Task An_anonymous_request_is_unauthorized()
    {
        await _factory.SeedAsync();

        var response = await _factory.CreateClient().GetAsync(Uri);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<JsonElement> BodyAsync(string objectId)
    {
        var response = await _factory.Authenticated(objectId).GetAsync(Uri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static string[] Properties(JsonElement element) =>
        [.. element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string DirectoryTenantId = "77777777-7777-7777-7777-777777777777";
        public const string OwnerObjectId = "11111111-1111-1111-1111-111111111111";
        public const string ManagerObjectId = "22222222-2222-2222-2222-222222222222";
        public const string OperatorObjectId = "33333333-3333-3333-3333-333333333333";

        public const long MonsterProviderProductId = 901;
        public const long CokeZeroProviderProductId = 902;
        public const long ImportedProviderProductId = 903;

        private const int BusinessId = 1;

        private static readonly DateTime CreatedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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

        /// <summary>
        /// One business, the three roles, and three unresolved Nayax sales in the window: two of
        /// provider products nothing local carries, and one of a provider product an existing product
        /// already carries the id of, which is therefore not waiting on an import.
        ///
        /// <para>The instants are relative to the real clock, because the request's own window is,
        /// and they are hours rather than seconds away from it so a run at a Sydney midnight cannot
        /// move one of them out of the thirty dates.</para>
        /// </summary>
        public async Task SeedAsync()
        {
            await using var db = Database();
            if (_seeded)
            {
                return;
            }

            var nowUtc = DateTime.UtcNow;

            db.Businesses.Add(new Business
            {
                Id = BusinessId,
                Name = "Vending Co",
                TimeZoneId = "Australia/Sydney",
                Status = BusinessStatus.Active,
                SalesProvider = ExternalProvider.Nayax,
                CreatedAtUtc = CreatedAt,
            });

            db.BusinessMemberships.AddRange(
                Membership(OwnerObjectId, BusinessRole.Owner),
                Membership(ManagerObjectId, BusinessRole.Manager),
                Membership(OperatorObjectId, BusinessRole.Operator));

            db.Products.Add(new Product
            {
                BusinessId = BusinessId,
                Name = "Spring Water 600ml",
                ExternalProductId = NayaxExternalId.From(ImportedProviderProductId),
                RestockTo = 0,
                CreatedAt = CreatedAt,
            });

            db.NayaxSales.AddRange(
                Unresolved(9001, MonsterProviderProductId, "Monster Original", 1234.51m, nowUtc.AddHours(-2)),
                Unresolved(9002, CokeZeroProviderProductId, "Coke Zero 600ml", 2345.62m, nowUtc.AddHours(-26)),
                Unresolved(9003, ImportedProviderProductId, "Spring Water 600ml", 345.73m, nowUtc.AddHours(-3)));

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

        private static NayaxSales Unresolved(
            long transactionId,
            long nayaxProductId,
            string productName,
            decimal settlementValue,
            DateTime authorizedAtUtc) =>
            new()
            {
                BusinessId = BusinessId,
                TransactionID = transactionId,
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                MachineID = 4200001,
                MachineName = "Pavillion Left",
                ProductId = null,
                Provider = ExternalProvider.Nayax,
                NayaxProductId = nayaxProductId,
                ProviderProductIdIsRaw = true,
                ProductName = productName,
                SettlementValue = settlementValue,
                PaymentMethod = "Credit Card",
                MachineAuthorizationTime = authorizedAtUtc,
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
                var tenantId = Request.Headers[DirectoryTenantIdHeader].ToString();
                var objectId = Request.Headers[ObjectIdHeader].ToString();
                if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(objectId))
                {
                    return Task.FromResult(AuthenticateResult.NoResult());
                }

                var claims = new List<Claim>
                {
                    new("tid", tenantId),
                    new("oid", objectId),
                };

                var scope = Request.Headers[ScopeHeader].ToString();
                if (!string.IsNullOrWhiteSpace(scope))
                {
                    claims.Add(new Claim("scp", scope));
                }

                var identity = new ClaimsIdentity(claims, SchemeName);
                return Task.FromResult(AuthenticateResult.Success(
                    new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
            }
        }
    }
}
