using System.Net;
using System.Text.Json;
using Inventory.Infrastructure.Data;
using InventoryApi.Auth.E2ETesting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// The synthetic end-to-end authentication scheme inside the dedicated E2E host (issue #46),
/// exercised through the real HTTP pipeline against a relational SQLite database.
///
/// What these tests are for is the claim that the scheme authenticates and nothing more. Every
/// assertion below is about a boundary that must still hold with it switched on:
/// an unauthenticated request is still 401; a synthetic actor with no business membership is
/// authenticated and then refused 403 by the business-scope middleware; an authorised actor sees
/// its own business's seeded data and no part of the other business's, by listing and by direct
/// id; and the fixture itself stamps ownership rather than leaving rows unowned.
/// </summary>
public sealed class E2ETestAuthenticationTests : IClassFixture<E2ETestAuthenticationTests.E2EHostFactory>
{
    private readonly E2EHostFactory _factory;

    public E2ETestAuthenticationTests(E2EHostFactory factory) => _factory = factory;

    [Fact]
    public async Task A_request_with_no_actor_header_is_still_unauthenticated()
    {
        var response = await _factory.CreateClient().GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("unknown-actor")]
    [InlineData("Business-A-Owner")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task An_actor_header_that_names_no_synthetic_actor_authenticates_nobody(string actorKey)
    {
        var response = await _factory.As(actorKey).GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Two actor headers are an ambiguity about who is calling, and the scheme refuses rather
    /// than letting header order decide which business's data is read.
    /// </summary>
    [Fact]
    public async Task A_repeated_actor_header_authenticates_nobody()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(E2ETestActors.ActorHeaderName, E2ETestActors.BusinessAOwner.Key);
        client.DefaultRequestHeaders.Add(E2ETestActors.ActorHeaderName, E2ETestActors.BusinessBOwner.Key);

        var response = await client.GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The synthetic scheme authenticates; it does not authorise. An actor the fixture gives no
    /// membership reaches the business-scope middleware and is refused there, exactly as a real
    /// signed-in account outside every business would be.
    /// </summary>
    [Fact]
    public async Task A_synthetic_actor_without_a_business_membership_is_forbidden_not_served()
    {
        var response = await _factory.As(E2ETestActors.NoMembership.Key).GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_authorised_synthetic_actor_sees_exactly_its_own_businesss_seeded_products()
    {
        var names = await ProductNamesAsync(E2ETestActors.BusinessAOwner.Key);

        Assert.Equal(
            [E2ETestFixture.CorrectionProductName, E2ETestFixture.PurchaseProductName, E2ETestFixture.ReorderProductName],
            names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_other_synthetic_actor_sees_only_the_other_business()
    {
        var names = await ProductNamesAsync(E2ETestActors.BusinessBOwner.Key);

        Assert.Equal([E2ETestFixture.BusinessBProductName], names);
    }

    /// <summary>
    /// Listing being filtered is not enough: a known id from the other business must be
    /// indistinguishable from an id that does not exist.
    /// </summary>
    [Fact]
    public async Task One_business_cannot_reach_the_other_businesss_product_by_id()
    {
        var businessAProductId = await ProductIdAsync(E2ETestActors.BusinessAOwner.Key, E2ETestFixture.ReorderProductName);
        var businessBProductId = await ProductIdAsync(E2ETestActors.BusinessBOwner.Key, E2ETestFixture.BusinessBProductName);

        var crossReads = await Task.WhenAll(
            _factory.As(E2ETestActors.BusinessBOwner.Key).GetAsync($"/api/products/{businessAProductId}"),
            _factory.As(E2ETestActors.BusinessAOwner.Key).GetAsync($"/api/products/{businessBProductId}"));

        Assert.Equal(
            [HttpStatusCode.NotFound, HttpStatusCode.NotFound],
            crossReads.Select(response => response.StatusCode));
    }

    /// <summary>
    /// The fixture's own data must be owned, not global: an unowned seeded row would be visible to
    /// every business and would make the isolation assertions above meaningless.
    /// </summary>
    [Fact]
    public async Task The_seeded_fixture_data_is_owned_by_the_two_synthetic_businesses()
    {
        _ = _factory.Services;
        await using var db = _factory.UnrestrictedDatabase();

        var businesses = await db.Businesses.AsNoTracking().OrderBy(business => business.Id).ToListAsync();
        var businessIds = businesses.Select(business => business.Id).ToArray();
        var products = await db.Products.AsNoTracking().ToListAsync();
        var memberships = await db.BusinessMemberships.AsNoTracking().ToListAsync();

        Assert.Equal([E2ETestFixture.BusinessAName, E2ETestFixture.BusinessBName], businesses.Select(x => x.Name));
        Assert.All(products, product => Assert.Contains(product.BusinessId, businessIds));
        Assert.All(
            await db.NayaxSales.AsNoTracking().ToListAsync(),
            sale => Assert.Contains(sale.BusinessId, businessIds));
        Assert.All(
            await db.StockAdjustments.AsNoTracking().ToListAsync(),
            movement => Assert.Contains(movement.BusinessId, businessIds));
        Assert.Equal(
            [E2ETestActors.BusinessAOwner.ObjectId, E2ETestActors.BusinessBOwner.ObjectId],
            memberships.OrderBy(membership => membership.BusinessId).Select(membership => membership.ObjectId));
        Assert.DoesNotContain(E2ETestActors.NoMembership.ObjectId, memberships.Select(membership => membership.ObjectId));
    }

    /// <summary>
    /// The seeded sale is the report state the suite exists to protect: a completed card sale with
    /// no cost of goods, so COGS and profit must be reported unavailable rather than zero. If this
    /// row were ever seeded with a cost, that browser test would silently stop proving anything.
    /// </summary>
    [Fact]
    public async Task The_seeded_sale_is_a_completed_card_sale_that_is_deliberately_uncosted()
    {
        _ = _factory.Services;
        await using var db = _factory.UnrestrictedDatabase();

        var sale = Assert.Single(await db.NayaxSales.AsNoTracking().ToListAsync());
        var productNames = await db.Products.AsNoTracking().Select(product => product.Name).ToListAsync();

        // Unmatchable by design: the cost rebuild matches a sale to a product by Nayax product id
        // or by name, so an id-less sale naming no catalogue product can never become costed, and
        // the report state the suite asserts cannot drift with test order.
        Assert.Null(sale.NayaxProductId);
        Assert.DoesNotContain(sale.ProductName, productNames);
        Assert.Equal(12, sale.TransactionStatusId);
        Assert.Equal("Credit Card", sale.PaymentMethod);
        Assert.Equal(E2ETestFixture.UncostedSaleAmount, sale.SettlementValue);
        Assert.Equal(E2ETestFixture.UncostedSaleAuthorizedAtUtc, sale.MachineAuthorizationTime);
        Assert.Null(sale.CostOfGoodsSold);
        Assert.Null(sale.UnitCostAtSale);
        Assert.Null(sale.NayaxProductCostPrice);
    }

    /// <summary>
    /// Restarting the E2E host against the same disposable database must not duplicate the
    /// fixture, because the harness may start the API more than once for one database file.
    /// </summary>
    [Fact]
    public async Task Seeding_again_adds_nothing()
    {
        _ = _factory.Services;

        await E2ETestFixture.SeedAsync(
            _factory.Services,
            _factory.Services.GetRequiredService<IWebHostEnvironment>(),
            _factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("test"),
            CancellationToken.None);

        await using var db = _factory.UnrestrictedDatabase();

        Assert.Equal(2, await db.Businesses.CountAsync());
        Assert.Equal(2, await db.BusinessMemberships.CountAsync());
        Assert.Equal(4, await db.Products.CountAsync());
        Assert.Equal(3, await db.StockAdjustments.CountAsync());
        Assert.Equal(1, await db.NayaxSales.CountAsync());
    }

    private async Task<string[]> ProductNamesAsync(string actorKey)
    {
        var response = await _factory.As(actorKey).GetAsync("/api/products");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.EnumerateArray()
            .Select(product => product.GetProperty("name").GetString()!)
            .ToArray();
    }

    private async Task<long> ProductIdAsync(string actorKey, string productName)
    {
        var response = await _factory.As(actorKey).GetAsync("/api/products");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.EnumerateArray()
            .Where(product => product.GetProperty("name").GetString() == productName)
            .Select(product => product.GetProperty("id").GetInt64())
            .Single();
    }

    /// <summary>
    /// The API hosted exactly as the end-to-end harness hosts it: the dedicated E2E environment
    /// name, and a disposable relational database the startup migration override is allowed to
    /// create the schema in.
    /// </summary>
    public sealed class E2EHostFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        public E2EHostFactory()
        {
            // Hosting must not depend on the process's current directory; see E2ETestHostContentRoot.
            E2ETestHostContentRoot.Pin();
            _connection.Open();
        }

        public HttpClient As(string actorKey)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation(E2ETestActors.ActorHeaderName, actorKey);
            return client;
        }

        /// <summary>Verifies both businesses' rows from outside the boundary, never over HTTP.</summary>
        public AppDbContext UnrestrictedDatabase() =>
            TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(E2ETestEnvironment.EnvironmentName);

            // The E2E environment is not one of the environments that migrate automatically, so
            // the harness opts its disposable database in with the setting that already exists
            // for exactly this case (see DatabaseSchemaStartup).
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
