using System.Net;
using Inventory.Infrastructure.Data;
using InventoryApi.Http.HealthChecks;
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

    [Fact]
    public async Task Liveness_endpoint_reports_the_build_commit()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        AssertBuildShaHeader(response);
    }

    [Fact]
    public async Task Readiness_endpoint_reports_the_build_commit_when_healthy()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        AssertBuildShaHeader(response);
    }

    /// <summary>
    /// Issue #1308: ASP.NET Core routing matches a route template case-insensitively and ignores a
    /// trailing slash, so it selects the very same liveness endpoint - with its normal status code
    /// and body - for every spelling below. The build commit therefore has to be bound to the
    /// endpoint registration rather than to one spelling of its path, or an operator polling
    /// <c>/HEALTH/LIVE</c> or <c>/health/live/</c> after a deploy gets a healthy reply carrying no
    /// SHA at all and cannot tell the new release from the old process finishing its restart.
    /// </summary>
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/live/")]
    [InlineData("/HEALTH/LIVE")]
    [InlineData("/Health/Live")]
    public async Task Liveness_endpoint_reports_the_build_commit_for_every_route_variant(string path)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertBuildShaHeader(response);
    }

    /// <summary>
    /// Issue #1308: the readiness endpoint, which the deploy workflow actually polls, for the same
    /// route variants and for the same reason as
    /// <see cref="Liveness_endpoint_reports_the_build_commit_for_every_route_variant"/>.
    /// </summary>
    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/health/ready/")]
    [InlineData("/HEALTH/READY")]
    [InlineData("/Health/Ready")]
    public async Task Readiness_endpoint_reports_the_build_commit_for_every_route_variant(string path)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertBuildShaHeader(response);
    }

    /// <summary>
    /// Issue #1308: nothing but the two health endpoints exposes the build commit - neither a path
    /// no endpoint matches (a 404 from routing) nor a real, unrelated API endpoint.
    /// </summary>
    [Theory]
    [InlineData("/health")]
    [InlineData("/health/livez")]
    [InlineData("/api/products")]
    public async Task Other_responses_do_not_carry_the_build_commit(string path)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.DoesNotContain(
            response.Headers,
            header => string.Equals(header.Key, "X-Build-Sha", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Issue #1308: the header must be a full 40-character hex commit SHA, or the literal
    /// "unknown" for a build with no git revision to stamp (for example a local build outside a
    /// git checkout) - never empty, and never any other build or environment detail.
    /// </summary>
    internal static void AssertBuildShaHeader(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("X-Build-Sha", out var values), "X-Build-Sha header is missing.");
        var value = Assert.Single(values);

        if (value == "unknown")
        {
            return;
        }

        Assert.Equal(40, value.Length);
        Assert.Matches("^[0-9a-f]{40}$", value);
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
    public async Task Readiness_endpoint_reports_the_build_commit_on_a_503()
    {
        var client = _factory.CreateClient();
        _factory.MakeDatabaseUnreachable();

        var response = await client.GetAsync("/health/ready");

        HealthEndpointsTests.AssertBuildShaHeader(response);
    }

    /// <summary>
    /// Issue #1308: the 503 readiness reply carries the build commit for every route variant too,
    /// so an operator polling a case variant during a failed deploy still learns which build
    /// refused, not merely that something did.
    /// </summary>
    [Theory]
    [InlineData("/health/ready/")]
    [InlineData("/HEALTH/READY")]
    public async Task Readiness_endpoint_reports_the_build_commit_on_a_503_for_every_route_variant(string path)
    {
        var client = _factory.CreateClient();
        _factory.MakeDatabaseUnreachable();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        HealthEndpointsTests.AssertBuildShaHeader(response);
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

/// <summary>
/// Issue #1308, acceptance criterion 3: "A build with no commit information (for example a local
/// build outside git) returns <c>unknown</c>, and the endpoints still work." This is the second
/// half of it - that the health endpoints keep answering normally while reporting
/// <c>unknown</c>; <see cref="BuildMetadataTests"/> is the first half, the parser itself.
///
/// A separate fixture because the whole point is a host built without the commit this repository's
/// own checkout always stamps: the assembly attribute of a loaded assembly cannot be changed, so
/// the fixture substitutes the registered <see cref="BuildShaSource"/> with exactly what
/// <see cref="BuildMetadata.ResolveBuildSha"/> produces for a build that carries no revision at
/// all. Everything else is the real application - the real routing, the real
/// <c>BuildShaHeaderMetadata</c> markers on the two registrations, the real middleware and the
/// real health checks - so what these tests assert is the response an operator would actually get
/// from such a build.
/// </summary>
public sealed class HealthEndpointsWithoutBuildMetadataTests : IClassFixture<HealthEndpointsWithoutBuildMetadataTests.ApiFactory>
{
    private readonly ApiFactory _factory;

    public HealthEndpointsWithoutBuildMetadataTests(ApiFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Both endpoints, and the route variants routing sends to the same two registrations, report
    /// the literal "unknown" - and still answer with their normal 200 and body, which is the
    /// "the endpoints still work" half of the criterion.
    /// </summary>
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/live/")]
    [InlineData("/HEALTH/LIVE")]
    [InlineData("/health/ready")]
    [InlineData("/health/ready/")]
    [InlineData("/HEALTH/READY")]
    public async Task Health_endpoints_report_unknown_and_still_answer_normally(string path)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());

        Assert.True(response.Headers.TryGetValues("X-Build-Sha", out var values), "X-Build-Sha header is missing.");
        Assert.Equal("unknown", Assert.Single(values));
    }

    /// <summary>
    /// The fallback does not leak the header onto anything else either: a path no endpoint matches
    /// and a real, unrelated API endpoint still carry no build commit.
    /// </summary>
    [Theory]
    [InlineData("/health")]
    [InlineData("/api/products")]
    public async Task Other_responses_still_do_not_carry_the_build_commit(string path)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.DoesNotContain(
            response.Headers,
            header => string.Equals(header.Key, "X-Build-Sha", StringComparison.OrdinalIgnoreCase));
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
                    options.UseSqlite(_connection);
                });

                var buildSha = services.SingleOrDefault(d => d.ServiceType == typeof(BuildShaSource));
                if (buildSha is not null)
                {
                    services.Remove(buildSha);
                }

                // What a build made outside a git checkout reports: no revision stamped into the
                // informational version at all, resolved by the same parser the application uses.
                services.AddSingleton(new BuildShaSource(BuildMetadata.ResolveBuildSha(informationalVersion: null)));
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
