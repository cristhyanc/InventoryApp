using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Inventory.Application.Nayax;
using Inventory.Domain.Machines;
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

// The persisted movement's reason, which the Domain declares under the same name.
using StockAdjustmentReason = Inventory.Infrastructure.Models.StockAdjustmentReason;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// Issue #1240, through the real request pipeline: every stock write that rebuilds a product's cost
/// and is refused because that product's history has a fatal data-quality issue answers
/// <c>409 Conflict</c> with the replay's own product-named sentence, instead of the generic
/// <c>500 "An unexpected error occurred"</c> it used to answer with.
///
/// <para>The blocked product is seeded exactly as the audit found one in the field: a positive
/// <c>Restock</c> movement with <strong>no</strong> unit cost, which the replay reports as the fatal
/// <c>UnknownCost</c> and names the product in. Nothing here simulates the refusal - the real
/// <c>RebuildProductCost</c> raises it over real stored rows - so these tests also prove the three
/// endpoints reach it at all.</para>
///
/// <para>Both pick-list endpoints additionally prove the batch rule they promise: the good product
/// in the same submission keeps its storage quantity, has no movement, and no refill row is
/// recorded under the key. An all-or-nothing refusal that quietly committed the rows before the
/// blocked one is the failure this asserts against.</para>
/// </summary>
public sealed class CostDataQualityRefusalApiTests : IClassFixture<CostDataQualityRefusalApiTests.ApiFactory>
{
    private readonly ApiFactory _factory;

    public CostDataQualityRefusalApiTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_manual_adjustment_of_a_blocked_product_is_a_409_naming_the_product()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated().PostAsync(
            $"/api/products/{ApiFactory.BlockedProductId}/stock",
            Json($$"""
                { "quantityChange": -1, "reason": {{(int)StockAdjustmentReason.Correction}}, "notes": "Spillage" }
                """));

        var detail = await AssertConflictAsync(response);
        Assert.Contains(ApiFactory.BlockedProductLabel, detail, StringComparison.Ordinal);

        await using var db = _factory.Database();
        Assert.Equal(
            ApiFactory.StartingStock,
            (await db.Products.SingleAsync(product => product.Id == ApiFactory.BlockedProductId)).QuantityInStock);
        Assert.Empty(db.StockAdjustments.Where(movement =>
            movement.ProductId == ApiFactory.BlockedProductId
            && movement.Reason == StockAdjustmentReason.Correction));
    }

    [Fact]
    public async Task A_take_inventory_apply_for_a_blocked_product_is_a_409_naming_the_product()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated().PostAsync(
            $"/api/products/{ApiFactory.BlockedProductId}/inventory-count/apply",
            Json($$"""
                { "countedStock": {{ApiFactory.StartingStock - 2}}, "expectedCurrentStock": {{ApiFactory.StartingStock}} }
                """));

        var detail = await AssertConflictAsync(response);
        Assert.Contains(ApiFactory.BlockedProductLabel, detail, StringComparison.Ordinal);

        await using var db = _factory.Database();
        Assert.Equal(
            ApiFactory.StartingStock,
            (await db.Products.SingleAsync(product => product.Id == ApiFactory.BlockedProductId)).QuantityInStock);
    }

    [Fact]
    public async Task A_single_machine_refill_names_the_blocked_product_and_its_machine_and_records_no_part_of_the_batch()
    {
        await _factory.SeedAsync();
        const string Key = "11111111-0000-4000-8000-000000000001";

        // The good row first, so its movement is staged and saved before the blocked row is reached:
        // what the rollback has to discard is a write that already happened.
        var response = await _factory.Authenticated().PostAsync(
            "/api/pick-list/refills",
            Json($$"""
                {
                  "idempotencyKey": "{{Key}}",
                  "machineId": {{ApiFactory.MachineId}},
                  "rows": [
                    { "productId": {{ApiFactory.SingleRefillProductId}}, "quantityTaken": 2 },
                    { "productId": {{ApiFactory.BlockedProductId}}, "quantityTaken": 1 }
                  ]
                }
                """));

        var detail = await AssertConflictAsync(response);
        Assert.StartsWith(PickListRefillPolicy.NothingRecordedPrefix, detail, StringComparison.Ordinal);
        Assert.Contains(ApiFactory.BlockedProductLabel, detail, StringComparison.Ordinal);
        Assert.Contains(ApiFactory.MachineLabelText, detail, StringComparison.Ordinal);

        await AssertNothingRecordedAsync(Key, ApiFactory.SingleRefillProductId);
    }

    [Fact]
    public async Task A_multi_machine_refill_names_the_blocked_product_and_its_machine_and_records_no_part_of_the_batch()
    {
        await _factory.SeedAsync();
        const string Key = "11111111-0000-4000-8000-000000000002";

        var response = await _factory.Authenticated().PostAsync(
            "/api/pick-list/multi-machine-refills",
            Json($$"""
                {
                  "idempotencyKey": "{{Key}}",
                  "allocations": [
                    { "machineId": {{ApiFactory.MachineId}}, "productId": {{ApiFactory.MultiRefillProductId}}, "quantityTaken": 2 },
                    { "machineId": {{ApiFactory.SecondMachineId}}, "productId": {{ApiFactory.BlockedProductId}}, "quantityTaken": 1 }
                  ]
                }
                """));

        var detail = await AssertConflictAsync(response);
        Assert.StartsWith(PickListRefillPolicy.NothingRecordedPrefix, detail, StringComparison.Ordinal);
        Assert.Contains(ApiFactory.BlockedProductLabel, detail, StringComparison.Ordinal);

        // The machine named is the blocked allocation's own, not the first machine of the batch:
        // in a matrix the operator has to know which column the bad cell is in.
        Assert.Contains(ApiFactory.SecondMachineLabelText, detail, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFactory.MachineLabelText, detail, StringComparison.Ordinal);

        await AssertNothingRecordedAsync(Key, ApiFactory.MultiRefillProductId);
    }

    /// <summary>
    /// The control, over the same endpoint and the same database: a batch naming only healthy
    /// products is recorded, so "nothing was written" above is a fact about the refusal rather than
    /// about this harness.
    /// </summary>
    [Fact]
    public async Task A_batch_of_healthy_products_is_still_recorded()
    {
        await _factory.SeedAsync();
        const string Key = "11111111-0000-4000-8000-000000000003";

        var response = await _factory.Authenticated().PostAsync(
            "/api/pick-list/multi-machine-refills",
            Json($$"""
                {
                  "idempotencyKey": "{{Key}}",
                  "allocations": [
                    { "machineId": {{ApiFactory.MachineId}}, "productId": {{ApiFactory.ControlProductId}}, "quantityTaken": 3 }
                  ]
                }
                """));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = _factory.Database();
        Assert.Single(db.PickListRefills.Where(refill => refill.IdempotencyKey == Key));
        Assert.Equal(
            ApiFactory.StartingStock - 3,
            (await db.Products.SingleAsync(product => product.Id == ApiFactory.ControlProductId)).QuantityInStock);
    }

    /// <summary>
    /// The refusal's status, media type and both message fields - <c>detail</c> and the <c>message</c>
    /// extension the frontend error handling reads - returning the sentence they agree on.
    /// </summary>
    private static async Task<string> AssertConflictAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var detail = body.GetProperty("detail").GetString();
        Assert.False(string.IsNullOrWhiteSpace(detail));
        Assert.Equal(detail, body.GetProperty("message").GetString());
        return detail!;
    }

    private async Task AssertNothingRecordedAsync(string idempotencyKey, long goodProductId)
    {
        await using var db = _factory.Database();

        Assert.Empty(db.PickListRefills.Where(refill => refill.IdempotencyKey == idempotencyKey));
        Assert.Empty(db.StockAdjustments.Where(movement =>
            movement.Reason == StockAdjustmentReason.MachineRefill
            && (movement.ProductId == goodProductId || movement.ProductId == ApiFactory.BlockedProductId)));
        Assert.Equal(
            ApiFactory.StartingStock,
            (await db.Products.SingleAsync(product => product.Id == goodProductId)).QuantityInStock);
        Assert.Equal(
            ApiFactory.StartingStock,
            (await db.Products.SingleAsync(product => product.Id == ApiFactory.BlockedProductId)).QuantityInStock);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>
    /// Hosts the real API over one in-memory SQLite database holding a single business, an owner of
    /// it (the Owner role holds <c>Inventory.Operate</c> and <c>PickList.View</c>), two active
    /// machines, one product whose cost history is fatally incomplete and three healthy products -
    /// one per test that takes stock, so no test's writes can decide another's. The Nayax client is
    /// a strict double with no setup: none of these writes reads a provider.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const long BlockedProductId = 100;
        public const long SingleRefillProductId = 200;
        public const long MultiRefillProductId = 300;
        public const long ControlProductId = 400;
        public const long MachineId = 50;
        public const long SecondMachineId = 51;
        public const int StartingStock = 10;

        /// <summary>How <c>ProductLabel</c> names the blocked product inside the replay's message.</summary>
        public const string BlockedProductLabel = "Coke 375mL (100)";

        public static readonly string MachineLabelText = MachineLabel.Phrase(MachineId, "Front Lobby");
        public static readonly string SecondMachineLabelText = MachineLabel.Phrase(SecondMachineId, "Loading Dock");

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

            db.Machines.AddRange(
                new LocalMachine { Id = MachineId, BusinessId = BusinessId, Name = "Front Lobby", IsActive = true },
                new LocalMachine { Id = SecondMachineId, BusinessId = BusinessId, Name = "Loading Dock", IsActive = true });

            db.Products.AddRange(
                Product(BlockedProductId, "Coke 375mL"),
                Product(SingleRefillProductId, "Chips"),
                Product(MultiRefillProductId, "Water 600mL"),
                Product(ControlProductId, "Chocolate"));

            // The healthy products' stock is the sum of one costed opening movement. The blocked
            // product's opening movement carries no unit cost at all, which is the fatal
            // UnknownCost history this whole feature is about: the quantity is real, what it cost
            // was never recorded, and no replay may invent it.
            db.StockAdjustments.AddRange(
                OpeningStock(BlockedProductId, unitCost: null),
                OpeningStock(SingleRefillProductId, unitCost: 1.50m),
                OpeningStock(MultiRefillProductId, unitCost: 1.50m),
                OpeningStock(ControlProductId, unitCost: 1.50m));

            await db.SaveChangesAsync();
            _seeded = true;
        }

        private static Product Product(long id, string name) => new()
        {
            Id = id,
            BusinessId = BusinessId,
            Name = name,
            QuantityInStock = StartingStock,
        };

        private static StockAdjustment OpeningStock(long productId, decimal? unitCost) => new()
        {
            BusinessId = BusinessId,
            ProductId = productId,
            QuantityChange = StartingStock,
            QuantityAfter = StartingStock,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = unitCost,
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
