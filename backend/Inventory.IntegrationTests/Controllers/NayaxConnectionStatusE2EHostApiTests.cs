using System.Net;
using System.Text.Json;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Nayax;
using InventoryApi.Auth.E2ETesting;
using InventoryApi.Tests.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// <c>GET api/settings/nayax-connection/status</c>, hosted the way the dedicated end-to-end testing
/// host hosts it (issue #1276, audit #1225 finding FE-05).
///
/// Every other environment registers <c>INayaxConnectionCheckRunner</c> beside the real Nayax HTTP
/// client (<c>AddNayaxLynxClient</c>); the E2E host registers neither, because it must never be able
/// to reach a live operator account. Before this issue that left
/// <see cref="InventoryApi.Controllers.NayaxConnectionController"/> unable to resolve at all - its one
/// constructor binds all four actions, and two of them need that port - so every action, including
/// this read-only one, answered <c>500</c> with an <see cref="InvalidOperationException"/> naming the
/// missing service. A seeded E2E business stores no Nayax credentials, so the answer this proves is
/// the same one production gives such a business: <c>NotConfigured</c>, with no machine checks
/// pending, read with no outbound call at all.
/// </summary>
public sealed class NayaxConnectionStatusE2EHostApiTests : IClassFixture<NayaxConnectionStatusE2EHostApiTests.ApiFactory>
{
    private const string StatusUri = "/api/settings/nayax-connection/status";
    private const string DashboardSummaryUri = "/api/dashboard/summary";

    private readonly ApiFactory _factory;

    public NayaxConnectionStatusE2EHostApiTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_business_with_no_stored_connection_reads_NotConfigured_with_no_machine_checks_pending()
    {
        var client = _factory.As(E2ETestActors.BusinessAOwner.Key);

        var response = await client.GetAsync(StatusUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("NotConfigured", body.RootElement.GetProperty("status").GetString());
        Assert.False(body.RootElement.GetProperty("machineChecksPending").GetBoolean());
    }

    /// <summary>
    /// The Dashboard takes its provider answer from its own summary, not from the status read: the
    /// live-fleet "Needs refill" section is withheld with <c>NayaxNotConnected</c> when the fleet
    /// read is refused. In this host that read is <c>E2ETestNayaxLynxClient</c>, which now asks the
    /// production credential provider first, so a business with no stored connection gets the same
    /// withheld section production gives it - not a machine-aware summary over an empty fleet.
    /// </summary>
    [Fact]
    public async Task The_Dashboard_summary_withholds_the_live_fleet_section_as_not_connected()
    {
        var client = _factory.As(E2ETestActors.BusinessAOwner.Key);

        var response = await client.GetAsync(DashboardSummaryUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var needsRefill = body.RootElement.GetProperty("needsRefill");
        Assert.False(needsRefill.GetProperty("available").GetBoolean());
        Assert.Equal("NayaxNotConnected", needsRefill.GetProperty("reason").GetString());
    }

    /// <summary>
    /// The other half of the acceptance criteria: this host resolves the port with no real Nayax
    /// HTTP client behind it anywhere. <see cref="NayaxLynxClient"/> and
    /// <see cref="NayaxConnectionCheckRunner"/> are the two concrete HTTP-backed types
    /// <c>AddNayaxLynxClient</c> would register; neither is registered in this host at all, so there
    /// is nothing configured here that could reach a live Nayax address.
    /// </summary>
    [Fact]
    public void The_host_registers_no_real_Nayax_HTTP_client()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<NayaxLynxClient>());
        Assert.Null(scope.ServiceProvider.GetService<NayaxConnectionCheckRunner>());
    }

    /// <summary>
    /// Hosts the real <c>Program.cs</c> composition as the dedicated E2E environment, exactly as the
    /// browser-level suite is hosted, against a disposable SQLite database carrying the real
    /// migration history and the real fixture seed.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        public ApiFactory()
        {
            E2ETestHostContentRoot.Pin();
            _connection.Open();
        }

        public HttpClient As(string actorKey)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation(E2ETestActors.ActorHeaderName, actorKey);
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(E2ETestEnvironment.EnvironmentName);

            // The E2E environment is not one of the environments that migrate automatically, so this
            // disposable database opts in with the setting that exists for exactly that case.
            builder.UseSetting("Database:AllowAutomaticMigrationUnsafeOutsideDevelopment", "true");

            builder.ConfigureServices(services =>
            {
                var dbContextOptions = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
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
