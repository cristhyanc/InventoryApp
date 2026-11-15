using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Inventory.Application.Nayax;
using Inventory.Domain.Stock;
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

// The request's reason and the persisted movement's reason, which the two layers declare under the
// same name (issue #282's mirrored vocabulary).
using DomainReason = Inventory.Domain.Stock.StockAdjustmentReason;
using PersistedReason = Inventory.Infrastructure.Models.StockAdjustmentReason;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// <c>POST /api/products/{productId}/stock</c> against the sign each reason is allowed to move
/// stock in (issue #1242), through the real request pipeline: JSON model binding, authentication,
/// the required scope, the <c>Inventory.Operate</c> capability policy, the controller, the use case,
/// <c>DomainExceptionHandler</c> and relational SQLite with the real migration history.
///
/// <para>What only this level can prove is that a refused combination is answered <c>400</c> rather
/// than a <c>500</c> and <b>writes nothing</b>: the refusal happens in the use case before the
/// adapter opens its write transaction, so "nothing was written" has to be read back from the
/// database. The two recorded movements beside the refusals are the control - the same endpoint over
/// the same database does record the sign each reason expects, so the empty tables are a fact about
/// the refusal and not about the harness.</para>
/// </summary>
public sealed class ManualStockAdjustmentSignApiTests : IClassFixture<ManualStockAdjustmentSignApiTests.ApiFactory>
{
    private readonly ApiFactory _factory;

    public ManualStockAdjustmentSignApiTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// Every reason × sign combination the policy refuses, with the sentence it refuses it with.
    /// The accepted combinations are the two recorded movements below.
    /// </summary>
    public static TheoryData<DomainReason, int, string> RefusedCombinations()
    {
        var data = new TheoryData<DomainReason, int, string>();

        foreach (var reason in new[] { DomainReason.Damaged, DomainReason.Expired, DomainReason.Sale })
        {
            data.Add(reason, 5, ManualStockAdjustmentPolicy.RemovesStockMessage(reason));
            data.Add(reason, 0, ManualStockAdjustmentPolicy.RemovesStockMessage(reason));
        }

        data.Add(DomainReason.Correction, 5, ManualStockAdjustmentPolicy.CorrectionRemovesStockMessage);
        data.Add(DomainReason.Correction, 0, ManualStockAdjustmentPolicy.CorrectionRemovesStockMessage);
        data.Add(DomainReason.Restock, 0, ManualStockAdjustmentPolicy.RestockAddsStockMessage);
        data.Add(DomainReason.Restock, -5, ManualStockAdjustmentPolicy.RestockAddsStockMessage);

        return data;
    }

    [Theory]
    [MemberData(nameof(RefusedCombinations))]
    public async Task A_quantity_in_the_wrong_direction_is_refused_and_records_nothing(
        DomainReason reason, int quantityChange, string refusal)
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated().PostAsJsonAsync(
            ApiFactory.RefusedProductStockUri,
            new
            {
                quantityChange,
                reason = (int)reason,
                notes = "Issue #1242",
                // A cost is sent with the refused Restock as the form would send it, so the sign is
                // what the refusal is about.
                unitCost = reason == DomainReason.Restock ? 1.50m : (decimal?)null,
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(refusal, problem.GetProperty("message").GetString());

        await using var db = _factory.Database();
        var movements = await db.StockAdjustments
            .Where(movement => movement.ProductId == ApiFactory.RefusedProductId)
            .ToListAsync();
        Assert.Equal(PersistedReason.Restock, Assert.Single(movements).Reason);
        var product = await db.Products.SingleAsync(candidate => candidate.Id == ApiFactory.RefusedProductId);
        Assert.Equal(ApiFactory.StartingStock, product.QuantityInStock);
    }

    /// <summary>
    /// The control for every write-off refusal: the natural quantity typed the way the form now
    /// sends it is recorded, removes storage stock and carries the product's own average cost, so
    /// the write-off's loss is recorded rather than lost.
    /// </summary>
    [Fact]
    public async Task A_negative_write_off_is_recorded_with_its_cost()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated().PostAsJsonAsync(
            ApiFactory.WriteOffProductStockUri,
            new { quantityChange = -2, reason = (int)DomainReason.Damaged, notes = "Crushed" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var movement = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(-2, movement.GetProperty("quantityChange").GetInt32());
        Assert.Equal(ApiFactory.OpeningUnitCost, movement.GetProperty("unitCost").GetDecimal());

        await using var db = _factory.Database();
        var recorded = await db.StockAdjustments.SingleAsync(adjustment =>
            adjustment.ProductId == ApiFactory.WriteOffProductId
            && adjustment.Reason == PersistedReason.Damaged);
        Assert.Equal(-2, recorded.QuantityChange);
        var product = await db.Products.SingleAsync(candidate => candidate.Id == ApiFactory.WriteOffProductId);
        Assert.Equal(ApiFactory.StartingStock - 2, product.QuantityInStock);
        Assert.Equal((int?)(ApiFactory.StartingStock - 2), product.CostingQuantity);
        Assert.Equal((decimal?)((ApiFactory.StartingStock - 2) * ApiFactory.OpeningUnitCost), product.InventoryValue);
    }

    /// <summary>The control for the Restock refusals: a positive Restock with a cost still records.</summary>
    [Fact]
    public async Task A_positive_restock_with_a_cost_is_recorded()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated().PostAsJsonAsync(
            ApiFactory.RestockProductStockUri,
            new { quantityChange = 4, reason = (int)DomainReason.Restock, unitCost = 2.00m });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = _factory.Database();
        var recorded = await db.StockAdjustments.SingleAsync(adjustment =>
            adjustment.ProductId == ApiFactory.RestockProductId
            && adjustment.QuantityChange == 4);
        Assert.Equal(PersistedReason.Restock, recorded.Reason);
        var product = await db.Products.SingleAsync(candidate => candidate.Id == ApiFactory.RestockProductId);
        Assert.Equal(ApiFactory.StartingStock + 4, product.QuantityInStock);
        Assert.Equal((int?)(ApiFactory.StartingStock + 4), product.CostingQuantity);
    }

    /// <summary>
    /// Hosts the real API over one in-memory SQLite database holding a single business, an Owner of
    /// it (the role holds <c>Inventory.Operate</c>) and three stocked products: one every refusal is
    /// made against, and one per recorded movement, so no test's writes decide another's. The Nayax
    /// client is a strict double with no setup: a manual adjustment reads nothing from a provider.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const long RefusedProductId = 100;
        public const long WriteOffProductId = 200;
        public const long RestockProductId = 300;
        public const int StartingStock = 10;
        public const decimal OpeningUnitCost = 1.50m;

        public static readonly string RefusedProductStockUri = $"/api/products/{RefusedProductId}/stock";
        public static readonly string WriteOffProductStockUri = $"/api/products/{WriteOffProductId}/stock";
        public static readonly string RestockProductStockUri = $"/api/products/{RestockProductId}/stock";

        private const string DirectoryTenantId = "88888888-8888-8888-8888-888888888888";
        private const string OwnerObjectId = "44444444-4444-4444-4444-444444444444";
        private const int BusinessId = 1;

        private static readonly DateTime CreatedAt = new(2026, 3, 10, 4, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private bool _seeded;

        public ApiFactory() => _connection.Open();

        /// <summary>An unrestricted context for reading what a request did, outside its boundary.</summary>
        public AppDbContext Database()
        {
            // Resolving Services builds and starts the host, which applies the migrations this
            // context then reads through.
            _ = Services;
            return TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        }

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

            db.Products.AddRange(
                StockedProduct(RefusedProductId, "Coke"),
                StockedProduct(WriteOffProductId, "Chips"),
                StockedProduct(RestockProductId, "Water"));

            // The stock each product holds has to be the sum of its movements: every stock write
            // rebuilds the product's cost from its history, and a quantity with no history behind it
            // is the data quality failure that rebuild exists to report.
            db.StockAdjustments.AddRange(
                OpeningStock(RefusedProductId), OpeningStock(WriteOffProductId), OpeningStock(RestockProductId));

            await db.SaveChangesAsync();
            _seeded = true;
        }

        private static Product StockedProduct(long id, string name) => new()
        {
            Id = id,
            BusinessId = BusinessId,
            Name = name,
            QuantityInStock = StartingStock,
        };

        private static StockAdjustment OpeningStock(long productId) => new()
        {
            BusinessId = BusinessId,
            ProductId = productId,
            QuantityChange = StartingStock,
            QuantityAfter = StartingStock,
            Reason = PersistedReason.Restock,
            UnitCost = OpeningUnitCost,
            EffectiveAt = CreatedAt,
            CreatedAt = CreatedAt,
        };

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
            {
                services.AddAuthentication(TestActorAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestActorAuthenticationHandler>(
                        TestActorAuthenticationHandler.SchemeName, _ => { });

                services.AddScoped(_ => new Mock<INayaxLynxClient>(MockBehavior.Strict).Object);
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
