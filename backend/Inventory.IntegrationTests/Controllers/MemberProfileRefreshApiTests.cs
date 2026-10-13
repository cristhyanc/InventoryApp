using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The member display-name and email refresh through the real request pipeline (issue #524):
/// authentication with real claims, the delegated scope, the business-scope middleware, a real
/// controller, the composition root's own registrations, and relational SQLite with the real
/// migration history.
///
/// <para>The drain is deliberately not running in this host. What a request is responsible for is
/// <em>scheduling</em> the refresh - which this test can observe exactly, with no waiting and no
/// polling - and the write itself is proved against a real database in
/// <c>MemberProfileRefreshWorkerTests</c> and <c>EfMemberProfileWriteStoreTests</c>. What a request
/// queued is then applied here the way the drain applies it, through the same use case in its own
/// new service scope, so the end result is still asserted on a real row.</para>
///
/// <para>Each test uses its own member identity, because the hourly throttle is a process-wide
/// singleton in the host exactly as it is in production: sharing one identity between tests would
/// make the first test's refresh decide the second's.</para>
/// </summary>
public sealed class MemberProfileRefreshApiTests : IClassFixture<MemberProfileRefreshApiTests.ApiFactory>
{
    private const string AnyBusinessEndpoint = "/api/me/access";

    private readonly ApiFactory _factory;

    public MemberProfileRefreshApiTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// The refresh itself, end to end: a member's ordinary request stores the name and email their
    /// validated token reported, on their own membership row, and leaves the rest of the row alone.
    /// </summary>
    [Fact]
    public async Task A_members_request_refreshes_their_own_stored_name_and_email()
    {
        await _factory.PrepareAsync();

        var response = await _factory.Authenticated(ApiFactory.RefreshedMember).GetAsync(AnyBusinessEndpoint);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var queued = Assert.Single(await _factory.DrainQueueAsync());
        Assert.Equal(ApiFactory.RefreshedMember, queued.Identity.ObjectId);

        var membership = await _factory.ReadMembershipAsync(ApiFactory.RefreshedMember);
        Assert.Equal(ApiFactory.MemberDisplayName, membership.DisplayName);
        Assert.Equal(ApiFactory.MemberEmail, membership.Email);
        Assert.Equal(BusinessRole.Owner, membership.Role);
        Assert.True(membership.IsActive);
    }

    /// <summary>
    /// The hourly rule through the pipeline: a member makes many requests, and only the first of
    /// them schedules a refresh.
    /// </summary>
    [Fact]
    public async Task Only_the_first_request_in_an_hour_schedules_a_refresh()
    {
        await _factory.PrepareAsync();
        var client = _factory.Authenticated(ApiFactory.BusyMember);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(AnyBusinessEndpoint)).StatusCode);
        }

        Assert.Single(await _factory.DrainQueueAsync());
    }

    /// <summary>
    /// A request carrying the support-session header refreshes nothing, so a support session cannot
    /// overwrite the member's own stored details with the supporter's claims - and the member's row
    /// is left exactly as it was.
    /// </summary>
    [Fact]
    public async Task A_support_session_request_refreshes_nothing()
    {
        await _factory.PrepareAsync();

        var client = _factory.Authenticated(ApiFactory.SupportedMember);
        client.DefaultRequestHeaders.Add(SupportSessionHeader.Name, "support-session-stand-in");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(AnyBusinessEndpoint)).StatusCode);

        Assert.Empty(await _factory.DrainQueueAsync());

        var membership = await _factory.ReadMembershipAsync(ApiFactory.SupportedMember);
        Assert.Null(membership.DisplayName);
        Assert.Null(membership.Email);
    }

    /// <summary>
    /// A token with neither display claim stores nothing rather than blanking the row, and - because
    /// it never claimed the hourly slot - a later request that does carry the claims is stored
    /// immediately.
    /// </summary>
    [Fact]
    public async Task A_token_without_the_display_claims_refreshes_nothing()
    {
        await _factory.PrepareAsync();

        var withoutClaims = _factory.Authenticated(ApiFactory.ClaimlessMember, withDisplayClaims: false);
        Assert.Equal(HttpStatusCode.OK, (await withoutClaims.GetAsync(AnyBusinessEndpoint)).StatusCode);

        Assert.Empty(await _factory.DrainQueueAsync());
        Assert.Null((await _factory.ReadMembershipAsync(ApiFactory.ClaimlessMember)).DisplayName);

        var withClaims = _factory.Authenticated(ApiFactory.ClaimlessMember);
        Assert.Equal(HttpStatusCode.OK, (await withClaims.GetAsync(AnyBusinessEndpoint)).StatusCode);

        Assert.Single(await _factory.DrainQueueAsync());
        Assert.Equal(
            ApiFactory.MemberDisplayName,
            (await _factory.ReadMembershipAsync(ApiFactory.ClaimlessMember)).DisplayName);
    }

    /// <summary>
    /// A signed-in caller the membership requirement refuses schedules nothing: they are answered
    /// 403 before the endpoint runs, and there is no membership of theirs to refresh. The revoked
    /// member's row keeps the details it was revoked with.
    /// </summary>
    [Theory]
    [InlineData(ApiFactory.Stranger)]
    [InlineData(ApiFactory.RevokedMember)]
    public async Task A_caller_with_no_usable_membership_refreshes_nothing(string objectId)
    {
        await _factory.PrepareAsync();

        var response = await _factory.Authenticated(objectId).GetAsync(AnyBusinessEndpoint);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await _factory.DrainQueueAsync());
    }

    /// <summary>
    /// The one endpoint a person with no usable membership can reach refreshes nothing either: it
    /// resolves no membership at all, so there is nothing of theirs to write.
    /// </summary>
    [Fact]
    public async Task The_account_state_endpoint_refreshes_nothing()
    {
        await _factory.PrepareAsync();

        var response = await _factory.Authenticated(ApiFactory.AccountStateMember).GetAsync("/api/me/account-state");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await _factory.DrainQueueAsync());
    }

    /// <summary>
    /// The logging rule over a whole request and its refresh: nothing the host logged while handling
    /// a member's request, or while applying the refresh it scheduled, contains their display name
    /// or their email address.
    /// </summary>
    [Fact]
    public async Task The_request_logs_neither_the_display_name_nor_the_email()
    {
        await _factory.PrepareAsync();

        Assert.Equal(
            HttpStatusCode.OK,
            (await _factory.Authenticated(ApiFactory.LoggedMember).GetAsync(AnyBusinessEndpoint)).StatusCode);

        Assert.Single(await _factory.DrainQueueAsync());

        var logged = _factory.LoggedText();
        Assert.NotEmpty(logged);
        Assert.DoesNotContain(ApiFactory.MemberDisplayName, logged, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ApiFactory.MemberEmail, logged, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ada", logged, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string DirectoryTenantId = "99999999-9999-9999-9999-999999999999";

        public const string RefreshedMember = "11111111-1111-1111-1111-111111111111";
        public const string BusyMember = "22222222-2222-2222-2222-222222222222";
        public const string SupportedMember = "33333333-3333-3333-3333-333333333333";
        public const string ClaimlessMember = "44444444-4444-4444-4444-444444444444";
        public const string RevokedMember = "55555555-5555-5555-5555-555555555555";
        public const string Stranger = "66666666-6666-6666-6666-666666666666";
        public const string AccountStateMember = "77777777-7777-7777-7777-777777777777";
        public const string LoggedMember = "88888888-8888-8888-8888-888888888888";

        public const string MemberDisplayName = "Ada Lovelace";
        public const string MemberEmail = "ada@vending.example";

        private const int BusinessId = 1;
        private static readonly DateTime CreatedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly RecordingLoggerProvider _logs = new();
        private bool _seeded;

        public ApiFactory() => _connection.Open();

        /// <summary>An unrestricted context for arranging and reading state outside the boundary.</summary>
        public AppDbContext Database()
        {
            // Resolving Services builds and starts the host, which is what applies the migrations
            // this context then reads and writes through.
            _ = Services;
            return TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        }

        public HttpClient Authenticated(string objectId, bool withDisplayClaims = true)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.DirectoryTenantIdHeader, DirectoryTenantId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ObjectIdHeader, objectId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ScopeHeader, "access_as_user");
            if (withDisplayClaims)
            {
                client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.DisplayNameHeader, MemberDisplayName);
                client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.EmailHeader, MemberEmail);
            }

            return client;
        }

        /// <summary>
        /// Seeds the deployment once, and leaves each test a clean starting point: nothing queued
        /// from an earlier test and no captured log entries.
        /// </summary>
        public async Task PrepareAsync()
        {
            await SeedAsync();
            await DrainQueueAsync();
            _logs.Clear();
        }

        /// <summary>
        /// Everything the requests queued, applied the way the drain applies it: one new service
        /// scope per refresh, resolving the same Application use case the worker resolves.
        ///
        /// The queue is read rather than completed, because it is the host's own singleton and a
        /// completed queue would accept nothing for the rest of the fixture's life.
        /// </summary>
        public async Task<List<MemberProfileRefreshRequest>> DrainQueueAsync()
        {
            var queue = Services.GetRequiredService<MemberProfileRefreshQueue>();
            var scopeFactory = Services.GetRequiredService<IServiceScopeFactory>();

            var pending = queue.Pending;
            var drained = new List<MemberProfileRefreshRequest>();

            await using var queued = queue.ReadAllAsync().GetAsyncEnumerator();
            for (var i = 0; i < pending && await queued.MoveNextAsync(); i++)
            {
                var request = queued.Current;
                drained.Add(request);

                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider
                    .GetRequiredService<RefreshMemberProfile>()
                    .Handle(request.Identity, request.Profile, CancellationToken.None);
            }

            return drained;
        }

        public async Task<BusinessMembership> ReadMembershipAsync(string objectId)
        {
            await using var db = Database();
            return await db.BusinessMemberships.AsNoTracking().SingleAsync(m => m.ObjectId == objectId);
        }

        /// <summary>Everything the host logged since the last reset, as one string to search.</summary>
        public string LoggedText() => _logs.Text();

        private async Task SeedAsync()
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

            db.BusinessMemberships.AddRange(
                Membership(RefreshedMember),
                Membership(BusyMember),
                Membership(SupportedMember),
                Membership(ClaimlessMember),
                Membership(AccountStateMember),
                Membership(LoggedMember),
                // Revoked, and holding the details it was revoked with, so a test can prove a
                // refused caller's row is not rewritten.
                Membership(RevokedMember, isActive: false, displayName: "Grace Hopper"));

            await db.SaveChangesAsync();
            _seeded = true;
        }

        private static BusinessMembership Membership(
            string objectId,
            bool isActive = true,
            string? displayName = null) =>
            new()
            {
                BusinessId = BusinessId,
                DirectoryTenantId = DirectoryTenantId,
                ObjectId = objectId,
                Role = BusinessRole.Owner,
                IsActive = isActive,
                CreatedAtUtc = CreatedAt,
                StatusChangedAtUtc = CreatedAt,
                DisplayName = displayName,
            };

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

                // The drain is removed rather than disabled: this test observes what a request
                // scheduled, and a background reader racing it would make that non-deterministic.
                var worker = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(IHostedService)
                    && d.ImplementationType == typeof(MemberProfileRefreshWorker));
                if (worker is not null)
                {
                    services.Remove(worker);
                }
            });

            builder.ConfigureLogging(logging => logging.AddProvider(_logs));

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
    /// Stands in for Entra, exactly as the account-state tests' handler does, plus the two display
    /// claims this slice reads: the <c>(tid, oid)</c> pair, the delegated scope, <c>name</c> and
    /// <c>preferred_username</c> all come from request headers, so the real claims path runs without
    /// a real token.
    /// </summary>
    private sealed class TestActorAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestActor";
        public const string DirectoryTenantIdHeader = "X-Test-Directory-Tenant-Id";
        public const string ObjectIdHeader = "X-Test-Object-Id";
        public const string ScopeHeader = "X-Test-Scope";
        public const string DisplayNameHeader = "X-Test-Display-Name";
        public const string EmailHeader = "X-Test-Email";

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

            if (Request.Headers.TryGetValue(DisplayNameHeader, out var displayName))
            {
                claims.Add(new Claim("name", displayName.ToString()));
            }

            if (Request.Headers.TryGetValue(EmailHeader, out var email))
            {
                claims.Add(new Claim("preferred_username", email.ToString()));
            }

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
        }
    }

    /// <summary>
    /// Captures everything the host logs, from every category and including log scopes, so a test
    /// can assert over a whole request's logging rather than one logger's.
    /// </summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _entries = [];

        public ILogger CreateLogger(string categoryName) => new Recording(this);

        public void Clear()
        {
            lock (_entries)
            {
                _entries.Clear();
            }
        }

        public string Text()
        {
            lock (_entries)
            {
                return string.Join('\n', _entries);
            }
        }

        public void Dispose()
        {
        }

        private void Add(string entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }

        private sealed class Recording(RecordingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                provider.Add(state.ToString() ?? string.Empty);
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                provider.Add(formatter(state, exception));
                if (exception is not null)
                {
                    provider.Add(exception.ToString());
                }
            }
        }
    }
}
