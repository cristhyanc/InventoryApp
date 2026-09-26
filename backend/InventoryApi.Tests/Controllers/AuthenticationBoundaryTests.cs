using System.Net;
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
    [InlineData("/api/purchases/1/file")]
    [InlineData("/api/operating-expenses/1/attachment")]
    public async Task Unauthenticated_request_to_protected_endpoint_returns_401(string requestUri)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(requestUri);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Uploaded business documents must have no anonymous URL at all. The API serves no
    /// static files, so even a document sitting in the web root — where receipts and
    /// operating-expense attachments were written before they moved to protected storage —
    /// is not reachable without going through the <c>[Authorize]</c>d API endpoints.
    /// </summary>
    [Theory]
    [InlineData("receipts")]
    [InlineData("expenses")]
    public async Task Anonymous_static_url_does_not_serve_an_uploaded_document(string folder)
    {
        var client = _factory.CreateClient();
        var environment = _factory.Services.GetRequiredService<IWebHostEnvironment>();
        Assert.Equal(_factory.WebRoot, environment.WebRootPath);
        var storedFileName = $"{Guid.NewGuid()}.pdf";
        var directory = Directory.CreateDirectory(Path.Combine(_factory.WebRoot, folder));
        await File.WriteAllBytesAsync(Path.Combine(directory.FullName, storedFileName), new byte[] { 1, 2, 3 });

        var response = await client.GetAsync($"/{folder}/{storedFileName}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        public string WebRoot { get; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection.Open();
            Directory.CreateDirectory(WebRoot);
            builder.UseWebRoot(WebRoot);

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
                if (Directory.Exists(WebRoot)) Directory.Delete(WebRoot, recursive: true);
            }
        }
    }
}
