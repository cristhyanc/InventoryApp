using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
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
/// The <c>GET /api/me/access</c> contract (issue #521), exercised through the real request
/// pipeline: authentication, the required scope, the business-scope middleware, the controller and
/// relational SQLite with the real migration history.
///
/// What it has to prove is not only the response shape but where the answer comes from. Three
/// members of the same business hold the three different roles and are each answered with their
/// own role and that role's capabilities; a fourth business exists so the business name and zone
/// can only have come from the caller's own membership; and the response never mentions another
/// member or another business.
/// </summary>
public sealed class MeAccessApiTests : IClassFixture<MeAccessApiTests.ApiFactory>
{
    private const string Uri = "/api/me/access";

    private readonly ApiFactory _factory;

    public MeAccessApiTests(ApiFactory factory) => _factory = factory;

    public static TheoryData<string, string, string[]> RolesAndCapabilities => new()
    {
        {
            ApiFactory.OperatorObjectId,
            "Operator",
            ["Dashboard.View", "PickList.View", "Inventory.Operate", "Purchasing.Operate"]
        },
        {
            ApiFactory.ManagerObjectId,
            "Manager",
            [
                "Dashboard.View",
                "Dashboard.ViewFinancials",
                "PickList.View",
                "Inventory.Operate",
                "Purchasing.Operate",
                "Suppliers.ManageGstDefaults",
                "Expenses.Manage",
                "Reports.View",
                "Imports.Run",
                "SiteCommissions.Manage",
            ]
        },
        {
            ApiFactory.OwnerObjectId,
            "Owner",
            [
                "Dashboard.View",
                "Dashboard.ViewFinancials",
                "PickList.View",
                "Inventory.Operate",
                "Purchasing.Operate",
                "Suppliers.ManageGstDefaults",
                "Expenses.Manage",
                "Reports.View",
                "Imports.Run",
                "SiteCommissions.Manage",
                "Integration.Manage",
                "Data.Repair",
                "Members.Manage",
                "Roles.View",
                "Business.Manage",
            ]
        },
    };

    /// <summary>
    /// The endpoint for each role: the same deployment and the same database answer each member
    /// with their own role and exactly that role's capabilities, so a response can only have come
    /// from the caller's membership.
    /// </summary>
    [Theory]
    [MemberData(nameof(RolesAndCapabilities))]
    public async Task Answers_each_member_with_their_own_role_and_its_capabilities(
        string objectId,
        string expectedRole,
        string[] expectedCapabilities)
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(objectId).GetAsync(Uri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);

        Assert.Equal(
            ["businessName", "capabilities", "role", "timeZoneId"],
            json.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(expectedRole, json.GetProperty("role").GetString());
        Assert.Equal(
            expectedCapabilities,
            json.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(ApiFactory.BusinessName, json.GetProperty("businessName").GetString());
        Assert.Equal(ApiFactory.BusinessTimeZoneId, json.GetProperty("timeZoneId").GetString());
    }

    /// <summary>
    /// The second business is what makes the previous answers meaningful: its own member is
    /// answered with its own name and zone, and nothing in the request chose either.
    /// </summary>
    [Fact]
    public async Task Answers_a_member_of_another_business_with_that_businesss_own_name_and_zone()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.OtherBusinessObjectId).GetAsync(Uri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);
        Assert.Equal("Manager", json.GetProperty("role").GetString());
        Assert.Equal(ApiFactory.OtherBusinessName, json.GetProperty("businessName").GetString());
        Assert.Equal(ApiFactory.OtherBusinessTimeZoneId, json.GetProperty("timeZoneId").GetString());
    }

    /// <summary>
    /// It describes the caller and nobody else: no other member of their own business, and no
    /// other business, appears anywhere in the response.
    /// </summary>
    [Fact]
    public async Task Says_nothing_about_other_members_or_other_businesses()
    {
        await _factory.SeedAsync();

        var body = await (await _factory.Authenticated(ApiFactory.OwnerObjectId).GetAsync(Uri))
            .Content.ReadAsStringAsync();

        Assert.DoesNotContain(ApiFactory.OtherBusinessName, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ApiFactory.OtherBusinessTimeZoneId, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ApiFactory.ManagerObjectId, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ApiFactory.OperatorObjectId, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ApiFactory.DirectoryTenantId, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("businessId", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_refused_with_401()
    {
        await _factory.SeedAsync();

        var response = await _factory.CreateClient().GetAsync(Uri);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The endpoint carries exactly the requirements every other business endpoint carries, and
    /// the required delegated scope is one of them. A member has to be able to ask what they may
    /// do, but only with a token that was issued for this API.
    /// </summary>
    [Fact]
    public async Task An_authenticated_caller_without_the_required_scope_is_refused()
    {
        await _factory.SeedAsync();

        var response = await _factory.WithoutScope(ApiFactory.OwnerObjectId).GetAsync(Uri);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_authenticated_caller_with_no_membership_is_refused_and_told_nothing()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.StrangerObjectId).GetAsync(Uri);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ApiFactory.BusinessName, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Owner", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fail-closed case this slice adds: a member whose stored role this deployment does not
    /// declare is refused outright. Reporting an empty capability list, or quietly treating them
    /// as the least privileged role, would both grant access nobody recorded.
    /// </summary>
    [Fact]
    public async Task A_member_whose_stored_role_is_not_declared_is_refused()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.UndeclaredRoleObjectId).GetAsync(Uri);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("capabilities", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ApiFactory.BusinessName, body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A member whose role was changed sees the change on their next request and not before:
    /// resolution is memoised per request only, so there is no cache to wait for and no sign-out
    /// needed.
    /// </summary>
    [Fact]
    public async Task A_changed_role_applies_on_the_next_request()
    {
        await _factory.SeedAsync();
        var client = _factory.Authenticated(ApiFactory.RoleChangeObjectId);

        var before = await ReadJsonAsync(await client.GetAsync(Uri));
        Assert.Equal("Operator", before.GetProperty("role").GetString());

        await _factory.SetRoleAsync(ApiFactory.RoleChangeObjectId, BusinessRole.Manager);

        var after = await ReadJsonAsync(await client.GetAsync(Uri));
        Assert.Equal("Manager", after.GetProperty("role").GetString());
        Assert.Contains(
            "Reports.View",
            after.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()));
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string BusinessName = "Vending Co";
        public const string BusinessTimeZoneId = "Australia/Sydney";
        public const string OtherBusinessName = "Second Vending Co";
        public const string OtherBusinessTimeZoneId = "America/New_York";

        public const string DirectoryTenantId = "99999999-9999-9999-9999-999999999999";
        public const string OwnerObjectId = "11111111-1111-1111-1111-111111111111";
        public const string ManagerObjectId = "22222222-2222-2222-2222-222222222222";
        public const string OperatorObjectId = "33333333-3333-3333-3333-333333333333";
        public const string OtherBusinessObjectId = "44444444-4444-4444-4444-444444444444";
        public const string StrangerObjectId = "55555555-5555-5555-5555-555555555555";
        public const string UndeclaredRoleObjectId = "66666666-6666-6666-6666-666666666666";
        public const string RoleChangeObjectId = "77777777-7777-7777-7777-777777777777";

        private const int BusinessId = 1;
        private const int OtherBusinessId = 2;

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

        /// <summary>
        /// Changes a member's stored role outside the request boundary, the way a future members
        /// page or an operator would.
        /// </summary>
        public async Task SetRoleAsync(string objectId, BusinessRole role)
        {
            await using var db = Database();
            var membership = await db.BusinessMemberships.SingleAsync(m => m.ObjectId == objectId);
            membership.Role = role;
            await db.SaveChangesAsync();
        }

        public async Task SeedAsync()
        {
            await using var db = Database();
            if (_seeded)
            {
                return;
            }

            db.Businesses.AddRange(
                new Business
                {
                    Id = BusinessId,
                    Name = BusinessName,
                    TimeZoneId = BusinessTimeZoneId,
                    CreatedAtUtc = CreatedAt,
                },
                new Business
                {
                    Id = OtherBusinessId,
                    Name = OtherBusinessName,
                    TimeZoneId = OtherBusinessTimeZoneId,
                    CreatedAtUtc = CreatedAt,
                });

            db.BusinessMemberships.AddRange(
                Membership(BusinessId, OwnerObjectId, BusinessRole.Owner),
                Membership(BusinessId, ManagerObjectId, BusinessRole.Manager),
                Membership(BusinessId, OperatorObjectId, BusinessRole.Operator),
                Membership(BusinessId, RoleChangeObjectId, BusinessRole.Operator),
                Membership(OtherBusinessId, OtherBusinessObjectId, BusinessRole.Manager),
                // A stored role no BusinessRole declares. In production it could only arrive from
                // a hand-written UPDATE or a newer deployment; the cast is how a test writes the
                // same thing.
                Membership(BusinessId, UndeclaredRoleObjectId, (BusinessRole)0));

            await db.SaveChangesAsync();
            _seeded = true;
        }

        private static BusinessMembership Membership(int businessId, string objectId, BusinessRole role) =>
            new()
            {
                BusinessId = businessId,
                DirectoryTenantId = DirectoryTenantId,
                ObjectId = objectId,
                Role = role,
                CreatedAtUtc = CreatedAt,
            };

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Testing applies pending migrations at startup, so the schema under these tests is
            // the migration history rather than a model snapshot.
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
