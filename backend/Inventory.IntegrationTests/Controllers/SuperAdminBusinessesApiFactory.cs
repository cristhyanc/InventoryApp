using System.Security.Claims;
using System.Text.Encodings.Web;
using Inventory.Domain.Nayax;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using InventoryApi.Auth.PlatformAdmin;
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
/// The API host the Super Admin business tests of issue #544 run against: one in-memory SQLite
/// connection with the real migration history, a configured platform administrator who is a member
/// of no business, and three businesses - one trading, one pending application, and one that was
/// already refused.
///
/// <para>The administrator is deliberately an actor with <strong>no membership anywhere</strong>,
/// because that is the case the whole design is about: platform authority is a configured Entra
/// <c>(tid, oid)</c> pair held outside the data, so it must grant this surface and must still be
/// refused by every business endpoint.</para>
///
/// <para><see cref="Unconfigured"/> is the same host exactly as it ships - no administrator
/// configured at all - which is what proves the fail-closed default: nobody can reach this surface,
/// including an Owner and including the actor who is the administrator on the other host.</para>
/// </summary>
public class SuperAdminBusinessesApiFactory : WebApplicationFactory<Program>
{
    public const string DirectoryTenantId = "99999999-9999-9999-9999-999999999999";

    /// <summary>The configured platform administrator, who is a member of no business.</summary>
    public const string AdministratorObjectId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    /// <summary>The Owner of the trading business - a business role, never platform authority.</summary>
    public const string TradingOwnerObjectId = "11111111-1111-1111-1111-111111111111";

    /// <summary>A Manager of that same business.</summary>
    public const string TradingManagerObjectId = "33333333-3333-3333-3333-333333333333";

    /// <summary>The person whose application is waiting for a decision.</summary>
    public const string ApplicantObjectId = "22222222-2222-2222-2222-222222222222";

    /// <summary>Somebody signed in who belongs to no business and is not the administrator.</summary>
    public const string StrangerObjectId = "44444444-4444-4444-4444-444444444444";

    public const string TradingBusinessName = "Vending Co";
    public const string PendingBusinessName = "Coastal Vending";
    public const string RejectedBusinessName = "Inland Vending";

    public const string PendingContactEmail = "owner@coastal.example.com";
    public const string PendingAbn = "51824753556";

    public static readonly DateTime CreatedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime AppliedAt = new(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime RejectedAt = new(2026, 9, 20, 7, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly RecordingLoggerProvider _logs = new();

    public SuperAdminBusinessesApiFactory() => _connection.Open();

    /// <summary>The host exactly as it ships: no platform administrator configured at all.</summary>
    public sealed class Unconfigured : SuperAdminBusinessesApiFactory
    {
        protected override bool ConfigurePlatformAdministrator => false;
    }

    public int TradingBusinessId { get; private set; }

    public int PendingBusinessId { get; private set; }

    public int RejectedBusinessId { get; private set; }

    public int ApplicantMembershipId { get; private set; }

    /// <summary>The trading business's Owner membership - the one a reactivation needs to exist.</summary>
    public int TradingOwnerMembershipId { get; private set; }

    /// <summary>The trading business's Manager membership, which no rule requires to stay active.</summary>
    public int TradingManagerMembershipId { get; private set; }

    protected virtual bool ConfigurePlatformAdministrator => true;

    /// <summary>An unrestricted context for arranging and verifying state outside the boundary.</summary>
    public AppDbContext Database()
    {
        // Resolving Services builds and starts the host, which is what applies the migrations this
        // context then reads and writes through.
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

    /// <summary>The administrator's own client, which every allowed call in these tests uses.</summary>
    public HttpClient AsAdministrator() => Authenticated(AdministratorObjectId);

    /// <summary>The administrator signed in without the delegated scope the API requires.</summary>
    public HttpClient WithoutScope(string objectId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.DirectoryTenantIdHeader, DirectoryTenantId);
        client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ObjectIdHeader, objectId);
        return client;
    }

    public void ClearLog() => _logs.Clear();

    /// <summary>Everything the host has logged since the last <see cref="ClearLog"/>.</summary>
    public IReadOnlyList<string> LoggedEntries() => _logs.Entries();

    public async Task<Business> BusinessAsync(int businessId)
    {
        await using var db = Database();
        return await db.Businesses.AsNoTracking().SingleAsync(business => business.Id == businessId);
    }

    public async Task<BusinessMembership> MembershipAsync(int membershipId)
    {
        await using var db = Database();
        return await db.BusinessMemberships
            .AsNoTracking()
            .SingleAsync(membership => membership.Id == membershipId);
    }

    public async Task SetBusinessStatusAsync(int businessId, BusinessStatus status)
    {
        await using var db = Database();
        (await db.Businesses.SingleAsync(business => business.Id == businessId)).Status = status;
        await db.SaveChangesAsync();
    }

    public async Task SetMembershipActiveAsync(int membershipId, bool isActive)
    {
        await using var db = Database();
        var membership = await db.BusinessMemberships.SingleAsync(candidate => candidate.Id == membershipId);
        membership.IsActive = isActive;
        membership.StatusChangedAtUtc = CreatedAt;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Puts the world back to the three businesses above and nobody else, so each test starts from
    /// the same world rather than from whatever its predecessor decided.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var db = Database();
        db.BusinessNayaxConnections.RemoveRange(await db.BusinessNayaxConnections.ToListAsync());
        db.Products.RemoveRange(await db.Products.ToListAsync());
        // Before the memberships: a redeemed invite names the membership it created with a
        // restricted foreign key, so the invites have to go first.
        db.MemberInvites.RemoveRange(await db.MemberInvites.ToListAsync());
        await db.SaveChangesAsync();
        db.BusinessMemberships.RemoveRange(await db.BusinessMemberships.ToListAsync());
        await db.SaveChangesAsync();
        db.Businesses.RemoveRange(await db.Businesses.ToListAsync());
        await db.SaveChangesAsync();

        var trading = new Business
        {
            Name = TradingBusinessName,
            TimeZoneId = "Australia/Sydney",
            Status = BusinessStatus.Active,
            CreatedAtUtc = CreatedAt,
        };
        var pending = new Business
        {
            Name = PendingBusinessName,
            TimeZoneId = "Australia/Brisbane",
            Status = BusinessStatus.Pending,
            ContactEmail = PendingContactEmail,
            Abn = PendingAbn,
            GstRegistered = true,
            AppliedAtUtc = AppliedAt,
            CreatedAtUtc = AppliedAt,
        };
        var rejected = new Business
        {
            Name = RejectedBusinessName,
            TimeZoneId = "Australia/Sydney",
            Status = BusinessStatus.Rejected,
            AppliedAtUtc = AppliedAt,
            DecidedAtUtc = RejectedAt,
            RejectionReason = "Needs a registered ABN.",
            CreatedAtUtc = AppliedAt,
        };

        db.Businesses.AddRange(trading, pending, rejected);
        await db.SaveChangesAsync();

        TradingBusinessId = trading.Id;
        PendingBusinessId = pending.Id;
        RejectedBusinessId = rejected.Id;

        var applicant = Membership(pending.Id, ApplicantObjectId, BusinessRole.Owner, isActive: true);
        var tradingOwner = Membership(trading.Id, TradingOwnerObjectId, BusinessRole.Owner, isActive: true);
        var tradingManager = Membership(trading.Id, TradingManagerObjectId, BusinessRole.Manager, isActive: true);
        db.BusinessMemberships.AddRange(
            tradingOwner,
            tradingManager,
            applicant,
            // The refused application's applicant was released by its rejection, which is why that
            // business has no active membership at all.
            Membership(rejected.Id, "55555555-5555-5555-5555-555555555555", BusinessRole.Owner, isActive: false));
        await db.SaveChangesAsync();

        ApplicantMembershipId = applicant.Id;
        TradingOwnerMembershipId = tradingOwner.Id;
        TradingManagerMembershipId = tradingManager.Id;

        ClearLog();
    }

    /// <summary>
    /// One more business in the state a test needs - a second trading business for somebody released
    /// from a deactivated one to join, for example (issue #545).
    /// </summary>
    public async Task<int> SeedBusinessAsync(string name, BusinessStatus status)
    {
        await using var db = Database();
        var business = new Business
        {
            Name = name,
            TimeZoneId = "Australia/Sydney",
            Status = status,
            CreatedAtUtc = CreatedAt,
        };

        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        return business.Id;
    }

    /// <summary>One more membership, in whichever business and state a test needs (issue #545).</summary>
    public async Task<int> SeedMembershipAsync(
        int businessId,
        string objectId,
        BusinessRole role,
        bool isActive,
        string? displayName = null,
        string? email = null)
    {
        await using var db = Database();
        var membership = Membership(businessId, objectId, role, isActive);
        membership.DisplayName = displayName;
        membership.Email = email;

        db.BusinessMemberships.Add(membership);
        await db.SaveChangesAsync();
        return membership.Id;
    }

    /// <summary>
    /// One invite of one business, stored exactly as the Owner-facing creation stores it: the token
    /// only as its hash, so a test can present the plaintext to the redemption endpoint while the row
    /// holds nothing usable (issue #545).
    ///
    /// <para>The expiry defaults to a week from <em>now</em> rather than from the seeded creation
    /// instant, because whether an invite is still live is judged against the host's real clock: an
    /// invite created at the fixture's historical instant would already have run out, and "expired"
    /// is the one state a deactivation deliberately leaves alone. A test that wants an expired invite
    /// passes <paramref name="expiresAtUtc"/> in the past.</para>
    /// </summary>
    public async Task<int> SeedInviteAsync(
        int businessId,
        string token,
        BusinessRole role = BusinessRole.Operator,
        MemberInviteStatus status = MemberInviteStatus.Pending,
        DateTime? createdAtUtc = null,
        DateTime? expiresAtUtc = null)
    {
        await using var db = Database();
        var created = createdAtUtc ?? CreatedAt;
        var invite = new MemberInvite
        {
            BusinessId = businessId,
            Role = role,
            TokenHash = MemberInviteToken.Hash(token),
            Status = status,
            CreatedByDirectoryTenantId = DirectoryTenantId,
            CreatedByObjectId = TradingOwnerObjectId,
            CreatedAtUtc = created,
            ExpiresAtUtc = expiresAtUtc ?? MemberInvitePolicy.ExpiresAt(DateTime.UtcNow),
            CancelledAtUtc = status == MemberInviteStatus.Cancelled ? created : null,
            UsedAtUtc = status == MemberInviteStatus.Used ? created : null,
        };

        db.MemberInvites.Add(invite);
        await db.SaveChangesAsync();
        return invite.Id;
    }

    public async Task<MemberInvite> InviteAsync(int inviteId)
    {
        await using var db = Database();
        return await db.MemberInvites.AsNoTracking().SingleAsync(invite => invite.Id == inviteId);
    }

    public async Task<IReadOnlyList<BusinessMembership>> MembershipsOfAsync(int businessId)
    {
        await using var db = Database();
        return await db.BusinessMemberships
            .AsNoTracking()
            .Where(membership => membership.BusinessId == businessId)
            .OrderBy(membership => membership.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Seeds one tenant-owned product and one stored Nayax connection for the trading business, so a
    /// test can prove that the directory reports the connection as a yes/no and reports nothing at
    /// all about the product.
    /// </summary>
    public async Task SeedTenantDataAsync(string productName, string tokenCiphertextMarker)
    {
        await using var db = Database();
        db.Products.Add(new Product { BusinessId = TradingBusinessId, Name = productName });
        db.BusinessNayaxConnections.Add(new BusinessNayaxConnection
        {
            BusinessId = TradingBusinessId,
            OperatorId = "2002736764",
            AccessTokenCiphertext = tokenCiphertextMarker,
            EncryptionKeyId = "2026-10",
            Status = NayaxConnectionStatus.Ready,
            CredentialRevision = 1,
            UpdatedAtUtc = CreatedAt,
        });
        await db.SaveChangesAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Testing applies pending migrations at startup, so the schema under these tests is the
        // migration history rather than a model snapshot.
        builder.UseEnvironment("Testing");

        if (ConfigurePlatformAdministrator)
        {
            builder.UseSetting($"{PlatformAdminOptions.SectionName}:DirectoryTenantId", DirectoryTenantId);
            builder.UseSetting($"{PlatformAdminOptions.SectionName}:ObjectId", AdministratorObjectId);
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

    private static BusinessMembership Membership(
        int businessId,
        string objectId,
        BusinessRole role,
        bool isActive) =>
        new()
        {
            BusinessId = businessId,
            DirectoryTenantId = DirectoryTenantId,
            ObjectId = objectId,
            Role = role,
            IsActive = isActive,
            CreatedAtUtc = CreatedAt,
            StatusChangedAtUtc = CreatedAt,
        };

    /// <summary>
    /// Stands in for Entra: the caller's <c>(tid, oid)</c> pair and delegated scope come from
    /// request headers, so the platform-admin policy, the middleware's own re-check of it, the scope
    /// requirement and membership resolution are exercised exactly as they are in production without
    /// a real token.
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

    /// <summary>
    /// Captures everything the host logs, from every category, so the audit assertions are made over
    /// the whole request's logging rather than one logger's - including what the audit event must
    /// <em>not</em> contain.
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

        public IReadOnlyList<string> Entries()
        {
            lock (_entries)
            {
                return _entries.ToArray();
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
