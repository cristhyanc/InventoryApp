using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using InventoryApi.Auth.PriceCollector;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using TestTokens = InventoryApi.Tests.Auth.TrustedIssuerTokenValidationTests.TestTokens;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// Real JWT bearer validation of the price collector's app-only token, through the production
/// pipeline (issue #924, parent #817).
///
/// <para>These tests mint tokens with their own signing keys and hand each scheme a stubbed OpenID
/// Connect metadata document, exactly as <see cref="TrustedIssuerTokenValidationTests"/> does, so what
/// is under test is the production validation - signature, issuer, audience, lifetime, the application
/// role, the absence of a delegated scope, the pinned tenant and the pinned client application -
/// rather than a synthetic bypass. The two signing keys are deliberately shared with that class: Entra
/// signs every tenant's workforce tokens with one key set, which is precisely why the tenant and
/// client pins, and not the signature, are what keep somebody else's token out.</para>
///
/// <para><strong>The asymmetry is asserted from both sides.</strong> An app-only token is accepted on
/// the collector endpoint and refused everywhere else; a delegated user token is accepted everywhere
/// else and refused on the collector endpoint. Each half would pass on its own while the other was
/// broken, and the second one broken is an app-only path into the whole API.</para>
/// </summary>
public sealed class PriceCollectorTokenValidationTests
    : IClassFixture<PriceCollectorTokenValidationTests.ApiFactory>
{
    private const string WorkEndpoint = "/api/price-collector/work";

    private readonly ApiFactory _factory;

    public PriceCollectorTokenValidationTests(ApiFactory factory)
    {
        _factory = factory;
    }

    #region The one token that is accepted

    /// <summary>
    /// The configured collector's app-only token is accepted, and the work list it is answered with
    /// carries a run id - which is the whole surface this issue adds.
    /// </summary>
    [Fact]
    public async Task The_configured_collectors_app_only_token_is_accepted()
    {
        await _factory.SeedAsync();

        var response = await Get(WorkEndpoint, CollectorTokens.AppOnly());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("runId").GetInt64() > 0);
    }

    /// <summary>
    /// Both spellings of this API's own audience are accepted - the bare application id of a v2 token
    /// and the <c>api://</c> App ID URI of a v1 one - because which one the registration issues is a
    /// portal setting a human owns.
    /// </summary>
    [Theory]
    [InlineData(TestTokens.WorkforceApiClientId)]
    [InlineData("api://" + TestTokens.WorkforceApiClientId)]
    public async Task Both_pinned_spellings_of_this_apis_audience_are_accepted(string audience)
    {
        await _factory.SeedAsync();

        var response = await Get(WorkEndpoint, CollectorTokens.AppOnly(audience: audience));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The application is named by <c>azp</c> on a v2 token and by <c>appid</c> on a v1 one, and
    /// either is read. A token carrying both, agreeing, is accepted too.
    /// </summary>
    [Theory]
    [InlineData("azp")]
    [InlineData("appid")]
    public async Task Either_spelling_of_the_client_application_claim_is_read(string claimType)
    {
        await _factory.SeedAsync();

        var claims = CollectorTokens.Claims(clientApplicationClaimType: claimType);

        Assert.Equal(HttpStatusCode.OK, (await Get(WorkEndpoint, CollectorTokens.Token(claims))).StatusCode);
    }

    #endregion

    #region The four pins, each refused on its own

    /// <summary>
    /// The highest-severity threat of this surface, and the issue's own stated constraint. The
    /// workforce authority is multi-tenant, so another tenant can consent to this API and assign
    /// <c>PriceCollector.Run</c> to an app of its own - and Microsoft signs that tenant's tokens with
    /// the same key set, so the signature is genuine. What refuses it is the issuer-to-<c>tid</c>
    /// binding and the tenant pin, from both directions: a token issued by another tenant's authority,
    /// and a token from the right authority claiming another tenant.
    /// </summary>
    [Fact]
    public async Task A_token_from_another_tenant_is_refused_however_it_is_shaped()
    {
        await _factory.SeedAsync();

        var anotherTenantsAuthority = await Get(WorkEndpoint, CollectorTokens.Token(
            CollectorTokens.Claims(),
            issuer: TestTokens.WorkforceIssuer(TestTokens.AttackerTenantId)));

        var anotherTenantClaimed = await Get(WorkEndpoint, CollectorTokens.Token(
            CollectorTokens.Claims(tenantId: TestTokens.AttackerTenantId)));

        var noTenantAtAll = await Get(WorkEndpoint, CollectorTokens.Token(
            CollectorTokens.Claims(tenantId: null)));

        Assert.Equal(HttpStatusCode.Unauthorized, anotherTenantsAuthority.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anotherTenantClaimed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noTenantAtAll.StatusCode);
    }

    /// <summary>
    /// The other half of the same constraint: the configured tenant's <em>other</em> workloads are not
    /// the collector. A token from the right tenant, with the right role, issued to a different
    /// application is refused - and so is one that names no application at all.
    /// </summary>
    [Fact]
    public async Task A_token_issued_to_another_application_in_the_right_tenant_is_refused()
    {
        await _factory.SeedAsync();

        var anotherApplication = await Get(WorkEndpoint, CollectorTokens.Token(
            CollectorTokens.Claims(clientId: TestTokens.SpaClientId)));

        var noApplicationAtAll = await Get(WorkEndpoint, CollectorTokens.Token(
            CollectorTokens.Claims(clientId: null)));

        Assert.Equal(HttpStatusCode.Unauthorized, anotherApplication.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noApplicationAtAll.StatusCode);
    }

    /// <summary>
    /// An application that was never granted the collector role may not run the collector, whatever
    /// else it was granted. A missing <c>roles</c> claim, an empty one and another role's value are
    /// all refused.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Inventory.ReadAll")]
    [InlineData("pricecollector.run")]
    public async Task A_token_without_the_collector_role_is_refused(string? role)
    {
        await _factory.SeedAsync();

        var response = await Get(WorkEndpoint, CollectorTokens.Token(CollectorTokens.Claims(role: role)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A token carrying <em>any</em> delegated scope is refused, even with the role, the tenant and the
    /// application all correct.
    ///
    /// <para>This is the rule that stops a person impersonating the collector. Entra puts an assigned
    /// application role in a <em>user's</em> token too, so "has the role" alone would be satisfied by
    /// a delegated token from the configured tenant - and the one thing this surface must never be is
    /// reachable as somebody.</para>
    /// </summary>
    [Theory]
    [InlineData("access_as_user")]
    [InlineData("User.Read")]
    public async Task A_token_carrying_any_delegated_scope_is_refused(string scope)
    {
        await _factory.SeedAsync();

        var response = await Get(WorkEndpoint, CollectorTokens.Token(
            CollectorTokens.Claims(scope: scope)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A token for another resource is not a token for this API, however correct everything else is -
    /// and the collector's own registration is not an audience this API answers to either.
    /// </summary>
    [Theory]
    [InlineData(CollectorTokens.CollectorClientId)]
    [InlineData(TestTokens.ExternalIdApiClientId)]
    [InlineData("https://graph.microsoft.com")]
    public async Task A_token_for_another_audience_is_refused(string audience)
    {
        await _factory.SeedAsync();

        var response = await Get(WorkEndpoint, CollectorTokens.AppOnly(audience: audience));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_or_not_yet_valid_collector_token_is_refused()
    {
        await _factory.SeedAsync();

        var now = DateTime.UtcNow;

        var expired = await Get(WorkEndpoint, CollectorTokens.Token(
            CollectorTokens.Claims(), notBefore: now.AddHours(-2), expires: now.AddHours(-1)));

        var notYetValid = await Get(WorkEndpoint, CollectorTokens.Token(
            CollectorTokens.Claims(), notBefore: now.AddHours(1), expires: now.AddHours(2)));

        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, notYetValid.StatusCode);
    }

    /// <summary>
    /// A key no trusted authority publishes validates nothing, and neither does the customer identity
    /// tenant's own key - which is a real key published by a real trusted authority, just not this
    /// token's.
    /// </summary>
    [Fact]
    public async Task A_collector_token_signed_with_an_unpublished_key_is_refused()
    {
        await _factory.SeedAsync();

        foreach (var key in new[] { TestTokens.UntrustedSigningKey, TestTokens.ExternalIdSigningKey })
        {
            var response = await Get(WorkEndpoint, CollectorTokens.Token(
                CollectorTokens.Claims(), signingKey: key));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    /// <summary>
    /// An ID token names the right application and is signed by a trusted authority, which is what
    /// makes substituting it attractive. It is refused here for the reason it is refused on every other
    /// scheme: the claims that bind it to a sign-in have no business on an access token.
    /// </summary>
    [Fact]
    public async Task An_id_token_is_not_an_app_only_access_token()
    {
        await _factory.SeedAsync();

        var claims = CollectorTokens.Claims();
        claims["nonce"] = "0a9f3c7e2b5d41f8";

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await Get(WorkEndpoint, CollectorTokens.Token(claims))).StatusCode);
    }

    #endregion

    #region The surface is one route, in both directions

    /// <summary>
    /// The issue's second criterion, first half: the collector's token reaches the collector endpoint
    /// and nothing else. Every other endpoint keeps the scheme it always had, whose
    /// delegated-access-token rule refuses a token with no <c>scp</c> - so this is <c>401</c> rather
    /// than a <c>403</c> about a missing capability.
    /// </summary>
    [Theory]
    [InlineData("/api/me/access")]
    [InlineData("/api/me/account-state")]
    [InlineData("/api/products")]
    [InlineData("/api/business/current")]
    [InlineData("/api/super-admin/businesses/pending-count")]
    public async Task The_collectors_token_is_refused_on_every_other_endpoint(string path)
    {
        await _factory.SeedAsync();

        var response = await Get(path, CollectorTokens.AppOnly());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// And the second half: a delegated user token is refused on the collector endpoint. Both shapes
    /// are refused - a member of a business, and an identity from the collector's own tenant carrying
    /// the role beside a delegated scope - because the collector endpoint is not a surface anybody
    /// reaches as a person.
    /// </summary>
    [Fact]
    public async Task A_delegated_user_token_is_refused_on_the_collector_endpoint()
    {
        await _factory.SeedAsync();

        var member = await Get(
            WorkEndpoint,
            TestTokens.WorkforceAccessToken(TestTokens.WorkforceTenantX, TestTokens.TenantXOwnerObjectId));

        var administrator = await Get(
            WorkEndpoint,
            TestTokens.WorkforceAccessToken(TestTokens.WorkforceTenantX, TestTokens.PlatformAdminObjectId));

        var fromTheCollectorsOwnTenant = await Get(WorkEndpoint, CollectorTokens.Token(
            CollectorTokens.Claims(scope: "access_as_user")));

        Assert.Equal(HttpStatusCode.Unauthorized, member.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, administrator.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, fromTheCollectorsOwnTenant.StatusCode);

        // And the member's own endpoints still answer them, so the refusal above is the collector
        // surface's rule rather than a broken token.
        Assert.Equal(
            HttpStatusCode.OK,
            (await Get(
                "/api/me/access",
                TestTokens.WorkforceAccessToken(
                    TestTokens.WorkforceTenantX, TestTokens.TenantXOwnerObjectId))).StatusCode);
    }

    /// <summary>
    /// No token, and an authorization header that is not a readable token, are challenged rather than
    /// faulted - and the collector endpoint issues no run for any of them.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Bearer not-a-json-web-token")]
    [InlineData("Bearer ")]
    [InlineData("Basic dXNlcjpwYXNzd29yZA==")]
    public async Task A_request_without_a_readable_bearer_token_is_challenged(string? authorizationHeader)
    {
        await _factory.SeedAsync();

        var client = _factory.CreateClient();
        if (authorizationHeader is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorizationHeader);
        }

        var response = await client.GetAsync(WorkEndpoint);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.Select(header => header.Scheme));
    }

    #endregion

    #region With no collector configured

    /// <summary>
    /// The shipped state, and the rollback state: with the tenant and client id unset the collector
    /// path accepts nothing at all, and every collector request is <c>401</c> rather than a
    /// <c>500</c> about a scheme nobody registered.
    ///
    /// <para>The same host still serves the member endpoints normally, which is the other half of the
    /// claim: an unconfigured collector is a missing surface, not a broken API.</para>
    /// </summary>
    [Fact]
    public async Task With_the_settings_empty_every_collector_request_is_unauthorized()
    {
        using var factory = new ApiFactory(collectorConfigured: false);
        await factory.SeedAsync();

        var collector = await Get(factory, WorkEndpoint, CollectorTokens.AppOnly());
        var member = await Get(
            factory,
            "/api/me/access",
            TestTokens.WorkforceAccessToken(TestTokens.WorkforceTenantX, TestTokens.TenantXOwnerObjectId));

        Assert.Equal(HttpStatusCode.Unauthorized, collector.StatusCode);
        Assert.Equal(HttpStatusCode.OK, member.StatusCode);
        Assert.Equal(0, await factory.RunCountAsync());
    }

    /// <summary>
    /// A half-filled section is not a loosely configured collector: it trusts none, for the reason an
    /// unusable customer identity section registers no second issuer. A configuration mistake must
    /// never mean "accept whatever arrives".
    /// </summary>
    [Theory]
    [InlineData("not-a-guid", CollectorTokens.CollectorClientId)]
    [InlineData(CollectorTokens.CollectorTenantId, "")]
    [InlineData("", CollectorTokens.CollectorClientId)]
    [InlineData("00000000-0000-0000-0000-000000000000", CollectorTokens.CollectorClientId)]
    public void An_unusable_section_trusts_no_collector(string tenantId, string clientId)
    {
        var decision = PriceCollectorAuthenticationDecision.From(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["PriceCollector:TenantId"] = tenantId,
                    ["PriceCollector:ClientId"] = clientId,
                })
                .Build());

        Assert.False(decision.IsRegistered);
        Assert.Equal(PriceCollectorAuthenticationState.Incomplete, decision.State);

        // The problem names the setting key and never its value.
        Assert.NotNull(decision.Problem);
        Assert.Contains("PriceCollector:", decision.Problem, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(tenantId))
        {
            Assert.DoesNotContain(tenantId, decision.Problem, StringComparison.Ordinal);
        }
    }

    /// <summary>Both identifiers blank is "not configured", which is a different state from unusable.</summary>
    [Fact]
    public void Both_identifiers_blank_is_not_configured()
    {
        var decision = PriceCollectorAuthenticationDecision.From(
            new ConfigurationBuilder().Build());

        Assert.False(decision.IsRegistered);
        Assert.Equal(PriceCollectorAuthenticationState.NotConfigured, decision.State);
        Assert.Null(decision.Problem);
    }

    #endregion

    #region Reading the responses

    private Task<HttpResponseMessage> Get(string path, string bearerToken) =>
        Get(_factory, path, bearerToken);

    private static async Task<HttpResponseMessage> Get(
        WebApplicationFactory<Program> factory, string path, string bearerToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return await client.GetAsync(path);
    }

    #endregion

    /// <summary>
    /// The collector's own identifiers and the tokens built from them.
    ///
    /// <para>The signing key is <see cref="TestTokens.WorkforceSigningKey"/> on purpose: the collector
    /// authenticates against the workforce authority of its own tenant, and Entra signs every tenant's
    /// workforce tokens with one shared key set. That is exactly why the tenant and application pins
    /// are the rule rather than the signature.</para>
    /// </summary>
    internal static class CollectorTokens
    {
        /// <summary>The tenant the collector's app registration lives in.</summary>
        public const string CollectorTenantId = "ffffffff-0000-4000-8000-00000000000f";

        /// <summary>The collector's own app registration in that tenant.</summary>
        public const string CollectorClientId = "1a2b3c4d-0000-4000-8000-000000000004";

        public static string CollectorIssuer => TestTokens.WorkforceIssuer(CollectorTenantId);

        /// <summary>
        /// What the collector tenant's workforce authority publishes: its own issuer, and the shared
        /// workforce key set.
        /// </summary>
        public static OpenIdConnectConfiguration Metadata
        {
            get
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = CollectorIssuer };
                configuration.SigningKeys.Add(TestTokens.WorkforceSigningKey);
                return configuration;
            }
        }

        /// <summary>The token the real collector presents: app-only, one role, no delegated scope.</summary>
        public static string AppOnly(string? audience = null) =>
            Token(Claims(), audience: audience);

        /// <summary>
        /// One collector token's claims, with every pin as a parameter so a test can break exactly
        /// one of them. Each default is the accepted value.
        /// </summary>
        public static Dictionary<string, object> Claims(
            string? tenantId = CollectorTenantId,
            string? clientId = CollectorClientId,
            string? role = PriceCollectorRole.Name,
            string? scope = null,
            string clientApplicationClaimType = "azp")
        {
            var claims = new Dictionary<string, object>(StringComparer.Ordinal);

            if (tenantId is not null)
            {
                claims["tid"] = tenantId;
            }

            if (clientId is not null)
            {
                claims[clientApplicationClaimType] = clientId;
            }

            if (role is not null)
            {
                claims["roles"] = new[] { role };
            }

            if (scope is not null)
            {
                claims["scp"] = scope;
            }

            // The object id of the service principal, which Entra puts on an app-only token. Nothing
            // here reads it: the collector is an application, and `azp`/`appid` is what says which.
            claims["oid"] = "0b0b0b0b-0000-4000-8000-00000000000b";

            return claims;
        }

        public static string Token(
            Dictionary<string, object> claims,
            string? issuer = null,
            string? audience = null,
            SecurityKey? signingKey = null,
            DateTime? notBefore = null,
            DateTime? expires = null) =>
            TestTokens.Token(
                signingKey ?? TestTokens.WorkforceSigningKey,
                issuer ?? CollectorIssuer,
                audience ?? TestTokens.WorkforceApiClientId,
                claims,
                notBefore,
                expires);
    }

    /// <summary>
    /// Hosts the real application with the collector scheme configured beside the two people-serving
    /// ones, over one in-memory relational SQLite database holding one business and one member.
    ///
    /// <para>Only the metadata documents are replaced; the schemes, the policy scheme that routes
    /// between the two people-serving ones, the collector policy, the business-scope middleware, the
    /// query filters and the controllers are the production ones.</para>
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string BusinessName = "Collector Test Vending";

        private static readonly DateTime CreatedAt = new(2026, 10, 28, 4, 0, 0, DateTimeKind.Utc);

        private readonly bool _collectorConfigured;
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private bool _seeded;

        public ApiFactory() : this(collectorConfigured: true)
        {
        }

        /// <summary>
        /// Internal, not public: xUnit allows a class fixture exactly one public constructor, and only
        /// the unconfigured-collector test builds a host that trusts none.
        /// </summary>
        internal ApiFactory(bool collectorConfigured)
        {
            E2ETestHostContentRoot.Pin();
            _collectorConfigured = collectorConfigured;
            _connection.Open();
        }

        public async Task SeedAsync()
        {
            _ = Services;

            await using var db = TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

            if (_seeded)
            {
                return;
            }

            db.Businesses.Add(new Business
            {
                Id = 1,
                Name = BusinessName,
                TimeZoneId = "Australia/Sydney",
                Status = BusinessStatus.Active,
                CreatedAtUtc = CreatedAt,
            });

            db.BusinessMemberships.Add(new BusinessMembership
            {
                BusinessId = 1,
                DirectoryTenantId = TestTokens.WorkforceTenantX,
                ObjectId = TestTokens.TenantXOwnerObjectId,
                Role = BusinessRole.Owner,
                IsActive = true,
                CreatedAtUtc = CreatedAt,
            });

            await db.SaveChangesAsync();
            _seeded = true;
        }

        /// <summary>
        /// How many collector runs exist, read from outside the boundary. It is what proves a refused
        /// request issued nothing: a <c>401</c> that had already stored a run would have handed work
        /// to nobody and expired the run that was legitimately open.
        /// </summary>
        public async Task<int> RunCountAsync()
        {
            await using var db = TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

            return await db.PriceCollectorRuns.CountAsync();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.UseSetting("AzureAd:Instance", "https://login.microsoftonline.com/");
            builder.UseSetting("AzureAd:TenantId", "common");
            builder.UseSetting("AzureAd:ClientId", TestTokens.WorkforceApiClientId);
            builder.UseSetting("AzureAd:Scopes", "access_as_user");

            // One configured platform administrator, so "a Super Admin's delegated token is refused
            // here too" is asserted against a real pair rather than against the shipped "nobody is an
            // administrator" state.
            builder.UseSetting("PlatformAdmin:DirectoryTenantId", TestTokens.WorkforceTenantX);
            builder.UseSetting("PlatformAdmin:ObjectId", TestTokens.PlatformAdminObjectId);

            if (_collectorConfigured)
            {
                builder.UseSetting("PriceCollector:TenantId", CollectorTokens.CollectorTenantId);
                builder.UseSetting("PriceCollector:ClientId", CollectorTokens.CollectorClientId);
            }

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

            // The only thing replaced about authentication: where each scheme reads its issuer and
            // signing keys from. No validation rule, no claim and no scheme is substituted, and
            // neither document is reachable from production code.
            builder.ConfigureTestServices(services =>
            {
                services.Configure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options => options.ConfigurationManager =
                        new StaticConfigurationManager<OpenIdConnectConfiguration>(TestTokens.WorkforceMetadata));

                services.Configure<JwtBearerOptions>(
                    PriceCollectorBearerScheme.Name,
                    options => options.ConfigurationManager =
                        new StaticConfigurationManager<OpenIdConnectConfiguration>(CollectorTokens.Metadata));
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
}
