using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using InventoryApi.Auth.ExternalIdentity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Xunit;

using Tokens = InventoryApi.Tests.Auth.TrustedIssuerTokenValidationTests.TestTokens;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// The rollback rehearsal the design asks for before production activation (issue #714;
/// `docs/identity-provider-decision.md` § 15, whose § 15.3 says in so many words that it "must be
/// rehearsed in #714").
///
/// <para>Lever 1 is a configuration edit: clear the <c>ExternalId__*</c> settings and the second
/// bearer scheme is not registered at all. What that must do, and what it must <em>not</em> do, is
/// the whole of § 15.1's table, and it is asserted here over <strong>one database</strong> read by
/// three hosts in turn - configured, rolled back, configured again - because "their memberships are
/// retained and become usable again the moment the configuration is restored" is a claim about data
/// surviving a configuration change, not about either state on its own.</para>
///
/// <para>Lever 2, reverting the code, is deliberately not simulated: with no schema change and no
/// migration in this design it is an ordinary deployment of an earlier commit, and its outcome for
/// people is identical to lever 1's. What a code revert would remove is exactly what an empty
/// configuration section already stops registering, which
/// <see cref="ExternalIdBearerSchemeCompositionTests"/> pins.</para>
///
/// <para>The observability requirement is here too, because an outage and a rollback are when the
/// logs are read: an authentication failure, a refused issuer and a redemption must leave no bearer
/// token and no invitation secret behind in them.</para>
/// </summary>
public sealed class CustomerIdentityRollbackRehearsalTests : IDisposable
{
    private const string WorkforceBusinessName = "Workforce Vending";
    private const string CustomerIdentityBusinessName = "Customer Identity Vending";

    private const string AdministratorObjectId = "0a000000-0000-4000-8000-0000000000aa";
    private const string WorkforceOwnerObjectId = "1b000000-0000-4000-8000-0000000000bb";
    private const string CustomerIdentityOwnerObjectId = "1c000000-0000-4000-8000-0000000000cc";
    private const string CustomerIdentityJoinerObjectId = "2c000000-0000-4000-8000-0000000000cd";

    private const int WorkforceBusinessId = 1;
    private const int CustomerIdentityBusinessId = 2;

    private static readonly DateTime CreatedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The one database every host below is pointed at. Keeping it outside the hosts is what makes
    /// the rehearsal a rehearsal: the configuration changes under the data, exactly as clearing an
    /// application setting changes it under a production database.
    /// </summary>
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public CustomerIdentityRollbackRehearsalTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    /// <summary>
    /// § 15.1's first two rows. Customer identity users "lose access - they cannot sign in at all",
    /// and existing workforce users see "no change whatsoever": the same business, the same role, on
    /// the same database, from the same token they always used.
    /// </summary>
    [Fact]
    public async Task Clearing_the_configuration_refuses_customer_identity_tokens_and_leaves_workforce_signin_untouched()
    {
        await SeedAsync();

        using var configured = CreateHost(customerIdentityConfigured: true);
        Assert.Equal(
            (CustomerIdentityBusinessName, nameof(BusinessRole.Owner)),
            await AccessAsync(CustomerIdentity(configured, CustomerIdentityOwnerObjectId)));

        using var rolledBack = CreateHost(customerIdentityConfigured: false);

        var refused = await CustomerIdentity(rolledBack, CustomerIdentityOwnerObjectId)
            .GetAsync("/api/me/access");

        // Not "no membership" and not "no business": the issuer is not trusted at all any more, so
        // there is nothing to resolve a membership from.
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await CustomerIdentity(rolledBack, CustomerIdentityOwnerObjectId)
                .GetAsync("/api/me/account-state")).StatusCode);

        Assert.Equal(
            (WorkforceBusinessName, nameof(BusinessRole.Owner)),
            await AccessAsync(Workforce(rolledBack, WorkforceOwnerObjectId)));
    }

    /// <summary>
    /// § 15.1's third, fourth and seventh rows, and the reason the rollback is reversible: nothing is
    /// deleted or deactivated by it. The membership and the outstanding invite are still there while
    /// the issuer is untrusted, and restoring the three settings gives the same identity the same
    /// business and the same role back - with the invite still redeemable by somebody new.
    /// </summary>
    [Fact]
    public async Task A_rollback_retains_the_memberships_and_the_live_invites_it_cannot_serve()
    {
        await SeedAsync();

        string inviteToken;
        using (var configured = CreateHost(customerIdentityConfigured: true))
        {
            inviteToken = await CreateInviteAsync(
                CustomerIdentity(configured, CustomerIdentityOwnerObjectId), BusinessRole.Manager);
        }

        using (var rolledBack = CreateHost(customerIdentityConfigured: false))
        {
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await CustomerIdentity(rolledBack, CustomerIdentityJoinerObjectId)
                    .PostAsJsonAsync("/api/invites/redeem", new { token = inviteToken })).StatusCode);

            // The row the rollback must not touch, read from outside the request boundary.
            var membership = await MembershipAsync(Tokens.ExternalIdTenantId, CustomerIdentityOwnerObjectId);
            Assert.True(membership.IsActive);
            Assert.Equal(BusinessRole.Owner, membership.Role);
            Assert.Equal(CustomerIdentityBusinessId, membership.BusinessId);

            // And the invite: still pending, still with its own 7-day lifetime running.
            var invite = Assert.Single(await InvitesAsync());
            Assert.Equal(MemberInviteStatus.Pending, invite.Status);
            Assert.Equal(CustomerIdentityBusinessId, invite.BusinessId);
            Assert.Null(invite.CancelledAtUtc);
            Assert.Null(invite.UsedAtUtc);
        }

        // Restoring the configuration is the whole of the recovery: no deployment, no data step.
        using var restored = CreateHost(customerIdentityConfigured: true);

        Assert.Equal(
            (CustomerIdentityBusinessName, nameof(BusinessRole.Owner)),
            await AccessAsync(CustomerIdentity(restored, CustomerIdentityOwnerObjectId)));

        var redeemed = await CustomerIdentity(restored, CustomerIdentityJoinerObjectId)
            .PostAsJsonAsync("/api/invites/redeem", new { token = inviteToken });
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        Assert.Equal(
            (CustomerIdentityBusinessName, nameof(BusinessRole.Manager)),
            await AccessAsync(CustomerIdentity(restored, CustomerIdentityJoinerObjectId)));
    }

    /// <summary>
    /// § 15.1's "Super Admin: no change" row, which is the recovery route a rollback has to keep:
    /// the configured pair is a workforce identity and never depended on the customer identity
    /// tenant (design § 8), so the platform-admin surface is reachable while that tenant is
    /// untrusted - and the same object id presented from it is not merely refused, it does not
    /// authenticate at all.
    /// </summary>
    [Fact]
    public async Task The_legacy_super_admin_recovery_route_still_works_while_customer_identity_signin_is_off()
    {
        await SeedAsync();

        using var rolledBack = CreateHost(customerIdentityConfigured: false);
        var administrator = Workforce(rolledBack, AdministratorObjectId);

        foreach (var read in new[]
        {
            "/api/super-admin/businesses",
            "/api/super-admin/businesses/pending-count",
            "/api/super-admin/support-sessions/active",
        })
        {
            // Successful rather than exactly `200`: an administrator with no active session is
            // answered `204`, and what this asserts is that the surface is reachable.
            var response = await administrator.GetAsync(read);
            Assert.True(response.IsSuccessStatusCode, $"{read} answered {response.StatusCode}");
        }

        // Deactivating a business - the lever an administrator needs during an incident - still works.
        Assert.Equal(
            HttpStatusCode.OK,
            (await administrator.PostAsync(
                $"/api/super-admin/businesses/{CustomerIdentityBusinessId}/deactivate", content: null)).StatusCode);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await CustomerIdentity(rolledBack, AdministratorObjectId)
                .GetAsync("/api/super-admin/businesses")).StatusCode);
    }

    /// <summary>
    /// What an incident leaves in the logs. A refused issuer, an expired session and a redemption are
    /// exactly the events an operator reads during a rollout or an outage, and none of them may carry
    /// the credential that caused it: not the bearer token, not the invitation token, and not a
    /// member's mailbox (design § 10.2 and § 14.3, and `AGENTS.md` § Nayax integration's logging
    /// rule, which is a repository-wide one).
    /// </summary>
    [Fact]
    public async Task An_authentication_failure_or_a_redemption_logs_no_token_and_no_invitation_secret()
    {
        await SeedAsync();

        const string Mailbox = "casey@example.test";

        string inviteToken;
        using (var configured = CreateHost(customerIdentityConfigured: true))
        {
            inviteToken = await CreateInviteAsync(
                CustomerIdentity(configured, CustomerIdentityOwnerObjectId), BusinessRole.Manager);
        }

        var logs = new RecordingLoggerProvider();

        using (var configured = CreateHost(customerIdentityConfigured: true, logs: logs))
        {
            // A local email/password account, whose token carries a display-only mailbox, redeems the
            // invite: the success path, which is where the token could reach an audit event.
            var joiner = Bearer(
                configured, Tokens.LocalAccountAccessToken(CustomerIdentityJoinerObjectId, Mailbox));
            Assert.Equal(
                HttpStatusCode.OK,
                (await joiner.PostAsJsonAsync("/api/invites/redeem", new { token = inviteToken })).StatusCode);

            // And an expired token, which is the ordinary end of a session.
            var expired = Tokens.Token(
                Tokens.ExternalIdSigningKey,
                Tokens.ExternalIdIssuer,
                Tokens.ExternalIdApiClientId,
                Tokens.Claims(Tokens.ExternalIdTenantId, CustomerIdentityOwnerObjectId),
                notBefore: DateTime.UtcNow.AddHours(-2),
                expires: DateTime.UtcNow.AddHours(-1));
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await Bearer(configured, expired).GetAsync("/api/me/access")).StatusCode);
        }

        string untrustedIssuerToken;
        using (var rolledBack = CreateHost(customerIdentityConfigured: false, logs: logs))
        {
            // The rollback's own refusal: an issuer this host trusts nothing of.
            untrustedIssuerToken = Tokens.ExternalIdAccessToken(CustomerIdentityOwnerObjectId);
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await Bearer(rolledBack, untrustedIssuerToken).GetAsync("/api/me/access")).StatusCode);
        }

        var recorded = logs.Text();

        // The guard against a vacuous assertion: the host did log, so the absences below are
        // absences rather than an empty string.
        Assert.NotEmpty(recorded);

        Assert.DoesNotContain(inviteToken, recorded, StringComparison.Ordinal);
        Assert.DoesNotContain(untrustedIssuerToken, recorded, StringComparison.Ordinal);
        Assert.DoesNotContain(Mailbox, recorded, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CustomerIdentity(WebApplicationFactory<Program> host, string objectId) =>
        Bearer(host, Tokens.ExternalIdAccessToken(objectId));

    private static HttpClient Workforce(WebApplicationFactory<Program> host, string objectId) =>
        Bearer(host, Tokens.WorkforceAccessToken(Tokens.WorkforceTenantX, objectId));

    private static HttpClient Bearer(WebApplicationFactory<Program> host, string accessToken)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private static async Task<(string? BusinessName, string? Role)> AccessAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/me/access");
        response.EnsureSuccessStatusCode();
        var access = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        return (access.GetProperty("businessName").GetString(), access.GetProperty("role").GetString());
    }

    private static async Task<string> CreateInviteAsync(HttpClient owner, BusinessRole role)
    {
        var response = await owner.PostAsJsonAsync(
            "/api/members/invites", new { role = role.ToString(), note = "Issue #714 rollback rehearsal" });
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement
            .GetProperty("token")
            .GetString()!;
    }

    private RollbackHost CreateHost(bool customerIdentityConfigured, RecordingLoggerProvider? logs = null) =>
        new(_connection, customerIdentityConfigured, logs);

    private AppDbContext Database() =>
        TestAppDbContext.Unrestricted(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

    private async Task<BusinessMembership> MembershipAsync(string directoryTenantId, string objectId)
    {
        await using var db = Database();

        return await db.BusinessMemberships
            .AsNoTracking()
            .SingleAsync(membership =>
                membership.DirectoryTenantId == directoryTenantId && membership.ObjectId == objectId);
    }

    private async Task<IReadOnlyList<MemberInvite>> InvitesAsync()
    {
        await using var db = Database();

        return await db.MemberInvites.AsNoTracking().OrderBy(invite => invite.Id).ToListAsync();
    }

    /// <summary>
    /// Two businesses, one member each, and one configured platform administrator. The schema comes
    /// from the migrations the first host applies, so the seeding waits for a host to exist.
    /// </summary>
    private async Task SeedAsync()
    {
        using var host = CreateHost(customerIdentityConfigured: true);
        _ = host.Services;

        await using var db = Database();

        if (await db.Businesses.AnyAsync())
        {
            return;
        }

        db.Businesses.AddRange(
            Business(WorkforceBusinessId, WorkforceBusinessName),
            Business(CustomerIdentityBusinessId, CustomerIdentityBusinessName));
        await db.SaveChangesAsync();

        db.BusinessMemberships.AddRange(
            Membership(WorkforceBusinessId, Tokens.WorkforceTenantX, WorkforceOwnerObjectId),
            Membership(CustomerIdentityBusinessId, Tokens.ExternalIdTenantId, CustomerIdentityOwnerObjectId));
        await db.SaveChangesAsync();
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

    private static BusinessMembership Membership(int businessId, string directoryTenantId, string objectId) =>
        new()
        {
            BusinessId = businessId,
            DirectoryTenantId = directoryTenantId,
            ObjectId = objectId,
            Role = BusinessRole.Owner,
            IsActive = true,
            CreatedAtUtc = CreatedAt,
            StatusChangedAtUtc = CreatedAt,
        };

    /// <summary>
    /// The application, hosted either with a customer identity tenant configured or with that
    /// section empty - the rollback state - over a database somebody else owns. Nothing else differs
    /// between the two: the same code, the same database, the same workforce configuration.
    /// </summary>
    private sealed class RollbackHost : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection;
        private readonly bool _customerIdentityConfigured;
        private readonly RecordingLoggerProvider? _logs;

        public RollbackHost(
            SqliteConnection connection, bool customerIdentityConfigured, RecordingLoggerProvider? logs)
        {
            E2ETestHostContentRoot.Pin();
            _connection = connection;
            _customerIdentityConfigured = customerIdentityConfigured;
            _logs = logs;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.UseSetting("AzureAd:Instance", "https://login.microsoftonline.com/");
            builder.UseSetting("AzureAd:TenantId", "common");
            builder.UseSetting("AzureAd:ClientId", Tokens.WorkforceApiClientId);
            builder.UseSetting("AzureAd:Scopes", "access_as_user");

            // The rollback lever itself: three empty identifiers (design § 15.1).
            builder.UseSetting("ExternalId:Instance", _customerIdentityConfigured ? Tokens.ExternalIdInstance : "");
            builder.UseSetting("ExternalId:TenantId", _customerIdentityConfigured ? Tokens.ExternalIdTenantId : "");
            builder.UseSetting("ExternalId:ClientId", _customerIdentityConfigured ? Tokens.ExternalIdApiClientId : "");
            builder.UseSetting("ExternalId:Scopes", "access_as_user");

            builder.UseSetting("PlatformAdmin:DirectoryTenantId", Tokens.WorkforceTenantX);
            builder.UseSetting("PlatformAdmin:ObjectId", AdministratorObjectId);

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

            if (_logs is not null)
            {
                builder.ConfigureLogging(logging =>
                {
                    logging.AddProvider(_logs);
                    logging.SetMinimumLevel(LogLevel.Trace);
                });
            }

            builder.ConfigureTestServices(services =>
            {
                services.Configure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options => options.ConfigurationManager =
                        new StaticConfigurationManager<OpenIdConnectConfiguration>(Tokens.WorkforceMetadata));

                if (!_customerIdentityConfigured)
                {
                    // There is no second scheme to hand a metadata document to, which is the point.
                    return;
                }

                services.Configure<JwtBearerOptions>(
                    ExternalIdBearerScheme.Name,
                    options => options.ConfigurationManager =
                        new StaticConfigurationManager<OpenIdConnectConfiguration>(Tokens.ExternalIdMetadata));
            });
        }
    }

    /// <summary>
    /// Captures everything every host logs, scopes included, so the absence assertions are made over
    /// the whole of the logging rather than over one logger's.
    /// </summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _entries = [];

        public ILogger CreateLogger(string categoryName) => new Recording(this);

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
