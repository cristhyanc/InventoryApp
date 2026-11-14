using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using InventoryApi.Controllers;
using InventoryApi.Tests.Swagger;
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
/// <c>POST api/site-commissions/{siteId}/payments</c> is gone (issue #1251).
///
/// <para>A recorded payment only ever counted for the exact period it was recorded against, so every
/// other range showed paid commission as outstanding and accepted a second payment for the same
/// sales, and the "does this exceed commission due" check and the insert were not one operation. The
/// repository owner decided on 13 November 2026 that the application does not record commission
/// payments at all: the report states commission due, and nothing else about payment.</para>
///
/// <para>The caller below is an <strong>Owner</strong> of the business, the role that holds
/// <c>SiteCommissions.Manage</c> and could record a payment until now: a <c>404</c> for them is the
/// route being absent rather than a member being refused, which a <c>403</c> would have been.</para>
/// </summary>
public sealed class SiteCommissionsControllerRetiredPaymentRouteTests
    : IClassFixture<SiteCommissionsControllerRetiredPaymentRouteTests.ApiFactory>
{
    private const string RetiredUri = "/api/site-commissions/1/payments?periodStart=2026-07-01&periodEnd=2026-07-31";

    private readonly ApiFactory _factory;

    public SiteCommissionsControllerRetiredPaymentRouteTests(ApiFactory factory) => _factory = factory;

    /// <summary>An Owner's request to the retired route is answered <c>404</c>, and never run.</summary>
    [Fact]
    public async Task An_owners_post_to_the_retired_payment_route_answers_404()
    {
        await _factory.SeedAsync();

        var response = await _factory.AsOwner().PostAsync(
            RetiredUri,
            new StringContent(
                """{"paymentDate":"2026-07-31T00:00:00Z","amount":1.00,"notes":"synthetic"}""",
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// And the controller publishes no payment endpoint at all, so nothing is reachable by another
    /// method or a route the request above happened not to spell.
    /// </summary>
    [Fact]
    public void The_site_commissions_controller_publishes_no_payment_endpoint()
    {
        var descriptions = ApiContractTestHost.GetApiDescriptionsFor<SiteCommissionsController>();

        Assert.DoesNotContain(descriptions, description =>
            description.RelativePath is not null &&
            description.RelativePath.Contains("payments", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The report the page does read is untouched by the removal: it still answers an Owner
    /// <c>200</c> on the same route, which is what proves this change removed the write and not the
    /// surface around it.
    /// </summary>
    [Fact]
    public async Task The_report_route_still_answers_the_same_owner()
    {
        await _factory.SeedAsync();

        var response = await _factory.AsOwner()
            .GetAsync("/api/site-commissions?from=2026-07-01&to=2026-07-31");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Hosts the real API over one in-memory SQLite database holding a single business with one
    /// Owner, so the request below is refused - or not - by the real routing, authentication,
    /// scope and capability pipeline.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private const string DirectoryTenantId = "77777777-7777-7777-7777-777777777777";
        private const string OwnerObjectId = "88888888-8888-8888-8888-888888888888";
        private const int BusinessId = 1;

        private static readonly DateTime CreatedAt = new(2026, 11, 13, 4, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private bool _seeded;

        public ApiFactory() => _connection.Open();

        public HttpClient AsOwner()
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
            if (_seeded) return;

            await using var db = TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

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

            await db.SaveChangesAsync();
            _seeded = true;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Testing applies pending migrations at startup, so the schema under this test is the
            // migration history rather than a model snapshot.
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                var dbContextOptions = services.SingleOrDefault(
                    descriptor => descriptor.ServiceType == typeof(DbContextOptions<AppDbContext>));
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
