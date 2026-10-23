using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
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
/// <c>GET /api/sites/list</c>, the light site list of issue #841, through the real request pipeline:
/// authentication, the required scope, the <c>Inventory.Operate</c> capability policy,
/// <c>BusinessScopeMiddleware</c>, the controller, the shared Nayax exception handler and relational
/// SQLite with the real migration history.
///
/// <para>Two things only a hosted test can show. The first is the <b>published shape</b>: the rows
/// carry <c>siteId</c> and <c>name</c> and nothing else - no machine count, stock percentage, alert
/// count or revenue, each of which would need the per-machine read this endpoint exists to avoid.
/// The second is the <b>cost</b>, counted on the registered Nayax client across a whole request:
/// one fleet read, and no <c>GetMachineProductsAsync</c> call at all, where <c>GET /api/sites</c>
/// makes one per machine.</para>
///
/// <para>The caller is an Operator, the least-privileged role, because the endpoint declares the
/// same capability as <c>GET /api/sites</c> and every role holds it; the role matrix itself - all
/// three roles reach it, a caller with no membership does not - is
/// <c>InventoryApi.Tests.Auth.CapabilityAuthorizationApiTests</c>, which owns that decision for
/// every capability endpoint. The use-case behaviour over fixed fleets is
/// <c>InventoryApi.Tests.Application.Sites.ListSitesTests</c>.</para>
/// </summary>
public sealed class SiteListApiTests : IClassFixture<SiteListApiTests.ApiFactory>
{
    private const string SiteListUri = "/api/sites/list";

    /// <summary>The light row's whole property set, in ordinal order.</summary>
    private static readonly string[] SiteListProperties = ["name", "siteId"];

    private readonly ApiFactory _factory;

    public SiteListApiTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// The acceptance criterion's shape half: one row per site the fleet is mapped to, carrying the
    /// id a filter submits and the name <c>SiteNameResolver</c> derived, ordered by name. The fleet
    /// holds a machine with no <c>CustomerID</c>, which stands at no site and is excluded exactly as
    /// <c>GET /api/sites</c> excludes it.
    /// </summary>
    [Fact]
    public async Task The_site_list_publishes_one_site_id_and_name_per_site()
    {
        await _factory.SeedAsync();
        _factory.Fleet.Reset();

        var response = await _factory.Authenticated().GetAsync(SiteListUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sites = (await ReadJsonAsync(response)).EnumerateArray().ToList();
        Assert.Equal(2, sites.Count);
        Assert.All(sites, site => Assert.Equal(SiteListProperties, Properties(site)));
        Assert.Equal(
            [ApiFactory.AlphaSiteId, ApiFactory.BravoSiteId],
            sites.Select(site => site.GetProperty("siteId").GetInt64()));
        Assert.Equal(["Alpha", "Bravo"], sites.Select(site => site.GetProperty("name").GetString()));
    }

    /// <summary>
    /// The acceptance criterion's cost half, which is the whole point of the endpoint: one fleet
    /// read for the request, and not one per-machine read - three of which the site summaries would
    /// have made for this same fleet.
    /// </summary>
    [Fact]
    public async Task The_site_list_costs_one_fleet_read_and_no_per_machine_read()
    {
        await _factory.SeedAsync();
        _factory.Fleet.Reset();

        var response = await _factory.Authenticated().GetAsync(SiteListUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, _factory.Fleet.MachineListCalls);
        Assert.Equal(0, _factory.Fleet.MachineProductCalls);
    }

    /// <summary>
    /// A business whose stored Nayax connection the status gate refuses is answered the shared
    /// <c>409 nayax_not_connected</c> - the same answer <c>GET /api/sites</c> gives, and the one
    /// <c>loadProviderList</c> already reads as "this business has no site filter" rather than as
    /// "this business has no sites". Never <c>200</c> with an empty list.
    /// </summary>
    [Fact]
    public async Task A_business_whose_connection_is_refused_is_answered_the_shared_not_connected_conflict() =>
        await AssertConflictAsync(
            new NayaxNotConnectedException(NayaxConnectionStatus.NotConfigured),
            NayaxNotConnectedException.ErrorCode);

    /// <summary>
    /// A Nayax account that has not granted the fleet read is answered the other published
    /// availability code, for the same reason and with the same status.
    /// </summary>
    [Fact]
    public async Task A_fleet_read_nayax_has_not_granted_is_answered_the_shared_permission_conflict() =>
        await AssertConflictAsync(
            new NayaxPermissionNotGrantedException("GetMachinesAsync"),
            NayaxPermissionNotGrantedException.ErrorCode);

    private async Task AssertConflictAsync(Exception failure, string expectedCode)
    {
        await _factory.SeedAsync();
        _factory.Fleet.Reset();
        _factory.Fleet.MachineListFailure = failure;

        try
        {
            var response = await _factory.Authenticated().GetAsync(SiteListUri);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(expectedCode, (await ReadJsonAsync(response)).GetProperty("code").GetString());
        }
        finally
        {
            _factory.Fleet.MachineListFailure = null;
        }
    }

    private static IReadOnlyList<string> Properties(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToList();

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    /// <summary>
    /// Hosts the real API over one in-memory SQLite database holding a single business with one
    /// Operator, and a counting Nayax client standing in for the fleet read. The business needs no
    /// <c>BusinessNayaxConnection</c> row, because the registered client never resolves credentials;
    /// what it has to serve is a fleet whose per-machine products are available, so a request that
    /// read them would succeed and the call counts above are about a choice rather than about
    /// missing fixtures.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const long AlphaSiteId = 43;
        public const long BravoSiteId = 42;

        private const string DirectoryTenantId = "55555555-5555-5555-5555-555555555555";
        private const string OperatorObjectId = "66666666-6666-6666-6666-666666666666";
        private const int BusinessId = 1;

        private static readonly DateTime CreatedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private bool _seeded;

        public ApiFactory() => _connection.Open();

        /// <summary>The registered Nayax client, so a test can count its calls and refuse a read.</summary>
        public FleetSpy Fleet { get; } = new();

        public HttpClient Authenticated()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.DirectoryTenantIdHeader, DirectoryTenantId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ObjectIdHeader, OperatorObjectId);
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
                ObjectId = OperatorObjectId,
                Role = BusinessRole.Operator,
                IsActive = true,
                CreatedAtUtc = CreatedAt,
                StatusChangedAtUtc = CreatedAt,
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
                services.AddSingleton<INayaxLynxClient>(Fleet);
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
    /// A fleet of three machines at two sites plus one machine at no site, counting the calls it is
    /// asked to make. Every machine has products to return, and a refusable fleet read stands in for
    /// the two Nayax availability failures the credential provider and the real client raise.
    /// </summary>
    public sealed class FleetSpy : INayaxLynxClient
    {
        private static readonly List<NayaxMachine> Machines =
        [
            new() { MachineID = 10, CustomerID = ApiFactory.BravoSiteId, MachineName = "Bravo Left" },
            new() { MachineID = 11, CustomerID = ApiFactory.BravoSiteId, MachineName = "Bravo Right" },
            new() { MachineID = 20, CustomerID = ApiFactory.AlphaSiteId, MachineName = "Alpha One" },
            new() { MachineID = 99, CustomerID = null, MachineName = "Unassigned Spare" },
        ];

        private int _machineListCalls;
        private int _machineProductCalls;

        public int MachineListCalls => _machineListCalls;

        public int MachineProductCalls => _machineProductCalls;

        /// <summary>Set to refuse the fleet read, as an unusable Nayax connection does.</summary>
        public Exception? MachineListFailure { get; set; }

        public void Reset()
        {
            Interlocked.Exchange(ref _machineListCalls, 0);
            Interlocked.Exchange(ref _machineProductCalls, 0);
            MachineListFailure = null;
        }

        public Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _machineListCalls);
            return MachineListFailure is null
                ? Task.FromResult(Machines)
                : Task.FromException<List<NayaxMachine>>(MachineListFailure);
        }

        public Task<List<NayaxMachineProduct>> GetMachineProductsAsync(
            long machineId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _machineProductCalls);
            return Task.FromResult<List<NayaxMachineProduct>>(
                [new() { MachineID = machineId, NayaxProductID = 1, PAR = 5, MissingStockByMDB = 1 }]);
        }

        public Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(
            long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(
            long machineId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<NayaxMachine> GetMachineAsync(long machineId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<List<NayaxMachineAlert>> GetMachineLastAlertsAsync(
            long machineId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Stands in for Entra: the caller's <c>(tid, oid)</c> pair and delegated scope come from request
    /// headers, so membership resolution, the scope requirement and the capability policy are
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
