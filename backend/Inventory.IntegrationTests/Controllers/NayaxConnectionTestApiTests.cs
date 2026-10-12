using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Nayax;
using InventoryApi.Tests.Application.Time;
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
/// <c>POST /api/settings/nayax-connection/test</c> through the real request pipeline (issue #536):
/// authentication, the required scope, the <c>Integration.Manage</c> capability policy,
/// <c>BusinessScopeMiddleware</c>, the controller, the real draft store with real AES-GCM encryption
/// and relational SQLite with the real migration history.
///
/// <para>The check runner is the one thing substituted, with a fake that records what it was asked
/// to test and answers a scripted report. No test here makes a Nayax call: the runner's own
/// behaviour against a fake HTTP handler is
/// <c>Infrastructure.Nayax.NayaxConnectionCheckRunnerTests</c>.</para>
///
/// <para>What is under test here is the contract at the boundary: the published shape, that the
/// response never repeats the submitted token, that the draft that lands in the database holds it
/// only as ciphertext, that a missing field is a <c>400</c> before anything is sent anywhere, and
/// that the rate limit is a <c>429</c> with a usable <c>Retry-After</c>.</para>
/// </summary>
public sealed class NayaxConnectionTestApiTests : IClassFixture<NayaxConnectionTestApiTests.ApiFactory>
{
    // Obviously fake values. No real token or operator account belongs in a test.
    private const string OperatorId = "2002736764";
    private const string Token = "fake-token-not-a-real-credential";
    private const string Route = "/api/settings/nayax-connection/test";

    private readonly ApiFactory _factory;

    public NayaxConnectionTestApiTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_test_reports_every_check_line_and_an_opaque_expiring_draft_id()
    {
        await _factory.ResetAsync();

        var response = await Post(ApiFactory.OwnerObjectId, OperatorId, Token);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var body = await response.Content.ReadFromJsonAsync<TestResponse>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body!.DraftId);
        Assert.True(body.NothingFailed);
        Assert.Equal(1, body.MachineCount);
        Assert.Equal(
            new[]
            {
                "SignIn", "Machines", "Machine", "ProductCatalogue", "ProductGroups",
                "MachineProducts", "MachineLastSales", "MachineLastAlerts",
            },
            body.Checks.Select(check => check.Check).ToArray());
        Assert.All(body.Checks, check => Assert.Equal("Passed", check.Status));
        Assert.All(body.Checks, check => Assert.Null(check.Failure));
        Assert.True(body.ExpiresAtUtc > DateTime.UtcNow);
        Assert.True(body.ExpiresAtUtc <= DateTime.UtcNow.AddMinutes(15));
    }

    /// <summary>
    /// A failed line names its cause in the published vocabulary, so the wizard of #538 has
    /// something to render without inventing a reason.
    /// </summary>
    [Fact]
    public async Task A_failed_check_is_reported_with_its_cause()
    {
        await _factory.ResetAsync();
        _factory.Runner.Report = new NayaxConnectionCheckReport(
            [
                NayaxConnectionCheckLine.Passed(NayaxConnectionCheck.SignIn),
                NayaxConnectionCheckLine.Failed(
                    NayaxConnectionCheck.ProductCatalogue,
                    NayaxConnectionCheckFailure.OperatorIdMismatch),
                NayaxConnectionCheckLine.NotChecked(NayaxConnectionCheck.MachineLastSales),
            ],
            null);

        var response = await Post(ApiFactory.OwnerObjectId, OperatorId, Token);
        var body = await response.Content.ReadFromJsonAsync<TestResponse>();

        Assert.False(body!.NothingFailed);
        Assert.Null(body.MachineCount);
        Assert.Equal(
            ("ProductCatalogue", "Failed", "OperatorIdMismatch"),
            body.Checks.Select(check => (check.Check, check.Status, check.Failure))
                .Single(check => check.Status == "Failed"));
        Assert.Contains(("MachineLastSales", "NotChecked", (string?)null), body.Checks
            .Select(check => (check.Check, check.Status, check.Failure)));
    }

    [Fact]
    public async Task The_checks_are_run_with_the_submitted_credentials()
    {
        await _factory.ResetAsync();

        await Post(ApiFactory.OwnerObjectId, OperatorId, Token);

        var candidate = Assert.Single(_factory.Runner.Candidates);
        Assert.Equal(OperatorId, candidate.OperatorId);
        Assert.Equal(Token, candidate.AccessToken);
    }

    /// <summary>
    /// The response never repeats the secret the Owner typed - not in the body, not in a header -
    /// so it cannot end up in a browser cache, a proxy log or an error report.
    /// </summary>
    [Fact]
    public async Task The_response_never_echoes_the_submitted_token()
    {
        await _factory.ResetAsync();

        var response = await Post(ApiFactory.OwnerObjectId, OperatorId, Token);
        var body = await response.Content.ReadAsStringAsync();
        var headers = response.Headers.ToString() + response.Content.Headers;

        Assert.DoesNotContain(Token, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Token, headers, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The draft that lands in the database holds the operator id in the clear - it is a remote
    /// identity, not a secret - and the token only as ciphertext, bound to this business and to the
    /// Owner who ran the test.
    /// </summary>
    [Fact]
    public async Task The_draft_is_stored_encrypted_and_bound_to_the_business_and_the_owner()
    {
        await _factory.ResetAsync();

        var response = await Post(ApiFactory.OwnerObjectId, OperatorId, Token);
        var body = await response.Content.ReadFromJsonAsync<TestResponse>();

        await using var db = _factory.Database();
        var draft = await db.NayaxConnectionTestDrafts.AsNoTracking().SingleAsync();

        Assert.Equal(body!.DraftId, draft.Id);
        Assert.Equal(ApiFactory.SeededBusinessId, draft.BusinessId);
        Assert.Equal(OperatorId, draft.OperatorId);
        Assert.Equal(ApiFactory.DirectoryTenantId.ToUpperInvariant(), draft.CreatedByDirectoryTenantId);
        Assert.Equal(ApiFactory.OwnerObjectId.ToUpperInvariant(), draft.CreatedByObjectId);
        Assert.NotEqual(Token, draft.AccessTokenCiphertext);
        Assert.DoesNotContain(Token, draft.AccessTokenCiphertext, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, draft.ResultsJson, StringComparison.Ordinal);
        Assert.Equal(ApiFactory.EncryptionKeyId, draft.EncryptionKeyId);
        Assert.Equal(body.ExpiresAtUtc, draft.ExpiresAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_second_test_replaces_the_business_s_one_draft()
    {
        await _factory.ResetAsync();

        var first = await (await Post(ApiFactory.OwnerObjectId, OperatorId, Token))
            .Content.ReadFromJsonAsync<TestResponse>();
        var second = await (await Post(ApiFactory.OwnerObjectId, OperatorId, "another-fake-token"))
            .Content.ReadFromJsonAsync<TestResponse>();

        Assert.NotEqual(first!.DraftId, second!.DraftId);

        await using var db = _factory.Database();
        var draft = await db.NayaxConnectionTestDrafts.AsNoTracking().SingleAsync();
        Assert.Equal(second.DraftId, draft.Id);
    }

    /// <summary>
    /// Testing a candidate token changes nothing about the credentials the business is actually
    /// using: an Owner trying a wrong token must not be able to break a working connection.
    /// </summary>
    [Fact]
    public async Task A_test_leaves_the_stored_connection_untouched()
    {
        await _factory.ResetAsync();
        var before = await _factory.StoredConnectionAsync();

        await Post(ApiFactory.OwnerObjectId, "9009009009", "yet-another-fake-token");

        Assert.Equal(before, await _factory.StoredConnectionAsync());
    }

    [Theory]
    [InlineData(null, Token)]
    [InlineData("", Token)]
    [InlineData("   ", Token)]
    [InlineData(OperatorId, null)]
    [InlineData(OperatorId, "")]
    [InlineData(OperatorId, "  ")]
    public async Task A_missing_field_is_refused_before_anything_is_tested_or_stored(
        string? operatorId, string? token)
    {
        await _factory.ResetAsync();

        var response = await Post(ApiFactory.OwnerObjectId, operatorId, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Runner.Candidates);

        await using var db = _factory.Database();
        Assert.Empty(await db.NayaxConnectionTestDrafts.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The sixth run inside the window is refused with a status a client can act on and a
    /// <c>Retry-After</c> it can wait out, and it makes no Nayax call at all.
    /// </summary>
    [Fact]
    public async Task The_sixth_test_in_the_window_is_refused_with_a_retry_after()
    {
        await _factory.ResetAsync();

        for (var run = 0; run < NayaxConnectionTestRateLimiter.MaxRunsPerWindow; run++)
        {
            Assert.Equal(
                HttpStatusCode.OK,
                (await Post(ApiFactory.OwnerObjectId, OperatorId, Token)).StatusCode);
        }

        var allowedRuns = _factory.Runner.Candidates.Count;
        var response = await Post(ApiFactory.OwnerObjectId, OperatorId, Token);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(allowedRuns, _factory.Runner.Candidates.Count);

        var retryAfter = Assert.Single(response.Headers.GetValues("Retry-After"));
        var seconds = int.Parse(retryAfter, CultureInfo.InvariantCulture);
        Assert.InRange(seconds, 1, (int)NayaxConnectionTestRateLimiter.Window.TotalSeconds);
    }

    /// <summary>
    /// The refusal says nothing about how often this business has tested, and nothing about the
    /// token it was carrying.
    /// </summary>
    [Fact]
    public async Task A_rate_limited_refusal_leaks_nothing()
    {
        await _factory.ResetAsync();
        for (var run = 0; run <= NayaxConnectionTestRateLimiter.MaxRunsPerWindow; run++)
        {
            await Post(ApiFactory.OwnerObjectId, OperatorId, Token);
        }

        var response = await Post(ApiFactory.OwnerObjectId, OperatorId, Token);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.DoesNotContain(Token, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(OperatorId, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unauthenticated_call_is_401_and_tests_nothing()
    {
        await _factory.ResetAsync();

        var response = await _factory.CreateClient().PostAsJsonAsync(
            Route, new { operatorId = OperatorId, token = Token });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_factory.Runner.Candidates);
    }

    /// <summary>
    /// The capability matrix is pinned centrally by <c>CapabilityAuthorizationApiTests</c>; what is
    /// asserted here is the consequence that matters for a secret - a refused caller's submitted
    /// token is never sent anywhere and never stored.
    /// </summary>
    [Theory]
    [InlineData(ApiFactory.ManagerObjectId)]
    [InlineData(ApiFactory.OperatorObjectId)]
    [InlineData(ApiFactory.StrangerObjectId)]
    public async Task A_caller_without_the_capability_is_refused_and_their_token_is_never_used(string objectId)
    {
        await _factory.ResetAsync();

        var response = await Post(objectId, OperatorId, Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.Runner.Candidates);

        await using var db = _factory.Database();
        Assert.Empty(await db.NayaxConnectionTestDrafts.AsNoTracking().ToListAsync());
    }

    private Task<HttpResponseMessage> Post(string objectId, string? operatorId, string? token) =>
        _factory.Authenticated(objectId).PostAsJsonAsync(Route, new { operatorId, token });

    private sealed record TestResponse(
        Guid DraftId,
        DateTime ExpiresAtUtc,
        int? MachineCount,
        bool NothingFailed,
        IReadOnlyList<CheckResponse> Checks);

    private sealed record CheckResponse(string Check, string Status, string? Failure);

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string DirectoryTenantId = "99999999-9999-9999-9999-999999999999";
        public const string OwnerObjectId = "11111111-1111-1111-1111-111111111111";
        public const string ManagerObjectId = "22222222-2222-2222-2222-222222222222";
        public const string OperatorObjectId = "33333333-3333-3333-3333-333333333333";
        public const string StrangerObjectId = "55555555-5555-5555-5555-555555555555";

        public const int SeededBusinessId = 1;
        public const string EncryptionKeyId = "2026-10";

        /// <summary>The business's existing, accepted connection, which a test must never disturb.</summary>
        private const string StoredOperatorId = "1001001001";
        private const string StoredCiphertext = "c3RvcmVkLWNpcGhlcnRleHQtbm90LWEtdG9rZW4=";

        private static readonly DateTime CreatedAt = new(2026, 10, 11, 4, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        /// <summary>
        /// The rate limiter's own clock, so one test's spent allowance does not leak into the next.
        /// Deliberately not the application's clock: the draft's created and expiry instants are
        /// still the real ones, which is what the expiry assertions read.
        /// </summary>
        private readonly FakeClock _limiterClock = new(CreatedAt);

        private bool _seeded;

        public ApiFactory() => _connection.Open();

        public FakeCheckRunner Runner { get; } = new();

        /// <summary>An unrestricted context for arranging and inspecting state outside a request.</summary>
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

        /// <summary>
        /// Puts every test back to the same starting point: no draft, no recorded candidate, and a
        /// rate-limit window with nothing in it.
        ///
        /// <para>The limiter is a singleton that outlives one test, as it does in production, so the
        /// window is drained by moving its own clock past it rather than by replacing the instance -
        /// which is exactly what a real ten-minute wait does, and keeps the production registration
        /// shape under test.</para>
        /// </summary>
        public async Task ResetAsync()
        {
            await SeedAsync();
            Runner.Reset();

            await using var db = Database();
            await db.NayaxConnectionTestDrafts.ExecuteDeleteAsync();
            _limiterClock.UtcNow += NayaxConnectionTestRateLimiter.Window + TimeSpan.FromMinutes(1);
        }

        /// <summary>The stored connection's own row, as text, so "untouched" is a byte comparison.</summary>
        public async Task<string> StoredConnectionAsync()
        {
            await using var db = Database();
            var row = await db.BusinessNayaxConnections.AsNoTracking().SingleAsync();
            return string.Join(
                '|',
                row.BusinessId,
                row.OperatorId,
                row.AccessTokenCiphertext,
                row.EncryptionKeyId,
                row.Status,
                row.CredentialRevision,
                row.LastTestedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                row.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
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

                // The one behavioural substitution: no test here may reach Nayax. Everything else -
                // the capability policy, the business scope, the use case, the real encrypting
                // draft store - is the production registration.
                services.AddSingleton<INayaxConnectionCheckRunner>(Runner);

                // The real limiter over a clock this fixture advances, so each test starts from an
                // empty window. The rule under test is the limiter's; only its sense of time moves.
                services.AddSingleton(new NayaxConnectionTestRateLimiter(_limiterClock));

                // The real AES-GCM protector with a test key, so the draft's token is genuinely
                // encrypted at rest here rather than refused by the fail-closed unconfigured one.
                // Registered as a service rather than as configuration, because this host reads its
                // encryption settings while Program runs, before a test configuration source could
                // reach it.
                var protection = new NayaxTokenProtectionOptions { ActiveKeyId = EncryptionKeyId };
                protection.Keys[EncryptionKeyId] = Convert.ToBase64String(
                    Enumerable.Repeat((byte)7, NayaxTokenProtectionConfiguration.KeySizeInBytes).ToArray());
                services.AddSingleton<INayaxTokenProtector>(new AesGcmNayaxTokenProtector(protection));
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

        private async Task SeedAsync()
        {
            await using var db = Database();
            if (_seeded)
            {
                return;
            }

            db.Businesses.Add(new Business
            {
                Id = SeededBusinessId,
                Name = "Vending Co",
                TimeZoneId = "Australia/Sydney",
                Status = BusinessStatus.Active,
                CreatedAtUtc = CreatedAt,
            });

            db.BusinessMemberships.AddRange(
                Membership(OwnerObjectId, BusinessRole.Owner),
                Membership(ManagerObjectId, BusinessRole.Manager),
                Membership(OperatorObjectId, BusinessRole.Operator));

            // The business already has an accepted connection, so "a test changes nothing about the
            // credentials in use" is a claim about a real row rather than about an absent one.
            db.BusinessNayaxConnections.Add(new BusinessNayaxConnection
            {
                BusinessId = SeededBusinessId,
                OperatorId = StoredOperatorId,
                AccessTokenCiphertext = StoredCiphertext,
                EncryptionKeyId = EncryptionKeyId,
                Status = NayaxConnectionStatus.Ready,
                CredentialRevision = 3,
                LastTestedAtUtc = CreatedAt,
                UpdatedAtUtc = CreatedAt,
            });

            await db.SaveChangesAsync();
            _seeded = true;
        }

        private static BusinessMembership Membership(string objectId, BusinessRole role) =>
            new()
            {
                BusinessId = SeededBusinessId,
                DirectoryTenantId = DirectoryTenantId,
                ObjectId = objectId,
                Role = role,
                CreatedAtUtc = CreatedAt,
            };
    }

    /// <summary>
    /// A check runner that records the candidates it was handed and answers a scripted report, so no
    /// test in this file reaches a network at all.
    /// </summary>
    public sealed class FakeCheckRunner : INayaxConnectionCheckRunner
    {
        public List<NayaxConnectionCredential> Candidates { get; } = [];

        public NayaxConnectionCheckReport Report { get; set; } = AllPassed();

        public void Reset()
        {
            Candidates.Clear();
            Report = AllPassed();
        }

        public Task<NayaxConnectionCheckReport> RunAsync(
            NayaxConnectionCredential candidate,
            CancellationToken cancellationToken)
        {
            Candidates.Add(candidate);
            return Task.FromResult(Report);
        }

        private static NayaxConnectionCheckReport AllPassed() =>
            new([.. NayaxConnectionCheckPolicy.All.Select(NayaxConnectionCheckLine.Passed)], 1);
    }

    /// <summary>
    /// Stands in for Entra: the caller's <c>(tid, oid)</c> pair and delegated scope come from
    /// request headers, so membership resolution, the capability policy and the scope requirement
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
