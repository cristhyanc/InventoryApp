using System.Net;
using InventoryApi.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Http;

/// <summary>
/// Issue #164: <c>/health/live</c> and <c>/health/ready</c> must be reachable without
/// authentication, and readiness must reflect real database connectivity.
/// </summary>
public sealed class HealthEndpointsTests : IClassFixture<HealthEndpointsTests.ApiFactory>
{
    private readonly ApiFactory _factory;

    public HealthEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Liveness_endpoint_returns_200_without_authentication()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_endpoint_returns_200_without_authentication_when_the_database_is_reachable()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection.Open();

            builder.ConfigureServices(services =>
            {
                var dbContextOptions = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (dbContextOptions is not null)
                {
                    services.Remove(dbContextOptions);
                }

                services.AddDbContext<AppDbContext>(options =>
                {
                    options.UseLazyLoadingProxies();
                    options.UseSqlite(_connection);
                });
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

/// <summary>
/// A separate fixture from <see cref="HealthEndpointsTests"/> because these tests deliberately
/// make the database unreachable to prove readiness fails closed; sharing a fixture would make
/// the healthy-path tests depend on test execution order.
///
/// The database is a real file under a temporary directory, not an open in-memory connection: an
/// already-open <c>:memory:</c> <see cref="SqliteConnection"/> silently reopens an empty database
/// when closed or disposed, so <c>CanConnectAsync</c> would still report healthy. Deleting the
/// containing directory after startup has migrated the schema is what reliably makes the
/// provider's own connection attempt fail.
/// </summary>
public sealed class HealthEndpointsDatabaseUnavailableTests : IClassFixture<HealthEndpointsDatabaseUnavailableTests.ApiFactory>
{
    private readonly ApiFactory _factory;

    public HealthEndpointsDatabaseUnavailableTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Readiness_endpoint_reports_unhealthy_when_the_database_is_unavailable()
    {
        var client = _factory.CreateClient();
        _factory.MakeDatabaseUnreachable();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Liveness_endpoint_stays_healthy_during_a_database_outage()
    {
        var client = _factory.CreateClient();
        _factory.MakeDatabaseUnreachable();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_directory);
            var connectionString = "Data Source=" + Path.Combine(_directory, "health-check-tests.db");

            builder.ConfigureServices(services =>
            {
                var dbContextOptions = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (dbContextOptions is not null)
                {
                    services.Remove(dbContextOptions);
                }

                services.AddDbContext<AppDbContext>(options =>
                {
                    options.UseLazyLoadingProxies();
                    options.UseSqlite(connectionString);
                });
            });
        }

        /// <summary>
        /// Clears pooled connections first so SQLite has no cached handle into the directory
        /// that is about to disappear, then deletes it so every subsequent connection attempt
        /// fails at the provider level.
        /// </summary>
        public void MakeDatabaseUnreachable()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }
            }
        }
    }
}
