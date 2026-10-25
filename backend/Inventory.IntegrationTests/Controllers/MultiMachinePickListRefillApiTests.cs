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
/// <c>POST /api/pick-list/multi-machine-refills</c> (issue #897) through the real request pipeline:
/// JSON model binding, authentication, the required scope, the capability policies, the controller,
/// the use case and relational SQLite.
///
/// <para>What only this level can prove is what the <em>body on the wire</em> does, which is why it
/// exists beside <c>PickListControllerTests</c>: those call the controller with objects a compiler
/// built, so they cannot produce an allocation element that is missing. JSON can - <c>[null]</c> is a
/// valid array whatever the element type declares - and an allocation that simply omits
/// <c>machineId</c> is the other thing only a real body can send. The recorded run beside them is the
/// control: the same endpoint over the same database does record a valid body, so "nothing was
/// written" is a fact about the refusal and not about the harness.</para>
///
/// <para>It also holds the backward-compatibility check for the endpoint this one was added beside:
/// <c>POST /api/pick-list/refills</c> still accepts its own body and still answers its own response,
/// over the same schema and the same tables (AGENTS.md § Architecture rules - preserve public API
/// contracts).</para>
/// </summary>
public sealed class MultiMachinePickListRefillApiTests
    : IClassFixture<MultiMachinePickListRefillApiTests.ApiFactory>
{
    private const string Uri = "/api/pick-list/multi-machine-refills";
    private const string SingleMachineUri = "/api/pick-list/refills";

    private readonly ApiFactory _factory;

    public MultiMachinePickListRefillApiTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// The control, and the criterion: one body, two machines, one movement per allocation attributed
    /// to its own machine, and storage down by the sum.
    /// </summary>
    [Fact]
    public async Task A_body_with_every_allocation_present_is_recorded()
    {
        await _factory.SeedAsync();
        const string key = "c0ffee00-0000-4000-8000-00000000aaaa";

        var response = await _factory.Authenticated().PostAsync(
            Uri,
            Json($$"""
                {
                  "idempotencyKey": "{{key}}",
                  "notes": "Friday run",
                  "allocations": [
                    { "machineId": {{ApiFactory.MachineA}}, "productId": {{ApiFactory.RecordedProductId}}, "quantityTaken": 4 },
                    { "machineId": {{ApiFactory.MachineB}}, "productId": {{ApiFactory.RecordedProductId}}, "quantityTaken": 3 }
                  ]
                }
                """));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.GetProperty("replayed").GetBoolean());
        Assert.Equal(
            [ApiFactory.MachineA, ApiFactory.MachineB],
            body.GetProperty("allocations").EnumerateArray()
                .Select(allocation => allocation.GetProperty("machineId").GetInt64()));

        await using var db = _factory.Database();
        var refill = await db.PickListRefills
            .Include(row => row.Lines)
            .SingleAsync(row => row.IdempotencyKey == key);
        Assert.Null(refill.MachineId);
        Assert.Equal(
            [ApiFactory.MachineA, ApiFactory.MachineB],
            refill.Lines.OrderBy(line => line.Id).Select(line => line.MachineId));

        var movements = await db.StockAdjustments
            .Where(movement =>
                movement.ProductId == ApiFactory.RecordedProductId
                && movement.Reason == StockAdjustmentReason.MachineRefill)
            .OrderBy(movement => movement.Id)
            .ToListAsync();
        Assert.Equal([-4, -3], movements.Select(movement => movement.QuantityChange));
        Assert.Equal(
            [ApiFactory.MachineA, ApiFactory.MachineB],
            movements.Select(movement => movement.MachineId));
        Assert.Equal(
            ApiFactory.StartingStock - 7,
            (await db.Products.SingleAsync(product => product.Id == ApiFactory.RecordedProductId))
                .QuantityInStock);
    }

    /// <summary>
    /// A missing element among otherwise valid allocations: <c>400</c> with the refusal on the line the
    /// caller sent it on, and the valid allocation beside it written nowhere, because one refusal
    /// refuses the whole batch.
    /// </summary>
    [Fact]
    public async Task An_allocation_sent_as_a_missing_value_is_refused_per_line_and_records_nothing()
    {
        await _factory.SeedAsync();
        const string key = "c0ffee00-0000-4000-8000-00000000bbbb";

        var response = await _factory.Authenticated().PostAsync(
            Uri,
            Json($$"""
                {
                  "idempotencyKey": "{{key}}",
                  "allocations": [
                    { "machineId": {{ApiFactory.MachineA}}, "productId": {{ApiFactory.RefusedProductId}}, "quantityTaken": 2 },
                    null
                  ]
                }
                """));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var errors = JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement
            .GetProperty("errors");
        Assert.Equal(
            MultiMachinePickListRefillPolicy.MissingAllocationMessage,
            Assert.Single(errors
                .GetProperty(MultiMachinePickListRefillResponseMapper.AllocationKey(1))
                .EnumerateArray())
                .GetString());
        Assert.False(errors.TryGetProperty(
            MultiMachinePickListRefillResponseMapper.AllocationKey(0), out _));

        await AssertNothingRecordedAsync(key);
    }

    /// <summary>
    /// An allocation that simply omits <c>machineId</c> - which only a real body can do, since the
    /// contract's own type declares it nullable precisely so this is a refusal and not a bind to
    /// <c>0</c>.
    /// </summary>
    [Fact]
    public async Task An_allocation_with_no_machine_is_refused_per_line_and_records_nothing()
    {
        await _factory.SeedAsync();
        const string key = "c0ffee00-0000-4000-8000-00000000cccc";

        var response = await _factory.Authenticated().PostAsync(
            Uri,
            Json($$"""
                {
                  "idempotencyKey": "{{key}}",
                  "allocations": [
                    { "productId": {{ApiFactory.RefusedProductId}}, "quantityTaken": 2 }
                  ]
                }
                """));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement
            .GetProperty("errors");
        Assert.Equal(
            MultiMachinePickListRefillPolicy.MachineRequiredMessage,
            Assert.Single(errors
                .GetProperty(MultiMachinePickListRefillResponseMapper.AllocationKey(0))
                .EnumerateArray())
                .GetString());

        await AssertNothingRecordedAsync(key);
    }

    /// <summary>
    /// The aggregate storage rule over the wire: two allocations of one product that each fit and
    /// together do not. Both lines carry the refusal and the storeroom is untouched.
    /// </summary>
    [Fact]
    public async Task Allocations_that_together_exceed_storage_are_refused_on_every_contributing_line()
    {
        await _factory.SeedAsync();
        const string key = "c0ffee00-0000-4000-8000-00000000dddd";

        var response = await _factory.Authenticated().PostAsync(
            Uri,
            Json($$"""
                {
                  "idempotencyKey": "{{key}}",
                  "allocations": [
                    { "machineId": {{ApiFactory.MachineA}}, "productId": {{ApiFactory.RefusedProductId}}, "quantityTaken": 6 },
                    { "machineId": {{ApiFactory.MachineB}}, "productId": {{ApiFactory.RefusedProductId}}, "quantityTaken": 6 }
                  ]
                }
                """));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement
            .GetProperty("errors");

        var expected = MultiMachinePickListRefillPolicy.NotEnoughStockForTotalMessage(
            12, ApiFactory.StartingStock);
        foreach (var index in new[] { 0, 1 })
        {
            Assert.Equal(
                expected,
                Assert.Single(errors
                    .GetProperty(MultiMachinePickListRefillResponseMapper.AllocationKey(index))
                    .EnumerateArray())
                    .GetString());
        }

        await AssertNothingRecordedAsync(key);
    }

    /// <summary>
    /// The endpoint this one was added beside is unchanged: the same body it always took, the same
    /// response it always answered - <c>machineId</c> on the refill and <c>rows</c> rather than
    /// <c>allocations</c> - and the same stored shape, with no machine on its line.
    /// </summary>
    [Fact]
    public async Task The_single_machine_endpoint_still_answers_its_own_contract()
    {
        await _factory.SeedAsync();
        const string key = "c0ffee00-0000-4000-8000-00000000eeee";

        var response = await _factory.Authenticated().PostAsync(
            SingleMachineUri,
            Json($$"""
                {
                  "idempotencyKey": "{{key}}",
                  "machineId": {{ApiFactory.MachineA}},
                  "rows": [{ "productId": {{ApiFactory.CompatibilityProductId}}, "quantityTaken": 4 }]
                }
                """));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.GetProperty("replayed").GetBoolean());
        Assert.Equal(ApiFactory.MachineA, body.GetProperty("machineId").GetInt64());
        Assert.Equal(
            4,
            Assert.Single(body.GetProperty("rows").EnumerateArray())
                .GetProperty("quantityTaken").GetInt32());
        Assert.False(body.TryGetProperty("allocations", out _));

        await using var db = _factory.Database();
        var refill = await db.PickListRefills
            .Include(row => row.Lines)
            .SingleAsync(row => row.IdempotencyKey == key);
        Assert.Equal(ApiFactory.MachineA, refill.MachineId);
        Assert.Null(Assert.Single(refill.Lines).MachineId);
        Assert.Equal(
            ApiFactory.StartingStock - 4,
            (await db.Products.SingleAsync(product => product.Id == ApiFactory.CompatibilityProductId))
                .QuantityInStock);
    }

    /// <summary>
    /// A no-machine refill is still recorded by the single-machine endpoint, which is what makes
    /// "an allocation must name a machine" a bounded rule rather than a removed capability.
    /// </summary>
    [Fact]
    public async Task The_single_machine_endpoint_still_records_a_refill_with_no_machine()
    {
        await _factory.SeedAsync();
        const string key = "c0ffee00-0000-4000-8000-00000000ffff";

        var response = await _factory.Authenticated().PostAsync(
            SingleMachineUri,
            Json($$"""
                {
                  "idempotencyKey": "{{key}}",
                  "rows": [{ "productId": {{ApiFactory.NoMachineProductId}}, "quantityTaken": 1 }]
                }
                """));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Null, body.GetProperty("machineId").ValueKind);

        await using var db = _factory.Database();
        Assert.Null((await db.PickListRefills.SingleAsync(row => row.IdempotencyKey == key)).MachineId);
    }

    private async Task AssertNothingRecordedAsync(string key)
    {
        await using var db = _factory.Database();
        Assert.Empty(db.PickListRefills.Where(refill => refill.IdempotencyKey == key));
        Assert.Empty(db.StockAdjustments.Where(movement =>
            movement.ProductId == ApiFactory.RefusedProductId
            && movement.Reason == StockAdjustmentReason.MachineRefill));
        Assert.Equal(
            ApiFactory.StartingStock,
            (await db.Products.SingleAsync(product => product.Id == ApiFactory.RefusedProductId))
                .QuantityInStock);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>
    /// Hosts the real API over one in-memory SQLite database holding a single business, an owner of it
    /// (the role holds both <c>PickList.View</c> and <c>Inventory.Operate</c>), two active machines and
    /// one stocked product per test, so no test's writes can decide another's. The Nayax client is a
    /// strict double with no setup: recording a refill reads nothing from a provider, and calling one
    /// here would fail the test rather than reach the network.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const long RefusedProductId = 300;
        public const long RecordedProductId = 301;
        public const long CompatibilityProductId = 302;
        public const long NoMachineProductId = 303;
        public const long MachineA = 11;
        public const long MachineB = 12;
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
            // Resolving Services builds and starts the host, which applies the migrations this context
            // then reads through.
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
                new LocalMachine { Id = MachineA, BusinessId = BusinessId, Name = "Machine A", IsActive = true },
                new LocalMachine { Id = MachineB, BusinessId = BusinessId, Name = "Machine B", IsActive = true });

            foreach (var productId in new[]
                { RefusedProductId, RecordedProductId, CompatibilityProductId, NoMachineProductId })
            {
                db.Products.Add(new Product
                {
                    Id = productId,
                    BusinessId = BusinessId,
                    Name = $"Product {productId}",
                    QuantityInStock = StartingStock,
                });

                // The stock each product holds has to be the sum of its movements: a refill rebuilds
                // the product's cost from its history, and a quantity with no history behind it is the
                // data quality failure that rebuild exists to report.
                db.StockAdjustments.Add(new StockAdjustment
                {
                    BusinessId = BusinessId,
                    ProductId = productId,
                    QuantityChange = StartingStock,
                    QuantityAfter = StartingStock,
                    Reason = StockAdjustmentReason.Restock,
                    UnitCost = 1.50m,
                    EffectiveAt = CreatedAt,
                    CreatedAt = CreatedAt,
                });
            }

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
    /// Stands in for Entra: the caller's <c>(tid, oid)</c> pair and delegated scope come from request
    /// headers, so membership resolution, the scope requirement and the capability policies are
    /// exercised exactly as they are in production without a real token.
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
