using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Inventory.Domain.FinancialConfiguration;
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
/// <c>POST api/site-commissions/agreements/{agreementId}/end</c> through the real routing,
/// authentication, scope, capability and persistence pipeline (issue #1254).
///
/// <para>This is the criterion the issue is about: a site's rate can change in the application
/// instead of in the database. The add refuses any overlap, so an open-ended 10% agreement from 1
/// January 2026 blocked a 12% agreement from 1 July 2026 for ever; ending the first on 30 June lets
/// the second in, and an agreement that really does overlap is still refused.</para>
///
/// <para>Each test owns its own site and agreement, because ending one is a write and the host is
/// shared.</para>
/// </summary>
public sealed class SiteCommissionAgreementEndApiTests
    : IClassFixture<SiteCommissionAgreementEndApiTests.ApiFactory>
{
    private readonly ApiFactory _factory;

    public SiteCommissionAgreementEndApiTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// The whole point: end the 10% agreement on 30 June 2026, and 12% from 1 July 2026 is accepted
    /// - which it was not before, because the open-ended agreement overlapped it for ever.
    /// </summary>
    [Fact]
    public async Task Ending_the_open_ended_agreement_lets_the_next_rate_start_the_day_after()
    {
        var agreementId = await _factory.AgreementIdAsync(ApiFactory.EndThenAddSiteId);

        var overlapping = await _factory.AsOwner().PostAsync(
            "/api/site-commissions/agreements", Agreement(ApiFactory.EndThenAddSiteId, "2026-07-01", 0.12m));
        Assert.Equal(HttpStatusCode.Conflict, overlapping.StatusCode);

        var ended = await _factory.AsOwner().PostAsync(EndRoute(agreementId), EndDate("2026-06-30"));
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
        using var body = JsonDocument.Parse(await ended.Content.ReadAsStringAsync());
        Assert.Equal(
            new DateTime(2026, 6, 30),
            body.RootElement.GetProperty("effectiveTo").GetDateTime());

        var added = await _factory.AsOwner().PostAsync(
            "/api/site-commissions/agreements", Agreement(ApiFactory.EndThenAddSiteId, "2026-07-01", 0.12m));
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
    }

    /// <summary>
    /// And ending one does not make the overlap check lenient: an agreement starting inside the
    /// period the ended agreement still covers is refused exactly as before.
    /// </summary>
    [Fact]
    public async Task An_agreement_that_still_overlaps_the_ended_one_is_refused()
    {
        var agreementId = await _factory.AgreementIdAsync(ApiFactory.StillOverlappingSiteId);

        Assert.Equal(
            HttpStatusCode.OK,
            (await _factory.AsOwner().PostAsync(EndRoute(agreementId), EndDate("2026-06-30"))).StatusCode);

        var overlapping = await _factory.AsOwner().PostAsync(
            "/api/site-commissions/agreements", Agreement(ApiFactory.StillOverlappingSiteId, "2026-06-15", 0.12m));

        Assert.Equal(HttpStatusCode.Conflict, overlapping.StatusCode);
    }

    [Fact]
    public async Task An_end_date_before_the_start_date_is_refused_with_the_rules_own_sentence()
    {
        var agreementId = await _factory.AgreementIdAsync(ApiFactory.BeforeStartSiteId);

        var response = await _factory.AsOwner().PostAsync(EndRoute(agreementId), EndDate("2025-12-31"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            SiteCommissionAgreementEndPolicy.BeforeStartMessage,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.Null(await _factory.EndDateOfAsync(agreementId));
    }

    [Fact]
    public async Task An_agreement_that_already_ends_cannot_be_extended()
    {
        var agreementId = await _factory.AgreementIdAsync(ApiFactory.AlreadyEndedSiteId);

        var response = await _factory.AsOwner().PostAsync(EndRoute(agreementId), EndDate("2026-12-31"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            SiteCommissionAgreementEndPolicy.LaterThanExistingEndMessage,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.Equal(new DateTime(2026, 6, 30), await _factory.EndDateOfAsync(agreementId));
    }

    /// <summary>
    /// Both roles that may add an agreement may end one, because ending only narrows: the repository
    /// owner's decision of 14 November 2026.
    /// </summary>
    [Fact]
    public async Task A_manager_can_end_an_agreement()
    {
        var agreementId = await _factory.AgreementIdAsync(ApiFactory.ManagerSiteId);

        var response = await _factory.AsManager().PostAsync(EndRoute(agreementId), EndDate("2026-06-30"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new DateTime(2026, 6, 30), await _factory.EndDateOfAsync(agreementId));
    }

    /// <summary>An Operator holds no commission capability at all, and writes nothing.</summary>
    [Fact]
    public async Task An_operator_is_refused_and_the_agreement_is_unchanged()
    {
        var agreementId = await _factory.AgreementIdAsync(ApiFactory.OperatorSiteId);

        var response = await _factory.AsOperator().PostAsync(EndRoute(agreementId), EndDate("2026-06-30"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await _factory.EndDateOfAsync(agreementId));
    }

    /// <summary>
    /// Another business's agreement is not found rather than refused, and is left untouched: the
    /// route value selects an agreement of the caller's own business and never a tenant.
    /// </summary>
    [Fact]
    public async Task Another_businesss_agreement_is_not_found_and_is_left_untouched()
    {
        var agreementId = await _factory.AgreementIdAsync(ApiFactory.OtherBusinessSiteId);

        var response = await _factory.AsOwner().PostAsync(EndRoute(agreementId), EndDate("2026-06-30"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(await _factory.EndDateOfAsync(agreementId));
    }

    [Fact]
    public async Task An_agreement_that_exists_nowhere_is_not_found()
    {
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await _factory.AsOwner().PostAsync(EndRoute(999999), EndDate("2026-06-30"))).StatusCode);
    }

    /// <summary>
    /// The committed change is written to the audit log with who made it, when, and the old and new
    /// end date - which is what makes a change to what a site is owed from a past date on traceable.
    /// </summary>
    [Fact]
    public async Task The_committed_change_is_written_to_the_audit_log()
    {
        var agreementId = await _factory.AgreementIdAsync(ApiFactory.AuditSiteId);

        Assert.Equal(
            HttpStatusCode.OK,
            (await _factory.AsOwner().PostAsync(EndRoute(agreementId), EndDate("2026-06-30"))).StatusCode);

        var audited = Assert.Single(_factory.LogMessages.Where(message =>
            message.StartsWith("Site commission agreement ended.", StringComparison.Ordinal) &&
            message.Contains($"agreement {agreementId};", StringComparison.Ordinal)));

        Assert.Contains($"Actor {ApiFactory.DirectoryTenantId}/{ApiFactory.OwnerObjectId}", audited, StringComparison.Ordinal);
        Assert.Contains($"business {ApiFactory.BusinessId};", audited, StringComparison.Ordinal);
        Assert.Contains($"site {ApiFactory.AuditSiteId};", audited, StringComparison.Ordinal);
        Assert.Contains("end date none -> 2026-06-30;", audited, StringComparison.Ordinal);
        Assert.Contains("occurred ", audited, StringComparison.Ordinal);
        // The trail records the decision, never the business's financial configuration.
        Assert.DoesNotContain("rate", audited, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A refused end date is not audited, because it did not happen.</summary>
    [Fact]
    public async Task A_refused_end_date_is_not_audited()
    {
        var agreementId = await _factory.AgreementIdAsync(ApiFactory.RefusalAuditSiteId);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await _factory.AsOwner().PostAsync(EndRoute(agreementId), EndDate("2025-01-01"))).StatusCode);

        Assert.DoesNotContain(
            _factory.LogMessages,
            message => message.StartsWith("Site commission agreement ended.", StringComparison.Ordinal) &&
                message.Contains($"agreement {agreementId};", StringComparison.Ordinal));
    }

    private static string EndRoute(int agreementId) =>
        $"/api/site-commissions/agreements/{agreementId}/end";

    private static StringContent EndDate(string date) =>
        new($$"""{"endDate":"{{date}}T00:00:00"}""", Encoding.UTF8, "application/json");

    private static StringContent Agreement(long siteId, string effectiveFrom, decimal rate) =>
        new(
            $$"""
            {"siteId":{{siteId}},"effectiveFrom":"{{effectiveFrom}}T00:00:00","effectiveTo":null,
             "commissionRate":{{rate}},"frequency":1,"basis":0,"paymentDueDaysAfterPeriodEnd":14}
            """,
            Encoding.UTF8,
            "application/json");

    /// <summary>
    /// Hosts the real API over one in-memory SQLite database holding two businesses, so a request is
    /// answered - or refused - by the real routing, authentication, scope, capability and tenant
    /// pipeline. The first business holds an Owner, a Manager and an Operator.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string DirectoryTenantId = "66666666-6666-6666-6666-666666666666";
        public const string OwnerObjectId = "11111111-1111-1111-1111-111111111111";
        public const string ManagerObjectId = "22222222-2222-2222-2222-222222222222";
        public const string OperatorObjectId = "33333333-3333-3333-3333-333333333333";
        public const int BusinessId = 1;
        public const int OtherBusinessId = 2;

        public const long EndThenAddSiteId = 10;
        public const long StillOverlappingSiteId = 11;
        public const long BeforeStartSiteId = 12;
        public const long AlreadyEndedSiteId = 13;
        public const long ManagerSiteId = 14;
        public const long OperatorSiteId = 15;
        public const long AuditSiteId = 16;
        public const long RefusalAuditSiteId = 17;
        public const long OtherBusinessSiteId = 18;

        private static readonly DateTime CreatedAt = new(2026, 11, 16, 4, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Start = new(2026, 1, 1);

        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly List<string> _logMessages = [];
        private bool _seeded;

        public ApiFactory() => _connection.Open();

        public IReadOnlyList<string> LogMessages
        {
            get
            {
                lock (_logMessages)
                {
                    return [.. _logMessages];
                }
            }
        }

        public HttpClient AsOwner() => Authenticated(OwnerObjectId);

        public HttpClient AsManager() => Authenticated(ManagerObjectId);

        public HttpClient AsOperator() => Authenticated(OperatorObjectId);

        /// <summary>The key the database assigned the agreement seeded for one site.</summary>
        public async Task<int> AgreementIdAsync(long siteId)
        {
            await SeedAsync();
            await using var db = TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
            return (await db.SiteCommissionAgreements.AsNoTracking()
                .SingleAsync(agreement => agreement.SiteId == siteId)).Id;
        }

        /// <summary>The end date stored on one agreement, read past every tenant filter.</summary>
        public async Task<DateTime?> EndDateOfAsync(int agreementId)
        {
            await using var db = TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
            return (await db.SiteCommissionAgreements.AsNoTracking()
                .SingleAsync(agreement => agreement.Id == agreementId)).EffectiveTo;
        }

        public async Task SeedAsync()
        {
            // Resolving Services builds and starts the host, which applies the migrations this
            // context then writes through.
            _ = Services;
            if (_seeded) return;

            await using var db = TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

            db.Businesses.AddRange(
                Business(BusinessId, "Vending Co"),
                Business(OtherBusinessId, "Other Vending Co"));

            db.BusinessMemberships.AddRange(
                Membership(BusinessId, OwnerObjectId, BusinessRole.Owner),
                Membership(BusinessId, ManagerObjectId, BusinessRole.Manager),
                Membership(BusinessId, OperatorObjectId, BusinessRole.Operator));

            db.SiteCommissionAgreements.AddRange(
                OpenEnded(BusinessId, EndThenAddSiteId),
                OpenEnded(BusinessId, StillOverlappingSiteId),
                OpenEnded(BusinessId, BeforeStartSiteId),
                OpenEnded(BusinessId, AlreadyEndedSiteId),
                OpenEnded(BusinessId, ManagerSiteId),
                OpenEnded(BusinessId, OperatorSiteId),
                OpenEnded(BusinessId, AuditSiteId),
                OpenEnded(BusinessId, RefusalAuditSiteId),
                OpenEnded(OtherBusinessId, OtherBusinessSiteId));

            await db.SaveChangesAsync();

            var alreadyEnded = await db.SiteCommissionAgreements
                .SingleAsync(agreement => agreement.SiteId == AlreadyEndedSiteId);
            alreadyEnded.EffectiveTo = new DateTime(2026, 6, 30);
            await db.SaveChangesAsync();

            _seeded = true;
        }

        private static Business Business(int id, string name) =>
            new()
            {
                Id = id,
                Name = name,
                TimeZoneId = "Australia/Sydney",
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
                IsActive = true,
                CreatedAtUtc = CreatedAt,
                StatusChangedAtUtc = CreatedAt,
            };

        private static SiteCommissionAgreement OpenEnded(int businessId, long siteId) =>
            new()
            {
                BusinessId = businessId,
                SiteId = siteId,
                EffectiveFrom = Start,
                EffectiveTo = null,
                CommissionRate = 0.10m,
                Frequency = CommissionFrequency.Monthly,
                Basis = CommissionBasis.GrossSales,
                PaymentDueDaysAfterPeriodEnd = 14,
                CreatedAt = CreatedAt,
                UpdatedAt = CreatedAt,
            };

        private HttpClient Authenticated(string objectId)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.DirectoryTenantIdHeader, DirectoryTenantId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ObjectIdHeader, objectId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ScopeHeader, "access_as_user");
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Testing applies pending migrations at startup, so the schema under these tests is the
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
