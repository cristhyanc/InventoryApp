using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using Inventory.Domain.Providers;
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

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The reorder-alert listing's response metadata (issue #726, review repair), through the real
/// request pipeline for <strong>both</strong> endpoints that publish it:
/// <c>GET /api/products/alerts/low-stock</c> and <c>GET /api/products?lowStockOnly=true</c>, which are
/// the same use case.
///
/// <para>Before this, both endpoints answered <c>machineReplenishmentNeed: 0</c> with no way to tell
/// "the machines need nothing" from "the machines were never read": the mode and reason
/// <c>CalculateReorderNeeds</c> resolved were discarded by the listing, so a caller had to fetch
/// <c>GET /api/dashboard/summary</c> to interpret a reorder-alert list. The body is unchanged - still
/// the same JSON array - and the mode arrives as the additive
/// <see cref="ReorderListingMetadataHeaders.Mode"/> header, with
/// <see cref="ReorderListingMetadataHeaders.MachineNeedUnavailableReason"/> beside it in storage-only
/// mode only.</para>
///
/// <para>The Nayax client is a test double here, because the three availability answers have to be
/// chosen per test; what a business with no stored connection is answered through the <em>real</em>
/// client is <c>ProviderOptionalReorderApiTests</c>.</para>
/// </summary>
public sealed class LowStockReorderMetadataApiTests : IClassFixture<LowStockReorderMetadataApiTests.ApiFactory>
{
    private const string LowStockAlerts = "/api/products/alerts/low-stock";
    private const string CatalogueLowStockOnly = "/api/products?lowStockOnly=true";

    private readonly ApiFactory _factory;

    public LowStockReorderMetadataApiTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// A fleet that answered: the mode says the machines' need is in the figures, and there is no
    /// unavailability reason to publish. The one machine reports three units missing for the stocked
    /// product, which is what makes it alert in this mode and not in storage-only mode.
    /// </summary>
    [Theory]
    [InlineData(LowStockAlerts)]
    [InlineData(CatalogueLowStockOnly)]
    public async Task The_listing_publishes_the_machine_aware_mode_and_no_reason(string path)
    {
        await _factory.SeedAsync();
        _factory.FleetAnswers();

        var response = await _factory.Authenticated().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("MachineAware", Header(response, ReorderListingMetadataHeaders.Mode));
        Assert.Null(Header(response, ReorderListingMetadataHeaders.MachineNeedUnavailableReason));
        var names = await ProductNamesAsync(response);
        Assert.Equal([ApiFactory.StockedProductName, ApiFactory.ThresholdProductName], names.Order());
    }

    /// <summary>
    /// The business has no Nayax credentials an ordinary operation may use. The alerts are the
    /// storage-only ones, and the two headers say so - the row's <c>machineReplenishmentNeed: 0</c> is
    /// a missing input, not a fact about the machines.
    /// </summary>
    [Theory]
    [InlineData(LowStockAlerts)]
    [InlineData(CatalogueLowStockOnly)]
    public async Task The_listing_publishes_storage_only_with_the_not_connected_reason(string path)
    {
        await _factory.SeedAsync();
        _factory.FleetIsNotConnected();

        var response = await _factory.Authenticated().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("StorageOnly", Header(response, ReorderListingMetadataHeaders.Mode));
        Assert.Equal(
            "NayaxNotConnected", Header(response, ReorderListingMetadataHeaders.MachineNeedUnavailableReason));
        Assert.Equal([ApiFactory.ThresholdProductName], await ProductNamesAsync(response));
    }

    /// <summary>
    /// The credentials are usable and Nayax refused the fleet read with <c>403</c>. Availability is
    /// per feature, so the listing is answered in storage-only mode and the reason distinguishes it
    /// from a business that never connected - an operator fixes the two differently.
    /// </summary>
    [Theory]
    [InlineData(LowStockAlerts)]
    [InlineData(CatalogueLowStockOnly)]
    public async Task The_listing_publishes_storage_only_with_the_permission_not_granted_reason(string path)
    {
        await _factory.SeedAsync();
        _factory.FleetIsNotGranted();

        var response = await _factory.Authenticated().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("StorageOnly", Header(response, ReorderListingMetadataHeaders.Mode));
        Assert.Equal(
            "PermissionNotGranted", Header(response, ReorderListingMetadataHeaders.MachineNeedUnavailableReason));
        Assert.Equal([ApiFactory.ThresholdProductName], await ProductNamesAsync(response));
    }

    /// <summary>
    /// An empty list is where the metadata matters most, and is exactly what a per-row field could
    /// never carry: the search matches no product, so the array is empty, and the mode and reason are
    /// still published - "nothing alerts" is not the same answer as "nothing alerts and the machines
    /// were never read".
    /// </summary>
    [Theory]
    [InlineData(LowStockAlerts)]
    [InlineData(CatalogueLowStockOnly)]
    public async Task An_empty_listing_still_publishes_its_mode_and_reason(string path)
    {
        await _factory.SeedAsync();
        _factory.FleetIsNotConnected();

        var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var response = await _factory.Authenticated().GetAsync($"{path}{separator}search=no-such-product");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await ProductNamesAsync(response));
        Assert.Equal("StorageOnly", Header(response, ReorderListingMetadataHeaders.Mode));
        Assert.Equal(
            "NayaxNotConnected", Header(response, ReorderListingMetadataHeaders.MachineNeedUnavailableReason));
    }

    /// <summary>
    /// The plain catalogue listing calculates no reorder needs, so it publishes no mode: an absent
    /// header means "no reorder facts were involved", never a guess at which mode would have applied.
    /// It is also why no Nayax read happens for it at all.
    /// </summary>
    [Fact]
    public async Task The_plain_catalogue_listing_publishes_no_reorder_metadata()
    {
        await _factory.SeedAsync();
        _factory.FleetAnswers();

        var response = await _factory.Authenticated().GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Header(response, ReorderListingMetadataHeaders.Mode));
        Assert.Null(Header(response, ReorderListingMetadataHeaders.MachineNeedUnavailableReason));
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private static async Task<IReadOnlyList<string>> ProductNamesAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.EnumerateArray()
            .Select(product => product.GetProperty("name").GetString()!)
            .ToList();
    }

    /// <summary>
    /// Hosts the real API over one in-memory SQLite database holding a single business with two
    /// products: one sitting on its low-stock threshold on storage stock alone, and one comfortably
    /// stocked in storage that only alerts once a machine's missing stock is accounted for. The Nayax
    /// client is a double whose fleet read is switched per test.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string ThresholdProductName = "Coke";
        public const string StockedProductName = "Chips";

        private const string DirectoryTenantId = "88888888-8888-8888-8888-888888888888";
        private const string OwnerObjectId = "22222222-2222-2222-2222-222222222222";
        private const int BusinessId = 1;
        private const long ThresholdProductId = 100;
        private const long StockedProductId = 200;
        private const long MachineId = 55;

        /// <summary>
        /// The machine's missing stock for the comfortably stocked product. It is what makes that
        /// product alert in machine-aware mode and not at all in storage-only mode, so the two modes
        /// have genuinely different right answers over one catalogue: projected stock is
        /// <c>40 - 36 = 4</c>, at or below its threshold of 5, where storage stock alone (40) is well
        /// clear of it.
        /// </summary>
        private const int MissingStockByMdb = 36;

        private static readonly DateTime CreatedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private bool _seeded;

        public ApiFactory() => _connection.Open();

        /// <summary>
        /// What the fleet read does. Set by one of the three switches below before each request;
        /// tests in a class run one at a time, and this assembly's collections do too.
        /// </summary>
        private Func<Task<List<NayaxMachine>>> MachinesRead { get; set; } =
            () => Task.FromResult(new List<NayaxMachine>());

        /// <summary>The fleet answers: one machine, with stock missing for the stocked product.</summary>
        public void FleetAnswers() =>
            MachinesRead = () => Task.FromResult(new List<NayaxMachine> { new() { MachineID = MachineId } });

        /// <summary>No credentials an ordinary operation may use, as the credential provider refuses.</summary>
        public void FleetIsNotConnected() =>
            MachinesRead = () => throw new NayaxNotConnectedException(NayaxConnectionStatus.NotConfigured);

        /// <summary>Usable credentials, and Nayax refused this read for lack of permission.</summary>
        public void FleetIsNotGranted() =>
            MachinesRead = () => throw new NayaxPermissionNotGrantedException("GetMachinesAsync");

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
            // Resolving Services builds and starts the host, which applies the migrations this
            // context then writes through.
            _ = Services;
            await using var db = TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
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

            db.Products.Add(new Product
            {
                Id = ThresholdProductId,
                BusinessId = BusinessId,
                Name = ThresholdProductName,
                QuantityInStock = 5,
                LowStockThreshold = 5,
                RestockTo = 24,
            });

            db.Products.Add(new Product
            {
                Id = StockedProductId,
                BusinessId = BusinessId,
                Name = StockedProductName,
                QuantityInStock = 40,
                LowStockThreshold = 5,
                RestockTo = 24,
            });

            // The stocked product's Nayax link (issue #732), without which the machine selection
            // resolves to no local product at all (issue #733). Every imported product has one in
            // production.
            db.ProductExternalIds.Add(new ProductExternalId
            {
                BusinessId = BusinessId,
                Provider = ExternalProvider.Nayax,
                ExternalId = NayaxExternalId.From(StockedProductId),
                ProductId = StockedProductId,
            });

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

                var nayax = new Mock<INayaxLynxClient>();
                nayax.Setup(client => client.GetMachinesAsync(It.IsAny<CancellationToken>()))
                    .Returns(() => MachinesRead());
                nayax.Setup(client => client.GetMachineProductsAsync(MachineId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new List<NayaxMachineProduct>
                    {
                        new() { NayaxProductID = StockedProductId, MissingStockByMDB = MissingStockByMdb },
                    });
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
