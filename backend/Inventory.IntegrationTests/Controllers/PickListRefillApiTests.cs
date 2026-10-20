using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Inventory.Application.Nayax;
using Inventory.Domain.Stock;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using InventoryApi.Adapters.Mapping;
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

// The persisted movement's reason, which the Domain declares under the same name.
using StockAdjustmentReason = Inventory.Infrastructure.Models.StockAdjustmentReason;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// <c>POST /api/pick-list/refills</c> (issue #743) through the real request pipeline: JSON model
/// binding, authentication, the required scope, the capability policies, the controller, the use
/// case and relational SQLite.
///
/// <para>What only this level can prove is what the <em>body on the wire</em> does, which is why it
/// exists beside <c>PickListControllerTests</c>: those call the controller with objects a compiler
/// built, so they cannot produce a row element that is missing. JSON can - <c>"rows": [null]</c> is
/// a valid array whatever the element type declares - and before the review repair that element
/// reached the controller's projection and was dereferenced, answering a <c>500</c> where the
/// contract promises a per-row <c>400</c>. The recorded pick beside it is the control: the same
/// endpoint over the same database does record a valid body, so "nothing was written" is a fact
/// about the refusal and not about the harness.</para>
/// </summary>
public sealed class PickListRefillApiTests : IClassFixture<PickListRefillApiTests.ApiFactory>
{
    private const string Uri = "/api/pick-list/refills";

    private readonly ApiFactory _factory;

    public PickListRefillApiTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// A missing element among otherwise valid rows: <c>400</c> with the refusal on the line the
    /// caller sent it on, and the valid row beside it written nowhere, because one refusal refuses
    /// the whole batch.
    /// </summary>
    [Fact]
    public async Task A_row_sent_as_a_missing_value_is_refused_per_row_and_records_nothing()
    {
        await _factory.SeedAsync();
        const string key = "f0c0a1c0-0000-4000-8000-00000000aaaa";

        var response = await _factory.Authenticated().PostAsync(
            Uri,
            Json($$"""
                {
                  "idempotencyKey": "{{key}}",
                  "rows": [{ "productId": {{ApiFactory.RefusedProductId}}, "quantityTaken": 2 }, null]
                }
                """));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var errors = JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement
            .GetProperty("errors");
        Assert.Equal(
            PickListRefillPolicy.MissingRowMessage,
            Assert.Single(errors.GetProperty(PickListRefillResponseMapper.RowKey(1)).EnumerateArray())
                .GetString());
        Assert.False(errors.TryGetProperty(PickListRefillResponseMapper.RowKey(0), out _));

        await using var db = _factory.Database();
        Assert.Empty(db.PickListRefills.Where(refill => refill.IdempotencyKey == key));
        Assert.Empty(db.StockAdjustments.Where(movement =>
            movement.ProductId == ApiFactory.RefusedProductId
            && movement.Reason == StockAdjustmentReason.MachineRefill));
        Assert.Equal(
            ApiFactory.StartingStock,
            (await db.Products.SingleAsync(product => product.Id == ApiFactory.RefusedProductId)).QuantityInStock);
    }

    /// <summary>
    /// The control: the same endpoint, the same database and a body with every row present records
    /// the pick - one <c>MachineRefill</c> movement, the storage quantity down by what was taken,
    /// and <c>replayed</c> false.
    /// </summary>
    [Fact]
    public async Task A_body_with_every_row_present_is_recorded()
    {
        await _factory.SeedAsync();
        const string key = "f0c0a1c0-0000-4000-8000-00000000bbbb";

        var response = await _factory.Authenticated().PostAsync(
            Uri,
            Json($$"""
                {
                  "idempotencyKey": "{{key}}",
                  "rows": [{ "productId": {{ApiFactory.RecordedProductId}}, "quantityTaken": 4 }]
                }
                """));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.GetProperty("replayed").GetBoolean());

        await using var db = _factory.Database();
        Assert.Single(db.PickListRefills.Where(refill => refill.IdempotencyKey == key));
        var movement = await db.StockAdjustments.SingleAsync(adjustment =>
            adjustment.ProductId == ApiFactory.RecordedProductId
            && adjustment.Reason == StockAdjustmentReason.MachineRefill);
        Assert.Equal(-4, movement.QuantityChange);
        Assert.Equal(
            ApiFactory.StartingStock - 4,
            (await db.Products.SingleAsync(product => product.Id == ApiFactory.RecordedProductId)).QuantityInStock);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>
    /// Hosts the real API over one in-memory SQLite database holding a single business, an owner of
    /// it (the role holds both <c>PickList.View</c> and <c>Inventory.Operate</c>) and two stocked
    /// products, one per test, so neither test's writes can decide the other's. The Nayax client is
    /// a strict double with no setup: recording a refill reads nothing from a provider, and calling
    /// one here would fail the test rather than reach the network.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const long RefusedProductId = 100;
        public const long RecordedProductId = 200;
        public const int StartingStock = 10;

        private const string DirectoryTenantId = "99999999-9999-9999-9999-999999999999";
        private const string OwnerObjectId = "33333333-3333-3333-3333-333333333333";
        private const int BusinessId = 1;

        private static readonly DateTime CreatedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);

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
                new Product
                {
                    Id = RefusedProductId,
                    BusinessId = BusinessId,
                    Name = "Coke",
                    QuantityInStock = StartingStock,
                },
                new Product
                {
                    Id = RecordedProductId,
                    BusinessId = BusinessId,
                    Name = "Chips",
                    QuantityInStock = StartingStock,
                });

            // The stock each product holds has to be the sum of its movements: a refill rebuilds the
            // product's cost from its history, and a quantity with no history behind it is the data
            // quality failure that rebuild exists to report.
            db.StockAdjustments.AddRange(
                OpeningStock(RefusedProductId), OpeningStock(RecordedProductId));

            await db.SaveChangesAsync();
            _seeded = true;
        }

        private static StockAdjustment OpeningStock(long productId) => new()
        {
            BusinessId = BusinessId,
            ProductId = productId,
            QuantityChange = StartingStock,
            QuantityAfter = StartingStock,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = 1.50m,
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
