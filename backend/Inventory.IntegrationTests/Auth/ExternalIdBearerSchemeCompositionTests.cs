using Inventory.Infrastructure.Data;
using InventoryApi.Auth.ExternalIdentity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// Which bearer schemes the composition root registers, decided from configuration alone (issue
/// #710, design § 5.3 and § 5.4's last two rows).
///
/// The shipped state is the fail-closed one: with the <c>ExternalId</c> section empty - which is
/// how source control carries it - the API registers exactly the one real workforce scheme it
/// always registered, and there is no second scheme for a token, a configuration value or a later
/// code change to select. A half-filled or malformed section means the same thing, deliberately:
/// a configuration mistake must not be able to turn "accept one pinned issuer" into "accept
/// whatever is configured", and it must never take workforce sign-in down with it.
///
/// Only a complete, well-formed section registers the second scheme, and then the default scheme
/// becomes the issuer-selecting policy scheme that chooses between the two validators.
/// </summary>
public sealed class ExternalIdBearerSchemeCompositionTests
{
    private const string ExternalIdTenantId = "9d6ff2a1-1f3e-4c6b-9a15-7b2c4f8e0d31";
    private const string ExternalIdClientId = "4f1c8e55-27b4-4a0e-9d7c-6a3f2b1e5c08";

    [Fact]
    public async Task A_complete_external_id_section_registers_the_pinned_scheme_beside_the_workforce_one()
    {
        using var factory = new HostFactory(new Dictionary<string, string?>
        {
            ["ExternalId:Instance"] = "https://contoso.ciamlogin.com/",
            ["ExternalId:TenantId"] = ExternalIdTenantId,
            ["ExternalId:ClientId"] = ExternalIdClientId,
            ["ExternalId:Scopes"] = "access_as_user",
        });

        var schemes = factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        var names = (await schemes.GetAllSchemesAsync()).Select(scheme => scheme.Name).ToArray();

        Assert.Contains(JwtBearerDefaults.AuthenticationScheme, names);
        Assert.Contains("ExternalIdBearer", names);
        Assert.Equal("IssuerSelectingBearer", (await schemes.GetDefaultAuthenticateSchemeAsync())?.Name);
    }

    /// <summary>
    /// Every one of these is a section somebody started filling in and did not finish, or filled
    /// in with a value that is not what the setting means. None of them registers the second
    /// scheme, and every one of them leaves the workforce scheme exactly as the shipped state has
    /// it.
    /// </summary>
    [Theory]
    // The shipped state: present and empty, exactly as appsettings.json carries it.
    [InlineData("", "", "")]
    [InlineData("   ", "   ", "   ")]
    // Half-filled.
    [InlineData("https://contoso.ciamlogin.com/", "", "")]
    [InlineData("", ExternalIdTenantId, "")]
    [InlineData("", "", ExternalIdClientId)]
    [InlineData("https://contoso.ciamlogin.com/", ExternalIdTenantId, "")]
    [InlineData("https://contoso.ciamlogin.com/", "", ExternalIdClientId)]
    [InlineData("", ExternalIdTenantId, ExternalIdClientId)]
    // Filled with something that is not the setting: a tenant that is not a GUID, a client id
    // that is not one, an authority that is not an absolute HTTPS host, and an authority
    // carrying a path of its own.
    [InlineData("https://contoso.ciamlogin.com/", "contoso.ciamlogin.com", ExternalIdClientId)]
    [InlineData("https://contoso.ciamlogin.com/", ExternalIdTenantId, "access_as_user")]
    [InlineData("http://contoso.ciamlogin.com/", ExternalIdTenantId, ExternalIdClientId)]
    [InlineData("contoso.ciamlogin.com", ExternalIdTenantId, ExternalIdClientId)]
    [InlineData("https://contoso.ciamlogin.com/oauth2/v2.0/", ExternalIdTenantId, ExternalIdClientId)]
    public async Task An_empty_or_unusable_external_id_section_registers_only_the_workforce_scheme(
        string instance, string tenantId, string clientId)
    {
        using var factory = new HostFactory(new Dictionary<string, string?>
        {
            ["ExternalId:Instance"] = instance,
            ["ExternalId:TenantId"] = tenantId,
            ["ExternalId:ClientId"] = clientId,
            ["ExternalId:Scopes"] = "access_as_user",
        });

        var schemes = factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        var names = (await schemes.GetAllSchemesAsync()).Select(scheme => scheme.Name).ToArray();

        Assert.Equal([JwtBearerDefaults.AuthenticationScheme], names);
        Assert.Equal(
            JwtBearerDefaults.AuthenticationScheme,
            (await schemes.GetDefaultAuthenticateSchemeAsync())?.Name);
    }

    /// <summary>
    /// The section is absent altogether rather than empty - the state a deployment that has never
    /// heard of this setting is in. Absent must mean exactly what empty means.
    /// </summary>
    [Fact]
    public async Task An_absent_external_id_section_registers_only_the_workforce_scheme()
    {
        using var factory = new HostFactory(new Dictionary<string, string?>());

        var schemes = factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.Equal(
            [JwtBearerDefaults.AuthenticationScheme],
            (await schemes.GetAllSchemesAsync()).Select(scheme => scheme.Name).ToArray());
    }

    /// <summary>
    /// The decision itself, read from configuration without hosting anything: three states, and a
    /// problem that names the setting keys so an operator can fix it - never their values, because
    /// a log line is not the place to echo configuration back.
    /// </summary>
    [Fact]
    public void The_configuration_decision_distinguishes_absent_unusable_and_complete()
    {
        Assert.Equal(
            ExternalIdAuthenticationState.NotConfigured,
            Decision(("ExternalId:Scopes", "access_as_user")).State);

        var unusable = Decision(
            ("ExternalId:Instance", "https://contoso.ciamlogin.com/"),
            ("ExternalId:TenantId", ExternalIdTenantId));

        Assert.Equal(ExternalIdAuthenticationState.Incomplete, unusable.State);
        Assert.Null(unusable.Authority);
        Assert.Contains("ExternalId:ClientId", unusable.Problem);
        Assert.DoesNotContain(ExternalIdTenantId, unusable.Problem);

        var complete = Decision(
            ("ExternalId:Instance", "https://contoso.ciamlogin.com/"),
            ("ExternalId:TenantId", ExternalIdTenantId),
            ("ExternalId:ClientId", ExternalIdClientId));

        Assert.True(complete.IsRegistered);
        Assert.Null(complete.Problem);
        Assert.Equal(Guid.Parse(ExternalIdTenantId), complete.Authority!.TenantId);
        Assert.Equal(
            $"https://contoso.ciamlogin.com/{ExternalIdTenantId}/v2.0",
            complete.Authority.MetadataAuthority);
        Assert.Equal(
            [ExternalIdClientId, $"api://{ExternalIdClientId}"],
            complete.Authority.ValidAudiences);
    }

    /// <summary>
    /// Which scheme an unvalidated issuer is routed to. The rule names the configured tenant and
    /// nothing else: it answers for the issuer form Microsoft documents and for the subdomain host
    /// its endpoints use, and it answers <c>false</c> for every workforce issuer, for another
    /// customer identity tenant, and for anything that is not an absolute URI - which all stay
    /// with the workforce validator, the scheme that must keep working whatever this routing does.
    /// </summary>
    [Theory]
    [InlineData($"https://{ExternalIdTenantId}.ciamlogin.com/{ExternalIdTenantId}/v2.0", true)]
    [InlineData($"https://contoso.ciamlogin.com/{ExternalIdTenantId}/v2.0", true)]
    [InlineData($"https://{ExternalIdTenantId}.ciamlogin.com/v2.0", true)]
    [InlineData("https://dddddddd-0000-4000-8000-00000000000d.ciamlogin.com/dddddddd-0000-4000-8000-00000000000d/v2.0", false)]
    [InlineData("https://login.microsoftonline.com/aaaaaaaa-0000-4000-8000-00000000000a/v2.0", false)]
    [InlineData("https://login.microsoftonline.com/common/v2.0", false)]
    [InlineData("https://accounts.google.com", false)]
    [InlineData("contoso.ciamlogin.com", false)]
    [InlineData("")]
    [InlineData(null)]
    public void Only_an_issuer_naming_the_configured_tenant_is_routed_to_the_pinned_scheme(
        string? unvalidatedIssuer, bool expected = false)
    {
        var authority = Decision(
            ("ExternalId:Instance", "https://contoso.ciamlogin.com/"),
            ("ExternalId:TenantId", ExternalIdTenantId),
            ("ExternalId:ClientId", ExternalIdClientId)).Authority;

        Assert.Equal(expected, authority!.NamesThisTenant(unvalidatedIssuer));
    }

    /// <summary>
    /// Each scheme's accepted audiences are stated in its own validation parameters, and they are
    /// the thing that decides (design § 5.1 rule 4).
    ///
    /// The second assertion is the one that keeps the first from being decoration.
    /// <c>Microsoft.Identity.Web</c> validates the audience with an <c>AudienceValidator</c>
    /// delegate, and the token-validation library calls such a delegate <em>instead of</em>
    /// comparing against the valid-audience list - so a pinned list with that delegate still in
    /// place would decide nothing at all, and a later edit to the list would silently have no
    /// effect.
    /// </summary>
    [Fact]
    public void Each_scheme_states_its_own_accepted_audiences_and_nothing_else_decides_them()
    {
        const string WorkforceClientId = "3dd61784-7289-4c2d-b0aa-3bb586a92004";

        using var factory = new HostFactory(new Dictionary<string, string?>
        {
            ["AzureAd:ClientId"] = WorkforceClientId,
            ["ExternalId:Instance"] = "https://contoso.ciamlogin.com/",
            ["ExternalId:TenantId"] = ExternalIdTenantId,
            ["ExternalId:ClientId"] = ExternalIdClientId,
        });

        var options = factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();

        foreach (var (scheme, clientId) in new[]
        {
            (JwtBearerDefaults.AuthenticationScheme, WorkforceClientId),
            ("ExternalIdBearer", ExternalIdClientId),
        })
        {
            var parameters = options.Get(scheme).TokenValidationParameters;

            Assert.Equal([clientId, $"api://{clientId}"], parameters.ValidAudiences);
            Assert.Null(parameters.AudienceValidator);
            Assert.True(parameters.ValidateAudience);
        }
    }

    private static ExternalIdAuthenticationDecision Decision(params (string Key, string Value)[] settings) =>
        ExternalIdAuthenticationDecision.From(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting =>
                new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build());

    private sealed class HostFactory : WebApplicationFactory<Program>
    {
        private readonly IReadOnlyDictionary<string, string?> _settings;
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        public HostFactory(IReadOnlyDictionary<string, string?> settings)
        {
            E2ETestHostContentRoot.Pin();
            _settings = settings;
            _connection.Open();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            foreach (var (key, value) in _settings)
            {
                builder.UseSetting(key, value);
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
