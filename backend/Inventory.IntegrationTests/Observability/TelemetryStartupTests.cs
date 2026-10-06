using System.Net;
using Inventory.Infrastructure.Data;
using InventoryApi.Observability;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Xunit;

namespace InventoryApi.Tests.Observability;

/// <summary>
/// Issue #165: telemetry is diagnostics, not a dependency the API needs in order to serve
/// requests. A developer machine and this test suite have no Application Insights connection
/// string, and the API must start and answer normally - a missing setting is never a startup
/// failure and never registers an exporter with nowhere to send.
/// </summary>
public sealed class TelemetryStartupTests : IClassFixture<TelemetryStartupTests.ApiFactory>
{
    private readonly ApiFactory _factory;

    public TelemetryStartupTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task The_api_starts_and_serves_requests_without_a_connection_string()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void No_tracer_provider_is_built_without_a_connection_string()
    {
        _ = _factory.CreateClient();

        Assert.Null(_factory.Services.GetService<TracerProvider>());
    }

    public sealed class ApiFactory : TelemetryApiFactory
    {
        protected override string? ConnectionString => string.Empty;
    }
}

/// <summary>
/// Issue #165: the mirror image - with a connection string configured, the whole host still comes
/// up and the OpenTelemetry providers are built. The connection string is synthetic and its
/// endpoints point at <c>localhost</c>, so this exercises composition and startup only and sends
/// no telemetry anywhere. Live export against a real Application Insights resource is
/// human-verified in Azure.
/// </summary>
public sealed class TelemetryStartupWithConnectionStringTests
    : IClassFixture<TelemetryStartupWithConnectionStringTests.ApiFactory>
{
    private readonly ApiFactory _factory;

    public TelemetryStartupWithConnectionStringTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task The_api_starts_and_serves_requests_with_telemetry_configured()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void The_tracer_provider_is_built_when_a_connection_string_is_configured()
    {
        _ = _factory.CreateClient();

        Assert.NotNull(_factory.Services.GetService<TracerProvider>());
    }

    public sealed class ApiFactory : TelemetryApiFactory
    {
        protected override string? ConnectionString =>
            "InstrumentationKey=00000000-0000-0000-0000-000000000000;"
            + "IngestionEndpoint=https://localhost/;LiveEndpoint=https://localhost/";
    }
}

/// <summary>
/// Hosts the real API over a throwaway in-memory SQLite database, with the Application Insights
/// connection string set explicitly by the derived fixture.
/// </summary>
public abstract class TelemetryApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    /// <summary>
    /// The value <see cref="ObservabilityServiceCollectionExtensions.ConnectionStringKey"/> is
    /// configured with. It is always set, never left to the environment, so neither case depends
    /// on whether the machine running the tests happens to have the variable set.
    /// </summary>
    protected abstract string? ConnectionString { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();

        // A host setting rather than a ConfigureAppConfiguration source: Program.cs reads the
        // connection string while the top-level statements run, which is before a deferred
        // configuration callback would be applied, and a host setting also takes precedence over
        // an ambient APPLICATIONINSIGHTS_CONNECTION_STRING environment variable.
        builder.UseSetting(ObservabilityServiceCollectionExtensions.ConnectionStringKey, ConnectionString);

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
