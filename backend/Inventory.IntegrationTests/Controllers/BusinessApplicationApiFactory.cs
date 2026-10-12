using System.Security.Claims;
using System.Text;
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

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The API host the self-service business application tests of issue #541 run against: one
/// in-memory SQLite connection with the real migration history, one existing <c>Active</c> business
/// with its own Owner and Manager, and the <c>SelfServiceOnboarding:Enabled</c> flag switched on.
///
/// <para>The flag is host configuration, read once at startup, so "on" and "off" cannot be two
/// states of one host. <see cref="Disabled"/> is the same factory with the flag left off, which is
/// what <c>BusinessApplicationDisabledApiTests</c> uses; nothing else differs between them.</para>
///
/// <para>The existing business is seeded in every reset so the tests can assert the property that
/// matters most about a new tenant arriving: an application changes nothing about the business that
/// was already trading, and an application endpoint reaches none of its data.</para>
/// </summary>
public class BusinessApplicationApiFactory : WebApplicationFactory<Program>
{
    public const string DirectoryTenantId = "99999999-9999-9999-9999-999999999999";

    /// <summary>The Owner of the existing, already-trading business.</summary>
    public const string OwnerObjectId = "11111111-1111-1111-1111-111111111111";

    /// <summary>A Manager of that same business.</summary>
    public const string ManagerObjectId = "33333333-3333-3333-3333-333333333333";

    public const int ExistingBusinessId = 1;

    public const string ExistingBusinessName = "Vending Co";

    public static readonly DateTime CreatedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly RecordingLoggerProvider _logs = new();

    public BusinessApplicationApiFactory() => _connection.Open();

    /// <summary>
    /// The same host with self-service onboarding switched off, which is its production default.
    /// </summary>
    public sealed class Disabled : BusinessApplicationApiFactory
    {
        protected override bool OnboardingEnabled => false;
    }

    /// <summary>Whether this host has the self-service onboarding flag switched on.</summary>
    protected virtual bool OnboardingEnabled => true;

    /// <summary>
    /// A person signing in for the first time: no membership anywhere, and an identity nothing else
    /// in the run has used.
    ///
    /// <para>A fresh one per test deliberately. The host is shared across this class's tests and the
    /// per-applicant submission limit is in-process state about an identity, so a shared constant
    /// would make one test's allowance depend on how many of its predecessors happened to apply -
    /// and would eventually make the suite fail on the sixth test rather than on a defect. It also
    /// means every test independently exercises the limit being per identity.</para>
    /// </summary>
    public static string NewApplicantObjectId() => Guid.NewGuid().ToString();

    /// <summary>
    /// A request body MVC cannot bind at all, as opposed to one that binds and then breaks an
    /// application rule. <c>[ApiController]</c> answers each of these with its own automatic
    /// <c>400</c> before the action runs, which is why the onboarding flag has to be decided before
    /// model binding (the review finding on issue #541's PR).
    /// </summary>
    /// <param name="kind">
    /// <c>malformed-json</c> (truncated JSON), <c>wrong-field-type</c> (an otherwise complete
    /// application whose <c>gstRegistered</c> is the string <c>"yes"</c>), <c>empty-body</c> (zero
    /// bytes declared as JSON) or <c>no-content-type</c> (zero bytes with no media type at all).
    /// </param>
    public static HttpContent UnbindableBody(string kind) => kind switch
    {
        "malformed-json" => Json("{\"name\":\"Coastal Vending\",\"contactEmail\":"),
        "wrong-field-type" => Json(
            "{\"name\":\"Coastal Vending\",\"contactEmail\":\"owner@coastal.example.com\","
                + "\"abn\":\"51824753556\",\"gstRegistered\":\"yes\",\"timeZoneId\":\"Australia/Brisbane\"}"),
        "empty-body" => Json(string.Empty),
        "no-content-type" => new ByteArrayContent([]),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown unbindable body."),
    };

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>An unrestricted context for arranging and verifying state outside the boundary.</summary>
    public AppDbContext Database()
    {
        // Resolving Services builds and starts the host, which is what applies the migrations this
        // context then reads and writes through.
        _ = Services;
        return TestAppDbContext.Unrestricted(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
    }

    public HttpClient Authenticated(string objectId, string? displayName = null, string? email = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.DirectoryTenantIdHeader, DirectoryTenantId);
        client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ObjectIdHeader, objectId);
        client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ScopeHeader, "access_as_user");

        if (displayName is not null)
        {
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.NameHeader, displayName);
        }

        if (email is not null)
        {
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.EmailHeader, email);
        }

        return client;
    }

    public HttpClient WithoutScope(string objectId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.DirectoryTenantIdHeader, DirectoryTenantId);
        client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ObjectIdHeader, objectId);
        return client;
    }

    public void ClearLog() => _logs.Clear();

    public string LoggedText() => _logs.Text();

    /// <summary>The account state the caller's own endpoint reports, as its published name.</summary>
    public Task<string?> AccountStateOfAsync(string objectId) => AccountStateFieldAsync(objectId, "state");

    /// <summary>
    /// The <c>onboardingEnabled</c> value the caller's own account state reports, as the raw JSON
    /// token, so a test can tell <c>false</c> from a missing field.
    /// </summary>
    public Task<string?> OnboardingEnabledOfAsync(string objectId) =>
        AccountStateFieldAsync(objectId, "onboardingEnabled");

    public async Task<Business> BusinessAsync(string name)
    {
        await using var db = Database();
        return await db.Businesses.AsNoTracking().SingleAsync(business => business.Name == name);
    }

    /// <summary>Every business row, in key order.</summary>
    public async Task<IReadOnlyList<Business>> BusinessesAsync()
    {
        await using var db = Database();
        return await db.Businesses.AsNoTracking().OrderBy(business => business.Id).ToListAsync();
    }

    public async Task<BusinessMembership> MembershipAsync(string objectId)
    {
        await using var db = Database();
        return await db.BusinessMemberships
            .AsNoTracking()
            .SingleAsync(membership => membership.ObjectId == objectId && membership.IsActive);
    }

    /// <summary>Every membership row recorded for one identity, active or not.</summary>
    public async Task<IReadOnlyList<BusinessMembership>> MembershipsOfAsync(string objectId)
    {
        await using var db = Database();
        return await db.BusinessMemberships
            .AsNoTracking()
            .Where(membership => membership.ObjectId == objectId)
            .OrderBy(membership => membership.Id)
            .ToListAsync();
    }

    /// <summary>Every membership row of every identity, in key order.</summary>
    public async Task<IReadOnlyList<BusinessMembership>> MembershipsAsync()
    {
        await using var db = Database();
        return await db.BusinessMemberships
            .AsNoTracking()
            .OrderBy(membership => membership.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Seeds one business outside the request boundary, in any lifecycle status, so a test can
    /// arrange a pending, rejected or deactivated world the API itself would not create.
    /// </summary>
    public async Task<int> SeedBusinessAsync(
        string name,
        BusinessStatus status,
        string? contactEmail = null,
        string? abn = null,
        bool? gstRegistered = null,
        string timeZoneId = "Australia/Sydney",
        DateTime? appliedAtUtc = null,
        DateTime? decidedAtUtc = null,
        string? rejectionReason = null)
    {
        await using var db = Database();
        var business = new Business
        {
            Name = name,
            TimeZoneId = timeZoneId,
            Status = status,
            ContactEmail = contactEmail,
            Abn = abn,
            GstRegistered = gstRegistered,
            AppliedAtUtc = appliedAtUtc,
            DecidedAtUtc = decidedAtUtc,
            RejectionReason = rejectionReason,
            CreatedAtUtc = CreatedAt,
        };

        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        return business.Id;
    }

    public async Task<int> SeedMembershipAsync(int businessId, string objectId, BusinessRole role, bool isActive)
    {
        await using var db = Database();
        var membership = Membership(businessId, objectId, role);
        membership.IsActive = isActive;
        db.BusinessMemberships.Add(membership);
        await db.SaveChangesAsync();
        return membership.Id;
    }

    public async Task SetBusinessStatusAsync(int businessId, BusinessStatus status)
    {
        await using var db = Database();
        (await db.Businesses.SingleAsync(business => business.Id == businessId)).Status = status;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Puts the world back to exactly one existing <c>Active</c> business with its Owner and
    /// Manager, and nobody else. These tests create businesses and memberships, so each one starts
    /// from the same world rather than from whatever its predecessor left behind.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var db = Database();
        db.MemberInvites.RemoveRange(await db.MemberInvites.ToListAsync());
        await db.SaveChangesAsync();
        db.BusinessMemberships.RemoveRange(await db.BusinessMemberships.ToListAsync());
        await db.SaveChangesAsync();
        db.Businesses.RemoveRange(await db.Businesses.ToListAsync());
        await db.SaveChangesAsync();

        db.Businesses.Add(new Business
        {
            Id = ExistingBusinessId,
            Name = ExistingBusinessName,
            TimeZoneId = "Australia/Sydney",
            Status = BusinessStatus.Active,
            CreatedAtUtc = CreatedAt,
        });
        await db.SaveChangesAsync();

        db.BusinessMemberships.AddRange(
            Membership(ExistingBusinessId, OwnerObjectId, BusinessRole.Owner),
            Membership(ExistingBusinessId, ManagerObjectId, BusinessRole.Manager));
        await db.SaveChangesAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Testing applies pending migrations at startup, so the schema under these tests is the
        // migration history rather than a model snapshot.
        builder.UseEnvironment("Testing");

        // The flag itself, as a deployment sets it. Left off on the Disabled host rather than set
        // to "false", so that host also proves the shipped default: absent configuration is off.
        if (OnboardingEnabled)
        {
            builder.UseSetting("SelfServiceOnboarding:Enabled", "true");
        }

        builder.ConfigureServices(services =>
        {
            var dbContextOptions = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (dbContextOptions is not null)
            {
                services.Remove(dbContextOptions);
            }

            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
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

    private async Task<string?> AccountStateFieldAsync(string objectId, string field)
    {
        var response = await Authenticated(objectId).GetAsync("/api/me/account-state");
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement
            .GetProperty(field)
            .ToString();
    }

    private static BusinessMembership Membership(int businessId, string objectId, BusinessRole role) =>
        new()
        {
            BusinessId = businessId,
            DirectoryTenantId = DirectoryTenantId,
            ObjectId = objectId,
            Role = role,
            IsActive = true,
            CreatedAtUtc = CreatedAt,
            StatusChangedAtUtc = CreatedAt,
        };

    /// <summary>
    /// Stands in for Entra: the caller's <c>(tid, oid)</c> pair, delegated scope and display claims
    /// come from request headers, so membership resolution, the scope requirement and issue #524's
    /// display values are exercised exactly as they are in production without a real token.
    /// </summary>
    private sealed class TestActorAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestActor";
        public const string DirectoryTenantIdHeader = "X-Test-Directory-Tenant-Id";
        public const string ObjectIdHeader = "X-Test-Object-Id";
        public const string ScopeHeader = "X-Test-Scope";
        public const string NameHeader = "X-Test-Name";
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

            if (Request.Headers.TryGetValue(NameHeader, out var name))
            {
                claims.Add(new Claim("name", name.ToString()));
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
    /// Captures everything the host logs, from every category and including log scopes, so the
    /// audit assertions are made over the whole request's logging rather than one logger's.
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
