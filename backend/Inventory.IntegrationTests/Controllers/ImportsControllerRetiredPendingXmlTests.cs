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
/// <c>POST api/imports/pending-xml</c> is gone (issue #1237).
///
/// <para>The server-folder import it ran was the one import that did not take its input from the
/// signed-in business's own upload: whatever XML somebody with server access had dropped into
/// <c>{WebRootPath}/ImportedFiles</c>. Reimbursement reports are uploaded through the Reimbursement
/// Report page instead (issues #1149 and #1152), so the route was retired.</para>
///
/// <para>The caller below is an <strong>Owner</strong> of the business, the role that holds
/// <c>Imports.Run</c> and could run this import until now: a <c>404</c> for them is the route being
/// absent rather than a member being refused, which a <c>403</c> would have been.</para>
/// </summary>
public sealed class ImportsControllerRetiredPendingXmlTests
    : IClassFixture<ImportsControllerRetiredPendingXmlTests.ApiFactory>
{
    private const string RetiredUri = "/api/imports/pending-xml";

    private readonly ApiFactory _factory;

    public ImportsControllerRetiredPendingXmlTests(ApiFactory factory) => _factory = factory;

    /// <summary>An Owner's request to the retired route is answered <c>404</c>, and never run.</summary>
    [Fact]
    public async Task An_owners_post_to_the_retired_pending_xml_route_answers_404()
    {
        await _factory.SeedAsync();

        var response = await _factory.AsOwner().PostAsync(
            RetiredUri, new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// And the controller publishes no such endpoint at all, so nothing is reachable by another
    /// method or a route the request above happened not to spell.
    /// </summary>
    [Fact]
    public void The_imports_controller_publishes_no_pending_xml_endpoint()
    {
        var descriptions = ApiContractTestHost.GetApiDescriptionsFor<ImportsController>();

        Assert.DoesNotContain(descriptions, description =>
            description.RelativePath is not null &&
            description.RelativePath.Contains("pending-xml", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Hosts the real API over one in-memory SQLite database holding a single business with one
    /// Owner, so the request below is refused - or not - by the real routing, authentication,
    /// scope and capability pipeline.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private const string DirectoryTenantId = "55555555-5555-5555-5555-555555555555";
        private const string OwnerObjectId = "66666666-6666-6666-6666-666666666666";
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
