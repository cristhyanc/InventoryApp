using System.Globalization;
using System.Net;
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
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The <c>GET</c>/<c>PUT /api/business/settings</c> contract (issue #548), exercised through the
/// real request pipeline: authentication, the required scope, the <c>Business.Manage</c> capability
/// policy, <c>BusinessScopeMiddleware</c>, the controller, the central <c>SaveChanges</c> enforcers
/// and relational SQLite with the real migration history.
///
/// <para>Three things have to be proved here rather than in a use-case test. Who may call it: an
/// Owner, and neither a Manager nor an Operator. Which business it changes: the one the caller's
/// membership resolved, with a second business present throughout whose row must come back
/// untouched - there is no identifier in the request for a caller to aim anywhere else. And that a
/// saved time zone takes effect on the very next request, which is what makes the zone a business
/// setting rather than a deployment constant.</para>
///
/// <para>The audit assertions read the host's own <c>ILogger</c> output, because the audit event is
/// part of the contract this issue states: it must name the actor, the business and the fields that
/// changed, and it must never carry the contact email address or the ABN.</para>
/// </summary>
public sealed class BusinessSettingsApiTests : IClassFixture<BusinessSettingsApiTests.ApiFactory>
{
    private const string Uri = "/api/business/settings";
    private const string AccessUri = "/api/me/access";
    private const string CurrentBusinessUri = "/api/business/current";

    /// <summary>The Australian Business Register's documented example ABN, spaced as it prints it.</summary>
    private const string SpacedAbn = "51 824 753 556";
    private const string NormalizedAbn = "51824753556";

    private readonly ApiFactory _factory;

    public BusinessSettingsApiTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Reports_the_settings_of_the_callers_own_business()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId).GetAsync(Uri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);
        Assert.Equal(
            ["abn", "contactEmail", "gstRegistered", "name", "timeZoneId"],
            json.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(ApiFactory.BusinessName, json.GetProperty("name").GetString());
        Assert.Equal(ApiFactory.BusinessTimeZoneId, json.GetProperty("timeZoneId").GetString());

        // The business that predates the application form records none of the profile details, and
        // the read reports exactly that rather than inventing a value for any of them.
        Assert.Equal(JsonValueKind.Null, json.GetProperty("contactEmail").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("abn").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("gstRegistered").ValueKind);
    }

    /// <summary>
    /// A save answers with what was stored, normalises the ABN a person typed with spaces, and is
    /// visible to the next read. Nothing in the request names a business.
    /// </summary>
    [Fact]
    public async Task An_owner_saves_the_settings_of_their_own_business()
    {
        await _factory.SeedAsync();
        var client = _factory.Authenticated(ApiFactory.SaveOwnerObjectId);

        var response = await client.PutAsync(Uri, Body(
            name: "Saved Vending Co",
            contactEmail: "owner@example.com",
            abn: SpacedAbn,
            gstRegistered: true,
            timeZoneId: "Australia/Sydney"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);
        Assert.Equal("Saved Vending Co", json.GetProperty("name").GetString());
        Assert.Equal("owner@example.com", json.GetProperty("contactEmail").GetString());
        Assert.Equal(NormalizedAbn, json.GetProperty("abn").GetString());
        Assert.True(json.GetProperty("gstRegistered").GetBoolean());
        Assert.Equal("Australia/Sydney", json.GetProperty("timeZoneId").GetString());

        var reread = await ReadJsonAsync(await client.GetAsync(Uri));
        Assert.Equal("Saved Vending Co", reread.GetProperty("name").GetString());
        Assert.Equal(NormalizedAbn, reread.GetProperty("abn").GetString());

        var stored = await _factory.StoredAsync(ApiFactory.SaveBusinessId);
        Assert.Equal("Saved Vending Co", stored.Name);
        Assert.Equal("owner@example.com", stored.ContactEmail);
        Assert.Equal(NormalizedAbn, stored.Abn);
        Assert.True(stored.GstRegistered);
    }

    /// <summary>
    /// The other half of the previous test: the second business in the same database is not touched
    /// by it. There is no business identifier in the request, so this is the whole surface a caller
    /// could have aimed elsewhere.
    /// </summary>
    [Fact]
    public async Task A_save_leaves_every_other_business_exactly_as_it_was()
    {
        await _factory.SeedAsync();
        var before = await _factory.StoredAsync(ApiFactory.OtherBusinessId);

        var response = await _factory.Authenticated(ApiFactory.IsolationOwnerObjectId).PutAsync(Uri, Body(
            name: "Isolation Vending Co",
            contactEmail: "isolation@example.com",
            abn: null,
            gstRegistered: false,
            timeZoneId: "America/New_York"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await _factory.StoredAsync(ApiFactory.OtherBusinessId);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.TimeZoneId, after.TimeZoneId);
        Assert.Equal(before.ContactEmail, after.ContactEmail);
        Assert.Equal(before.Abn, after.Abn);
        Assert.Equal(before.GstRegistered, after.GstRegistered);
        Assert.Equal(before.Status, after.Status);
    }

    /// <summary>
    /// The criterion this slice exists for: a saved zone takes effect on the next request, with no
    /// sign-out and no restart, and it is the zone <c>/api/me/access</c> and
    /// <c>/api/business/current</c> both report afterwards.
    /// </summary>
    [Fact]
    public async Task A_saved_time_zone_takes_effect_on_the_next_request()
    {
        await _factory.SeedAsync();
        var client = _factory.Authenticated(ApiFactory.TimeZoneOwnerObjectId);

        var before = await ReadJsonAsync(await client.GetAsync(AccessUri));
        Assert.Equal("Australia/Sydney", before.GetProperty("timeZoneId").GetString());

        var saved = await client.PutAsync(Uri, Body(
            name: "Zoned Vending Co",
            contactEmail: "zone@example.com",
            abn: null,
            gstRegistered: true,
            timeZoneId: "America/New_York"));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var access = await ReadJsonAsync(await client.GetAsync(AccessUri));
        Assert.Equal("America/New_York", access.GetProperty("timeZoneId").GetString());

        var current = await ReadJsonAsync(await client.GetAsync(CurrentBusinessUri));
        Assert.Equal("America/New_York", current.GetProperty("timeZoneId").GetString());
    }

    [Theory]
    [InlineData("Manager")]
    [InlineData("Operator")]
    public async Task A_role_without_business_manage_may_neither_read_nor_save(string role)
    {
        await _factory.SeedAsync();
        var objectId = ApiFactory.Members.Single(member => member.Role == role).ObjectId;
        var client = _factory.Authenticated(objectId);

        var read = await client.GetAsync(Uri);
        var save = await client.PutAsync(Uri, Body(
            name: "Not Allowed",
            contactEmail: "nope@example.com",
            abn: null,
            gstRegistered: true,
            timeZoneId: "America/New_York"));

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, save.StatusCode);

        // The refusal happened before anything was written, and tells the caller nothing about the
        // capability it wanted or the role it holds.
        var stored = await _factory.StoredAsync(ApiFactory.BusinessId);
        Assert.Equal(ApiFactory.BusinessName, stored.Name);
        var body = await save.Content.ReadAsStringAsync();
        foreach (var leak in new[] { "Business.Manage", "apability", "Owner", "Manager", "Operator" })
        {
            Assert.DoesNotContain(leak, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_refused_with_401()
    {
        await _factory.SeedAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(Uri)).StatusCode);
    }

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
        Assert.DoesNotContain(ApiFactory.OtherBusinessName, body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every validation rule the issue states, each one refused with <c>400</c> and each one
    /// leaving the business exactly as it was: a save is all of the settings or none of them.
    /// </summary>
    [Theory]
    [InlineData("a blank name", null, "owner@example.com", null, true, "Australia/Sydney")]
    [InlineData("a whitespace name", "   ", "owner@example.com", null, true, "Australia/Sydney")]
    [InlineData("a missing contact email", "Validated Co", null, null, true, "Australia/Sydney")]
    [InlineData("a malformed contact email", "Validated Co", "owner@", null, true, "Australia/Sydney")]
    [InlineData("an email with no domain dot", "Validated Co", "owner@example", null, true, "Australia/Sydney")]
    [InlineData("an email with a space", "Validated Co", "own er@example.com", null, true, "Australia/Sydney")]
    [InlineData("a missing GST registration", "Validated Co", "owner@example.com", null, null, "Australia/Sydney")]
    [InlineData("an ABN failing its checksum", "Validated Co", "owner@example.com", "51824753557", true, "Australia/Sydney")]
    [InlineData("an ABN of the wrong length", "Validated Co", "owner@example.com", "5182475355", true, "Australia/Sydney")]
    [InlineData("a blank ABN", "Validated Co", "owner@example.com", "   ", true, "Australia/Sydney")]
    [InlineData("a missing time zone", "Validated Co", "owner@example.com", null, true, null)]
    [InlineData("an unknown time zone", "Validated Co", "owner@example.com", null, true, "Mars/Olympus_Mons")]
    [InlineData("a Windows time zone id", "Validated Co", "owner@example.com", null, true, "AUS Eastern Standard Time")]
    public async Task An_invalid_save_is_refused_with_400_and_stores_nothing(
        string because,
        string? name,
        string? contactEmail,
        string? abn,
        bool? gstRegistered,
        string? timeZoneId)
    {
        await _factory.SeedAsync();
        var before = await _factory.StoredAsync(ApiFactory.ValidationBusinessId);

        var response = await _factory.Authenticated(ApiFactory.ValidationOwnerObjectId)
            .PutAsync(Uri, Body(name, contactEmail, abn, gstRegistered, timeZoneId));

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"{because} answered {(int)response.StatusCode}.");

        var after = await _factory.StoredAsync(ApiFactory.ValidationBusinessId);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.TimeZoneId, after.TimeZoneId);
        Assert.Equal(before.ContactEmail, after.ContactEmail);
        Assert.Equal(before.Abn, after.Abn);
        Assert.Equal(before.GstRegistered, after.GstRegistered);
    }

    /// <summary>
    /// The ABN is the one optional setting, and clearing it is a real intention: null is the single
    /// representation of "not recorded", so a saved null is stored as null rather than as a blank
    /// string.
    /// </summary>
    [Fact]
    public async Task An_omitted_ABN_clears_the_recorded_one()
    {
        await _factory.SeedAsync();
        var client = _factory.Authenticated(ApiFactory.AbnOwnerObjectId);

        await client.PutAsync(Uri, Body("Abn Co", "abn@example.com", SpacedAbn, true, "Australia/Sydney"));
        Assert.Equal(NormalizedAbn, (await _factory.StoredAsync(ApiFactory.AbnBusinessId)).Abn);

        var cleared = await client.PutAsync(Uri, Body("Abn Co", "abn@example.com", null, true, "Australia/Sydney"));

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Null((await _factory.StoredAsync(ApiFactory.AbnBusinessId)).Abn);
    }

    /// <summary>
    /// The audit event: the actor, the business and the fields that changed - and neither the
    /// contact email address nor the ABN, which are the two values the issue says must never appear
    /// in it.
    /// </summary>
    [Fact]
    public async Task A_change_is_audited_with_the_changed_fields_and_no_values()
    {
        await _factory.SeedAsync();
        var client = _factory.Authenticated(ApiFactory.AuditOwnerObjectId);

        _factory.ClearLog();
        var response = await client.PutAsync(Uri, Body(
            name: "Audited Vending Co",
            contactEmail: "audited@example.com",
            abn: SpacedAbn,
            gstRegistered: true,
            timeZoneId: "America/New_York"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var audit = Assert.Single(_factory.LogMessages.Where(message =>
            message.Contains("Business settings changed", StringComparison.Ordinal)));

        Assert.Contains(ApiFactory.AuditOwnerObjectId, audit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ApiFactory.DirectoryTenantId, audit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            ApiFactory.AuditBusinessId.ToString(CultureInfo.InvariantCulture),
            audit,
            StringComparison.Ordinal);
        foreach (var field in new[] { "Name", "ContactEmail", "Abn", "GstRegistered", "TimeZoneId" })
        {
            Assert.Contains(field, audit, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("audited@example.com", audit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(NormalizedAbn, audit, StringComparison.Ordinal);
        Assert.DoesNotContain(SpacedAbn, audit, StringComparison.Ordinal);
    }

    /// <summary>
    /// A save that changes one setting audits that one setting. The event is evidence about a
    /// change, so it must not list the fields that were merely resubmitted unchanged.
    /// </summary>
    [Fact]
    public async Task An_audited_change_names_only_the_fields_that_changed()
    {
        await _factory.SeedAsync();
        var client = _factory.Authenticated(ApiFactory.PartialAuditOwnerObjectId);

        await client.PutAsync(Uri, Body("Partial Co", "partial@example.com", null, true, "Australia/Sydney"));

        _factory.ClearLog();
        var response = await client.PutAsync(Uri, Body("Renamed Co", "partial@example.com", null, true, "Australia/Sydney"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var audit = Assert.Single(_factory.LogMessages.Where(message =>
            message.Contains("Business settings changed", StringComparison.Ordinal)));
        Assert.Contains("Name", audit, StringComparison.Ordinal);
        foreach (var unchanged in new[] { "ContactEmail", "GstRegistered", "TimeZoneId" })
        {
            Assert.DoesNotContain(unchanged, audit, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Resubmitting the settings unchanged is not a change, so there is nothing to audit. An audit
    /// trail that recorded every save would make the record of the real changes harder to read, not
    /// easier.
    /// </summary>
    [Fact]
    public async Task A_save_that_changes_nothing_is_not_audited()
    {
        await _factory.SeedAsync();
        var client = _factory.Authenticated(ApiFactory.NoChangeOwnerObjectId);

        await client.PutAsync(Uri, Body("No Change Co", "nochange@example.com", null, false, "Australia/Sydney"));

        _factory.ClearLog();
        var response = await client.PutAsync(Uri, Body("No Change Co", "nochange@example.com", null, false, "Australia/Sydney"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(
            _factory.LogMessages,
            message => message.Contains("Business settings changed", StringComparison.Ordinal));
    }

    private static StringContent Body(
        string? name,
        string? contactEmail,
        string? abn,
        bool? gstRegistered,
        string? timeZoneId)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["contactEmail"] = contactEmail,
            ["abn"] = abn,
            ["gstRegistered"] = gstRegistered,
            ["timeZoneId"] = timeZoneId,
        });

        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string BusinessName = "Vending Co";
        public const string BusinessTimeZoneId = "Australia/Sydney";
        public const string OtherBusinessName = "Second Vending Co";

        public const int BusinessId = 1;
        public const int OtherBusinessId = 2;
        public const int SaveBusinessId = 3;
        public const int ValidationBusinessId = 4;
        public const int AuditBusinessId = 5;
        public const int AbnBusinessId = 6;
        public const int TimeZoneBusinessId = 7;
        public const int NoChangeBusinessId = 8;
        public const int PartialAuditBusinessId = 9;
        public const int IsolationBusinessId = 10;

        public const string DirectoryTenantId = "88888888-8888-8888-8888-888888888888";
        public const string OwnerObjectId = "11111111-1111-1111-1111-111111111111";
        public const string ManagerObjectId = "22222222-2222-2222-2222-222222222222";
        public const string OperatorObjectId = "33333333-3333-3333-3333-333333333333";
        public const string StrangerObjectId = "44444444-4444-4444-4444-444444444444";
        public const string SaveOwnerObjectId = "55555555-5555-5555-5555-555555555555";
        public const string ValidationOwnerObjectId = "66666666-6666-6666-6666-666666666666";
        public const string AuditOwnerObjectId = "77777777-7777-7777-7777-777777777777";
        public const string AbnOwnerObjectId = "99999999-9999-9999-9999-999999999999";
        public const string TimeZoneOwnerObjectId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        public const string NoChangeOwnerObjectId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        public const string PartialAuditOwnerObjectId = "cccccccc-cccc-cccc-cccc-cccccccccccc";
        public const string IsolationOwnerObjectId = "dddddddd-dddd-dddd-dddd-dddddddddddd";

        private static readonly DateTime CreatedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly List<string> _logMessages = [];
        private bool _seeded;

        public ApiFactory() => _connection.Open();

        /// <summary>The three roles of one business, for the capability assertions.</summary>
        public static (string Role, string ObjectId)[] Members =>
        [
            ("Owner", OwnerObjectId),
            ("Manager", ManagerObjectId),
            ("Operator", OperatorObjectId),
        ];

        /// <summary>Every <c>ILogger</c> message the host has written since the last clear.</summary>
        public IReadOnlyList<string> LogMessages
        {
            get
            {
                lock (_logMessages)
                {
                    return _logMessages.ToArray();
                }
            }
        }

        public void ClearLog()
        {
            lock (_logMessages)
            {
                _logMessages.Clear();
            }
        }

        /// <summary>An unrestricted context for arranging and inspecting state outside a request.</summary>
        public AppDbContext Database()
        {
            // Resolving Services builds and starts the host, which is what applies the migrations
            // this context then reads and writes through.
            _ = Services;
            return TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        }

        /// <summary>The stored business row, read outside the request boundary.</summary>
        public async Task<Business> StoredAsync(int businessId)
        {
            await using var db = Database();
            return await db.Businesses.AsNoTracking().SingleAsync(business => business.Id == businessId);
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

        public async Task SeedAsync()
        {
            await using var db = Database();
            if (_seeded)
            {
                return;
            }

            // One business per test that writes, so a save made by one test is never the state
            // another one reads: every assertion here is about a business's own row.
            db.Businesses.AddRange(
                Business(BusinessId, BusinessName, BusinessTimeZoneId),
                Business(OtherBusinessId, OtherBusinessName, "America/New_York"),
                Business(SaveBusinessId, "Save Vending Co", BusinessTimeZoneId),
                Business(ValidationBusinessId, "Validation Vending Co", BusinessTimeZoneId),
                Business(AuditBusinessId, "Audit Vending Co", BusinessTimeZoneId),
                Business(AbnBusinessId, "Abn Vending Co", BusinessTimeZoneId),
                Business(TimeZoneBusinessId, "Zone Vending Co", BusinessTimeZoneId),
                Business(NoChangeBusinessId, "No Change Vending Co", BusinessTimeZoneId),
                Business(PartialAuditBusinessId, "Partial Vending Co", BusinessTimeZoneId),
                Business(IsolationBusinessId, "Isolation Vending Co", BusinessTimeZoneId));

            db.BusinessMemberships.AddRange(
                Membership(BusinessId, OwnerObjectId, BusinessRole.Owner),
                Membership(BusinessId, ManagerObjectId, BusinessRole.Manager),
                Membership(BusinessId, OperatorObjectId, BusinessRole.Operator),
                Membership(SaveBusinessId, SaveOwnerObjectId, BusinessRole.Owner),
                Membership(ValidationBusinessId, ValidationOwnerObjectId, BusinessRole.Owner),
                Membership(AuditBusinessId, AuditOwnerObjectId, BusinessRole.Owner),
                Membership(AbnBusinessId, AbnOwnerObjectId, BusinessRole.Owner),
                Membership(TimeZoneBusinessId, TimeZoneOwnerObjectId, BusinessRole.Owner),
                Membership(NoChangeBusinessId, NoChangeOwnerObjectId, BusinessRole.Owner),
                Membership(PartialAuditBusinessId, PartialAuditOwnerObjectId, BusinessRole.Owner),
                Membership(IsolationBusinessId, IsolationOwnerObjectId, BusinessRole.Owner));

            await db.SaveChangesAsync();
            _seeded = true;
        }

        private static Business Business(int id, string name, string timeZoneId) =>
            new()
            {
                Id = id,
                Name = name,
                TimeZoneId = timeZoneId,
                Status = BusinessStatus.Active,
                CreatedAtUtc = CreatedAt,
            };

        private static BusinessMembership Membership(int businessId, string objectId, BusinessRole role) =>
            new()
            {
                BusinessId = businessId,
                DirectoryTenantId = DirectoryTenantId,
                ObjectId = objectId,
                Role = role,
                CreatedAtUtc = CreatedAt,
                StatusChangedAtUtc = CreatedAt,
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
                services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(Capture));
            });

            builder.ConfigureTestServices(services =>
                services.AddAuthentication(TestActorAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestActorAuthenticationHandler>(
                        TestActorAuthenticationHandler.SchemeName, _ => { }));
        }

        private void Capture(string message)
        {
            lock (_logMessages)
            {
                _logMessages.Add(message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _connection.Dispose();
            }
        }

        private sealed class CapturingLoggerProvider(Action<string> capture) : ILoggerProvider
        {
            public ILogger CreateLogger(string categoryName) => new CapturingLogger(capture);

            public void Dispose()
            {
            }

            private sealed class CapturingLogger(Action<string> capture) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(
                    LogLevel logLevel,
                    EventId eventId,
                    TState state,
                    Exception? exception,
                    Func<TState, Exception?, string> formatter) =>
                    capture(formatter(state, exception));
            }
        }
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
