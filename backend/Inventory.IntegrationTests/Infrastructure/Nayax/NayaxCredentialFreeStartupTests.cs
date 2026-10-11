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
/// Issue #520: the API must start and serve every non-Nayax feature with no Nayax credential in
/// configuration at all.
///
/// That is what makes the credential a business's own property rather than a deployment-wide one:
/// a global operator id and token used to be a condition for the whole API - including every
/// feature that has nothing to do with Nayax - to start, and after the global settings are removed
/// from an environment (docs/tenant-rollout.md § When to remove the global settings) there is none
/// to validate. The real host is used rather than the registration alone, because "startup" is the
/// claim being tested.
/// </summary>
public sealed class NayaxCredentialFreeStartupTests : IClassFixture<NayaxCredentialFreeStartupTests.ApiFactory>
{
    private readonly ApiFactory _factory;

    public NayaxCredentialFreeStartupTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task The_api_starts_and_serves_requests_with_no_nayax_credential_configured()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Proves the fixture really did remove the credential, so the test above is not passing
    /// because the committed <c>appsettings.json</c> operator id was still there.
    /// </summary>
    [Fact]
    public void The_hosted_configuration_really_holds_no_operator_id_or_token()
    {
        _ = _factory.CreateClient();
        var configuration = _factory.Services.GetRequiredService<IConfiguration>();

        Assert.True(string.IsNullOrWhiteSpace(configuration[$"{NayaxLynxOptions.SectionName}:OperatorId"]));
        Assert.True(string.IsNullOrWhiteSpace(configuration[$"{NayaxLynxOptions.SectionName}:AccessToken"]));
        Assert.True(string.IsNullOrWhiteSpace(configuration["Nayax:Token"]));
    }

    /// <summary>
    /// The Nayax client is still registered and still resolvable - it simply has no credential of
    /// its own, and gets one per call from the current business.
    /// </summary>
    [Fact]
    public void The_nayax_client_is_still_registered_and_resolves_its_credentials_per_request()
    {
        _ = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<INayaxLynxClient>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<INayaxRequestCredentialProvider>());
    }

    /// <summary>
    /// Hosts the real API over a throwaway in-memory SQLite database with every Nayax credential
    /// setting blanked. Host settings rather than a configuration callback, for the reason
    /// <c>TelemetryApiFactory</c> gives: <c>Program.cs</c> reads configuration while its top-level
    /// statements run, and a host setting also wins over the committed <c>appsettings.json</c>
    /// value and any ambient environment variable.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection.Open();

            builder.UseSetting($"{NayaxLynxOptions.SectionName}:OperatorId", string.Empty);
            builder.UseSetting($"{NayaxLynxOptions.SectionName}:AccessToken", string.Empty);
            builder.UseSetting("Nayax:Token", string.Empty);

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
            if (disposing)
            {
                _connection.Dispose();
            }
        }
    }
}
