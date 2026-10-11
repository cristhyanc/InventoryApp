using System.Net;
using System.Net.Http.Json;
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
/// The <c>GET /api/me/account-state</c> contract (issue #523), exercised through the real request
/// pipeline: authentication, the required scope, the business-scope middleware with its new
/// membership-not-required marker, the controller, and relational SQLite with the real migration
/// history.
///
/// <para>Four identities in the same deployment are in the four states today's schema can hold -
/// a member, a person with no membership at all, a person whose membership was revoked, and a
/// member of a deactivated business - and each is answered with their own state. A second, fully
/// active business exists throughout, so an answer could only have come from the caller's own
/// records, and the response must mention neither it nor the caller's own business.</para>
///
/// <para><c>MembershipAmbiguous</c> is deliberately not here: since issue #522 the database
/// refuses to hold two active memberships for one identity, so it is covered where it can still
/// be constructed - <c>AccountStatePolicyTests</c> and <c>GetAccountStateTests</c>.</para>
/// </summary>
public sealed class MeAccountStateApiTests : IClassFixture<MeAccountStateApiTests.ApiFactory>
{
    private const string Uri = "/api/me/account-state";

    private readonly ApiFactory _factory;

    public MeAccountStateApiTests(ApiFactory factory) => _factory = factory;

    public static TheoryData<string, string> IdentitiesAndStates => new()
    {
        { ApiFactory.MemberObjectId, "Member" },
        { ApiFactory.StrangerObjectId, "NoMembership" },
        { ApiFactory.RevokedObjectId, "MembershipRevoked" },
        { ApiFactory.DeactivatedBusinessObjectId, "BusinessDeactivated" },
    };

    /// <summary>
    /// Every state this slice reports, each with the exact three-field response: the same
    /// deployment and the same database answer each identity with its own state.
    /// </summary>
    [Theory]
    [MemberData(nameof(IdentitiesAndStates))]
    public async Task Answers_each_identity_with_its_own_account_state(string objectId, string expectedState)
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(objectId).GetAsync(Uri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);

        Assert.Equal(
            ["onboardingEnabled", "rejectionReason", "state"],
            json.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(expectedState, json.GetProperty("state").GetString());
        Assert.False(json.GetProperty("onboardingEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("rejectionReason").ValueKind);
    }

    /// <summary>
    /// The response says which state the account is in and nothing else. No business name, no
    /// identifier for either business or for the caller, and no trace of the other business that
    /// exists in the same deployment - a person the application has not approved is not thereby
    /// entitled to learn which businesses it holds.
    /// </summary>
    [Theory]
    [InlineData(ApiFactory.MemberObjectId)]
    [InlineData(ApiFactory.RevokedObjectId)]
    [InlineData(ApiFactory.DeactivatedBusinessObjectId)]
    [InlineData(ApiFactory.StrangerObjectId)]
    public async Task Never_names_a_business_its_identifier_or_the_caller(string objectId)
    {
        await _factory.SeedAsync();

        var body = await (await _factory.Authenticated(objectId).GetAsync(Uri)).Content.ReadAsStringAsync();

        Assert.DoesNotContain(ApiFactory.BusinessName, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ApiFactory.OtherBusinessName, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ApiFactory.DeactivatedBusinessName, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("businessId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(objectId, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ApiFactory.DirectoryTenantId, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_refused_with_401()
    {
        await _factory.SeedAsync();

        var response = await _factory.CreateClient().GetAsync(Uri);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// "Membership not required" is not "sign-in not required", and it is not "any token will do"
    /// either: the endpoint keeps the delegated <c>access_as_user</c> scope every other endpoint
    /// requires, so a token that was not issued for this API reaches nothing.
    /// </summary>
    [Fact]
    public async Task An_authenticated_caller_without_the_required_scope_is_refused()
    {
        await _factory.SeedAsync();

        var response = await _factory.WithoutScope(ApiFactory.MemberObjectId).GetAsync(Uri);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The marker grants nothing anywhere else. The same callers that are answered above are still
    /// refused by the membership requirement on an ordinary business endpoint, on the sibling
    /// action of the very same controller, and on a write - so reaching the account-state endpoint
    /// has given them no access to anything.
    /// </summary>
    [Theory]
    [InlineData(ApiFactory.StrangerObjectId)]
    [InlineData(ApiFactory.RevokedObjectId)]
    [InlineData(ApiFactory.DeactivatedBusinessObjectId)]
    public async Task A_caller_answered_here_still_reaches_nothing_else(string objectId)
    {
        await _factory.SeedAsync();
        var client = _factory.Authenticated(objectId);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Uri)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/me/access")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/business/current")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/categories", new { name = "Should not be created" })).StatusCode);
    }

    /// <summary>
    /// The middleware's public 403 body is unchanged: it still carries the same single sentence
    /// and no hint of which state the caller is in. The account-state endpoint is the only place
    /// that distinction is visible, and only for the caller's own identity.
    /// </summary>
    [Theory]
    [InlineData(ApiFactory.StrangerObjectId)]
    [InlineData(ApiFactory.RevokedObjectId)]
    [InlineData(ApiFactory.DeactivatedBusinessObjectId)]
    public async Task The_public_403_body_is_unchanged(string objectId)
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(objectId).GetAsync("/api/products");
        var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Forbidden", json.GetProperty("title").GetString());
        Assert.Equal(
            "The signed-in account is not associated with a business in this application.",
            json.GetProperty("detail").GetString());
        Assert.Equal(403, json.GetProperty("status").GetInt32());

        var body = await response.Content.ReadAsStringAsync();
        foreach (var state in new[] { "MembershipRevoked", "BusinessDeactivated", "NoMembership", "Member" })
        {
            Assert.DoesNotContain(state, body, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A revocation applies on the next request, with no cache to wait for: the person who was a
    /// member a moment ago is told their membership was revoked, and the business they were a
    /// member of is still not named.
    /// </summary>
    [Fact]
    public async Task A_revocation_changes_the_state_on_the_next_request()
    {
        await _factory.SeedAsync();
        var client = _factory.Authenticated(ApiFactory.RevocationObjectId);

        var before = await ReadJsonAsync(await client.GetAsync(Uri));
        Assert.Equal("Member", before.GetProperty("state").GetString());

        await _factory.RevokeAsync(ApiFactory.RevocationObjectId);

        var after = await ReadJsonAsync(await client.GetAsync(Uri));
        Assert.Equal("MembershipRevoked", after.GetProperty("state").GetString());
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string BusinessName = "Vending Co";
        public const string OtherBusinessName = "Second Vending Co";
        public const string DeactivatedBusinessName = "Offboarded Vending Co";

        public const string DirectoryTenantId = "99999999-9999-9999-9999-999999999999";
        public const string MemberObjectId = "11111111-1111-1111-1111-111111111111";
        public const string StrangerObjectId = "22222222-2222-2222-2222-222222222222";
        public const string RevokedObjectId = "33333333-3333-3333-3333-333333333333";
        public const string DeactivatedBusinessObjectId = "44444444-4444-4444-4444-444444444444";
        public const string RevocationObjectId = "55555555-5555-5555-5555-555555555555";

        private const int BusinessId = 1;
        private const int OtherBusinessId = 2;
        private const int DeactivatedBusinessId = 3;

        private static readonly DateTime CreatedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime RevokedAt = new(2026, 6, 1, 4, 0, 0, DateTimeKind.Utc);

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

        /// <summary>A caller whose token carries no delegated scope at all.</summary>
        public HttpClient WithoutScope(string objectId)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.DirectoryTenantIdHeader, DirectoryTenantId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ObjectIdHeader, objectId);
            return client;
        }

        /// <summary>
        /// Revokes a membership outside the request boundary, the way the member management of
        /// issue #504 will: the row stays and records when its state changed.
        /// </summary>
        public async Task RevokeAsync(string objectId)
        {
            await using var db = Database();
            var membership = await db.BusinessMemberships.SingleAsync(m => m.ObjectId == objectId);
            membership.IsActive = false;
            membership.StatusChangedAtUtc = RevokedAt;
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
                Business(BusinessId, BusinessName),
                Business(OtherBusinessId, OtherBusinessName),
                Business(DeactivatedBusinessId, DeactivatedBusinessName, isActive: false));

            db.BusinessMemberships.AddRange(
                Membership(BusinessId, MemberObjectId),
                Membership(BusinessId, RevocationObjectId),
                // Revoked, and revoked later than it was created, so the state the row describes
                // is the one StatusChangedAtUtc records rather than its creation instant.
                Membership(BusinessId, RevokedObjectId, isActive: false, statusChangedAtUtc: RevokedAt),
                Membership(DeactivatedBusinessId, DeactivatedBusinessObjectId),
                // The other business has its own member, so it is a real business in this
                // deployment rather than an empty row.
                Membership(OtherBusinessId, "66666666-6666-6666-6666-666666666666"));

            await db.SaveChangesAsync();
            _seeded = true;
        }

        private static Business Business(int id, string name, bool isActive = true) =>
            new()
            {
                Id = id,
                Name = name,
                TimeZoneId = "Australia/Sydney",
                IsActive = isActive,
                CreatedAtUtc = CreatedAt,
            };

        private static BusinessMembership Membership(
            int businessId,
            string objectId,
            bool isActive = true,
            DateTime? statusChangedAtUtc = null) =>
            new()
            {
                BusinessId = businessId,
                DirectoryTenantId = DirectoryTenantId,
                ObjectId = objectId,
                Role = BusinessRole.Owner,
                IsActive = isActive,
                CreatedAtUtc = CreatedAt,
                StatusChangedAtUtc = statusChangedAtUtc ?? CreatedAt,
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
    /// request headers, so the membership path and the scope requirement are exercised exactly as
    /// they are in production without a real token.
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
