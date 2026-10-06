using System.Net;
using Inventory.Infrastructure.Data;
using InventoryApi.Auth.E2ETesting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// The fail-closed half of issue #46, proven through the real HTTP pipeline rather than through
/// the composition root: in a host that is not the dedicated E2E environment, the synthetic actor
/// header is an unknown header with no effect, the synthetic scheme does not exist to be
/// selected, and the real Microsoft Entra bearer scheme is still the one answering.
///
/// Production is included deliberately. The cost of this scheme being reachable in production
/// would be total - a caller could name a synthetic identity and be believed - so "it is not
/// registered there" is asserted against a host started as Production, not inferred from the
/// composition code.
/// </summary>
public sealed class E2ETestAuthenticationFailsClosedTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    [InlineData("e2etest")]
    public async Task The_synthetic_actor_header_authenticates_nobody_outside_the_dedicated_environment(string environmentName)
    {
        using var factory = new HostFactory(environmentName);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            E2ETestActors.ActorHeaderName, E2ETestActors.BusinessAOwner.Key);

        var response = await client.GetAsync("/api/products");

        // 401, not 403: the request never authenticated at all, so it never reached the
        // business-scope middleware that a membership-less caller is refused by.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    [InlineData("e2etest")]
    public async Task The_synthetic_scheme_is_not_registered_outside_the_dedicated_environment(string environmentName)
    {
        using var factory = new HostFactory(environmentName);
        var schemes = factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.Null(await schemes.GetSchemeAsync(E2ETestAuthenticationHandler.SchemeName));
        Assert.Equal(
            JwtBearerDefaults.AuthenticationScheme,
            (await schemes.GetDefaultAuthenticateSchemeAsync())?.Name);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    [InlineData("e2etest")]
    public async Task No_end_to_end_fixture_data_is_seeded_outside_the_dedicated_environment(string environmentName)
    {
        using var factory = new HostFactory(environmentName);
        _ = factory.Services;

        await using var db = factory.UnrestrictedDatabase();

        Assert.Empty(await db.Businesses.AsNoTracking().ToListAsync());
        Assert.Empty(await db.BusinessMemberships.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Products.AsNoTracking().ToListAsync());
        Assert.Empty(await db.NayaxSales.AsNoTracking().ToListAsync());
    }

    private sealed class HostFactory : WebApplicationFactory<Program>
    {
        private readonly string _environmentName;
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        public HostFactory(string environmentName)
        {
            E2ETestHostContentRoot.Pin();
            _environmentName = environmentName;
            _connection.Open();
        }

        public AppDbContext UnrestrictedDatabase() =>
            TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(_environmentName);

            // Granted for every environment under test, so the only thing that differs between
            // them is the environment name itself.
            builder.UseSetting("Database:AllowAutomaticMigrationUnsafeOutsideDevelopment", "true");

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
