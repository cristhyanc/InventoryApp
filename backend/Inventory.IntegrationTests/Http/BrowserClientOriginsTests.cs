using System.Net;
using Inventory.Infrastructure.Data;
using InventoryApi.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Http;

/// <summary>
/// The CORS origins (hosted testing environment): the local dev server and the production frontend
/// are always allowed, a deployment may add https origins by configuration only, and an invalid entry
/// fails instead of being ignored.
/// </summary>
public sealed class BrowserClientOriginsTests
{
    private static IConfiguration Configuration(params string[] additionalOrigins)
    {
        var values = additionalOrigins
            .Select((origin, index) => new KeyValuePair<string, string?>($"{BrowserClientOrigins.AdditionalOriginsSection}:{index}", origin));
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void Without_configuration_only_the_local_and_production_origins_are_allowed()
    {
        var origins = BrowserClientOrigins.Resolve(Configuration());

        Assert.Equal(
            ["http://localhost:4200", "https://red-island-0c128c000.7.azurestaticapps.net"],
            origins);
    }

    [Fact]
    public void Configured_https_origins_are_added_after_the_fixed_ones_normalised_and_without_duplicates()
    {
        var origins = BrowserClientOrigins.Resolve(Configuration(
            " https://Test-Frontend.example/ ",
            "https://test-frontend.example",
            "https://other.example:8443",
            "",
            "https://red-island-0c128c000.7.azurestaticapps.net"));

        Assert.Equal(
            [
                "http://localhost:4200",
                "https://red-island-0c128c000.7.azurestaticapps.net",
                "https://test-frontend.example",
                "https://other.example:8443",
            ],
            origins);
    }

    [Theory]
    [InlineData("http://test-frontend.example")]
    [InlineData("https://test-frontend.example/auth")]
    [InlineData("https://test-frontend.example?x=1")]
    [InlineData("https://test-frontend.example#x")]
    [InlineData("https://user@test-frontend.example")]
    [InlineData("https://*.azurestaticapps.net")]
    [InlineData("*")]
    [InlineData("test-frontend.example")]
    public void An_origin_that_is_not_a_bare_https_origin_fails_instead_of_being_ignored(string origin)
    {
        var error = Assert.Throws<InvalidOperationException>(() => BrowserClientOrigins.Resolve(Configuration(origin)));

        Assert.Contains(BrowserClientOrigins.AdditionalOriginsSection, error.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// The configured origin reaches the API's real CORS policy: a preflight from it is allowed, and one
/// from an unconfigured origin is not.
/// </summary>
public sealed class BrowserClientOriginsPreflightTests : IClassFixture<BrowserClientOriginsPreflightTests.ApiFactory>
{
    private const string TestingFrontend = "https://test-frontend.example";

    private readonly ApiFactory _factory;

    public BrowserClientOriginsPreflightTests(ApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpResponseMessage> PreflightAsync(string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/products");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        return await _factory.CreateClient().SendAsync(request);
    }

    [Theory]
    [InlineData(TestingFrontend)]
    [InlineData("https://red-island-0c128c000.7.azurestaticapps.net")]
    public async Task A_preflight_from_an_allowed_origin_is_answered_with_that_origin(string origin)
    {
        using var response = await PreflightAsync(origin);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed));
        Assert.Equal(origin, Assert.Single(allowed));
    }

    [Fact]
    public async Task A_preflight_from_an_unconfigured_origin_is_not_allowed()
    {
        using var response = await PreflightAsync("https://unconfigured.example");

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection.Open();
            builder.UseSetting($"{BrowserClientOrigins.AdditionalOriginsSection}:0", TestingFrontend);

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
