using System.Net;
using Inventory.Application.Nayax;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Nayax;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Nayax;

/// <summary>
/// Issue #726: the API starts and serves requests with <strong>no Nayax configuration at all</strong>
/// - no <c>NayaxLynx</c> section, no base URL, no operator id, no token, and no token-protection
/// key.
///
/// <para>That is the premise the whole of issue #329 rests on: a sales provider is an optional
/// add-on, so a deployment that has never heard of Nayax must come up and run every storage feature.
/// <c>NayaxCredentialFreeStartupTests</c> (issue #520) already proves it with the global
/// <em>credential</em> settings blanked; this file removes the section itself, so the only base URL
/// left is the one <see cref="NayaxLynxOptions"/> carries in code. A setting blanked to an empty
/// string and a setting that is absent are different inputs - the first one would fail the base-URL
/// validation - and only the absent case describes a deployment that never configured the
/// integration.</para>
///
/// <para>Removing a committed <c>appsettings.json</c> value needs the content root rather than a
/// host setting: a host setting can only override a key with another value, and a configuration
/// callback is applied after <c>Program.cs</c>'s top-level statements have already read
/// configuration (the reason <c>NayaxCredentialFreeStartupTests</c> and <c>TelemetryApiFactory</c>
/// use host settings). Pointing the content root at an empty directory is what makes the section
/// genuinely absent; Entra's own non-secret settings are supplied as host settings, because what is
/// under test is the absence of Nayax configuration and not of everything else.</para>
/// </summary>
public sealed class NayaxConfigurationFreeStartupTests
    : IClassFixture<NayaxConfigurationFreeStartupTests.ApiFactory>
{
    private readonly ApiFactory _factory;

    public NayaxConfigurationFreeStartupTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task The_api_starts_and_serves_requests_with_no_nayax_configuration()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Proves the fixture really removed the section, so the test above is not passing because the
    /// committed <c>appsettings.json</c> was still being read.
    /// </summary>
    [Fact]
    public void The_hosted_configuration_holds_no_nayax_section_at_all()
    {
        _ = _factory.CreateClient();
        var configuration = _factory.Services.GetRequiredService<IConfiguration>();

        Assert.Null(configuration[$"{NayaxLynxOptions.SectionName}:BaseUrl"]);
        Assert.Null(configuration[$"{NayaxLynxOptions.SectionName}:OperatorId"]);
        Assert.Null(configuration[$"{NayaxLynxOptions.SectionName}:AccessToken"]);
        Assert.Null(configuration["Nayax:Token"]);
        Assert.Null(configuration[$"{NayaxTokenProtectionOptions.SectionName}:ActiveKeyId"]);
        Assert.Empty(configuration.GetSection(NayaxLynxOptions.SectionName).GetChildren());
    }

    /// <summary>
    /// The integration is still composed, so connecting a business later needs no deployment
    /// change: the client and the per-request credential provider both resolve. What they have is
    /// no credential of their own - each call reads the current business's stored connection.
    /// </summary>
    [Fact]
    public void The_nayax_client_is_still_registered_with_no_configuration_of_its_own()
    {
        _ = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<INayaxLynxClient>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<INayaxRequestCredentialProvider>());
    }

    /// <summary>
    /// Hosts the real API with its content root pointed at an empty directory, so no
    /// <c>appsettings*.json</c> is loaded at all, over a throwaway in-memory SQLite database.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly string _emptyContentRoot = Path.Combine(
            Path.GetTempPath(), $"inventoryapp-no-nayax-configuration-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection.Open();
            Directory.CreateDirectory(_emptyContentRoot);

            // Testing applies pending migrations at startup, so the schema under this host is the
            // migration history rather than a model snapshot.
            builder.UseEnvironment("Testing");
            builder.UseSetting(WebHostDefaults.ContentRootKey, _emptyContentRoot);

            // Entra's own non-secret settings, which appsettings.json would otherwise have supplied.
            // Nothing here is a Nayax setting, which is the whole point of the fixture.
            builder.UseSetting("AzureAd:Instance", "https://login.microsoftonline.com/");
            builder.UseSetting("AzureAd:TenantId", "common");
            builder.UseSetting("AzureAd:ClientId", "00000000-0000-0000-0000-000000000001");
            builder.UseSetting("AzureAd:Scopes", "access_as_user");

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
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing)
            {
                return;
            }

            _connection.Dispose();
            if (Directory.Exists(_emptyContentRoot))
            {
                Directory.Delete(_emptyContentRoot, recursive: true);
            }
        }
    }
}
