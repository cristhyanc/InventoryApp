using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
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
/// The <c>GET /api/business/current</c> contract (issue #499), exercised through the real request
/// pipeline: authentication, the required scope, the business-scope middleware, the controller and
/// relational SQLite.
///
/// The endpoint exists so the frontend can render operator-facing instants and resolve calendar
/// inputs in the business's own time zone instead of a hard-coded constant. What it must prove
/// here is therefore not only the response shape but where the answer comes from: the trusted
/// current business resolved from the caller's membership. Two businesses with different zones are
/// seeded and each member is answered with their own zone; nothing in the request can choose
/// either one.
/// </summary>
public sealed class CurrentBusinessApiTests : IClassFixture<CurrentBusinessApiTests.ApiFactory>
{
    private const string Uri = "/api/business/current";

    private readonly ApiFactory _factory;

    public CurrentBusinessApiTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Returns_only_the_name_and_time_zone_of_the_callers_own_business()
    {
        await _factory.SeedBusinessesAsync();

        var response = await _factory.Authenticated(ApiFactory.SydneyObjectId).GetAsync(Uri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);
        Assert.Equal(
            ["name", "timeZoneId"],
            json.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Sydney Vending", json.GetProperty("name").GetString());
        Assert.Equal("Australia/Sydney", json.GetProperty("timeZoneId").GetString());
    }

    /// <summary>
    /// The second business is the one that makes the first answer meaningful: the same endpoint,
    /// the same deployment and the same database answer each member with their own zone, so a
    /// response can only have come from the caller's membership.
    /// </summary>
    [Fact]
    public async Task Answers_a_member_of_another_business_with_that_businesss_own_time_zone()
    {
        await _factory.SeedBusinessesAsync();

        var response = await _factory.Authenticated(ApiFactory.NewYorkObjectId).GetAsync(Uri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);
        Assert.Equal("New York Vending", json.GetProperty("name").GetString());
        Assert.Equal("America/New_York", json.GetProperty("timeZoneId").GetString());
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_refused_with_401()
    {
        await _factory.SeedBusinessesAsync();

        var response = await _factory.CreateClient().GetAsync(Uri);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Until issue #502 adds role policies this endpoint carries exactly the requirements every
    /// other business endpoint carries, and the required delegated scope is one of them.
    /// </summary>
    [Fact]
    public async Task An_authenticated_caller_without_the_required_scope_is_refused()
    {
        await _factory.SeedBusinessesAsync();

        var response = await _factory.WithoutScope(ApiFactory.SydneyObjectId).GetAsync(Uri);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_authenticated_caller_with_no_membership_is_refused_and_told_nothing()
    {
        await _factory.SeedBusinessesAsync();

        var response = await _factory.Authenticated(ApiFactory.StrangerObjectId).GetAsync(Uri);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Sydney", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("New_York", body, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const int SydneyBusinessId = 1;
        public const int NewYorkBusinessId = 2;
        public const string DirectoryTenantId = "33333333-3333-3333-3333-333333333333";
        public const string SydneyObjectId = "11111111-1111-1111-1111-111111111111";
        public const string NewYorkObjectId = "22222222-2222-2222-2222-222222222222";
        public const string StrangerObjectId = "44444444-4444-4444-4444-444444444444";

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

        /// <summary>A member of a business whose token carries no delegated scope at all.</summary>
        public HttpClient WithoutScope(string objectId)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.DirectoryTenantIdHeader, DirectoryTenantId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ObjectIdHeader, objectId);
            return client;
        }

        public async Task SeedBusinessesAsync()
        {
            await using var db = Database();
            if (_seeded)
            {
                return;
            }

            db.Businesses.AddRange(
                new Business
                {
                    Id = SydneyBusinessId,
                    Name = "Sydney Vending",
                    TimeZoneId = "Australia/Sydney",
                    CreatedAtUtc = CreatedAt,
                },
                new Business
                {
                    Id = NewYorkBusinessId,
                    Name = "New York Vending",
                    TimeZoneId = "America/New_York",
                    CreatedAtUtc = CreatedAt,
                });
            db.BusinessMemberships.AddRange(
                new BusinessMembership
                {
                    BusinessId = SydneyBusinessId,
                    DirectoryTenantId = DirectoryTenantId,
                    ObjectId = SydneyObjectId,
                    CreatedAtUtc = CreatedAt,
                },
                new BusinessMembership
                {
                    BusinessId = NewYorkBusinessId,
                    DirectoryTenantId = DirectoryTenantId,
                    ObjectId = NewYorkObjectId,
                    CreatedAtUtc = CreatedAt,
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
                var dbContextOptions = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
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
    }

    /// <summary>
    /// Stands in for Entra: the caller's <c>(tid, oid)</c> pair and delegated scope come from
    /// request headers, so membership resolution and the scope requirement are exercised exactly
    /// as they are in production without a real token.
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
