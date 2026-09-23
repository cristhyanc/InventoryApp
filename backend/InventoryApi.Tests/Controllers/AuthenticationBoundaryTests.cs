using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using InventoryApi.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// Proves the HTTP authentication/authorization boundary added for issue #38: a request
/// carrying no bearer token must be rejected by ASP.NET Core's authentication middleware
/// (401) before it ever reaches a controller action, for representative protected endpoints
/// across different controllers.
/// </summary>
public sealed class AuthenticationBoundaryTests : IClassFixture<AuthenticationBoundaryTests.ApiFactory>
{
    private readonly ApiFactory _factory;

    public AuthenticationBoundaryTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/api/products")]
    [InlineData("/api/categories")]
    public async Task Unauthenticated_request_to_protected_endpoint_returns_401(string requestUri)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(requestUri);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
