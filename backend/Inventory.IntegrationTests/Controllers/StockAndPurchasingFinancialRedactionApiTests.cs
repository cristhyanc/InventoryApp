using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The stock-movement and purchasing endpoints' role-dependent inventory value (issue #700),
/// exercised through the real request pipeline: authentication, the required scope, the
/// <c>Inventory.Operate</c>/<c>Purchasing.Operate</c> capability policies,
/// <c>BusinessScopeMiddleware</c>, the controllers and relational SQLite with the real migration
/// history.
///
/// <para>These are the endpoints the audit of issue #700 found still carrying the figure #527
/// withholds from the Dashboard, #681 refuses on its own endpoint and #698 redacted on the machine
/// and product responses. Two shapes carried it:</para>
/// <list type="bullet">
///   <item>a <b>stock movement</b>'s <c>inventoryValueAfter</c> - the business-owned inventory value
///   at cost immediately after that movement, which on the latest movement <em>is</em> the current
///   valuation. It reaches a caller four ways: nested in a product's <c>stockAdjustments</c>, on
///   <c>GET</c> and <c>POST api/products/{id}/stock</c>, and on the global
///   <c>GET api/stock-history</c>.</item>
///   <item>the <b>catalogue snapshot</b> a purchase line and a supplier-order line carry, whose
///   <c>inventoryValue</c> is the same per-product figure #698 withheld from <c>api/products</c> -
///   left reachable by listing purchases or outstanding orders instead.</item>
/// </list>
///
/// <para>What this has to prove is a value, not just a property set: every restricted field is JSON
/// <c>null</c> for an Operator - never <c>0</c>, which would read as a real "nothing on hand"
/// figure - while the movement's own unit cost, total cost and resulting average unit cost, the
/// purchase's own line costs and totals, and every quantity stay exactly what a Manager is given.
/// The recorded decision (Cristhyan, 15 October 2026) keeps all of those visible: an Operator
/// records the purchase and the restock they describe.</para>
///
/// <para>Three members of one business hold the three roles against one seeded catalogue, movement
/// history, purchase and supplier order, so every difference between their responses is the role
/// and nothing else.</para>
/// </summary>
public sealed class StockAndPurchasingFinancialRedactionApiTests
    : IClassFixture<StockAndPurchasingFinancialRedactionApiTests.ApiFactory>
{
    private static readonly string ProductUri =
        $"/api/products/{ApiFactory.ProductId.ToString(CultureInfo.InvariantCulture)}";

    private static readonly string ProductStockUri = $"{ProductUri}/stock";

    private static readonly string AdjustableProductStockUri =
        $"/api/products/{ApiFactory.AdjustableProductId.ToString(CultureInfo.InvariantCulture)}/stock";

    private const string StockHistoryUri = "/api/stock-history";
    private const string PurchasesUri = "/api/purchases";
    private const string SupplierOrdersUri = "/api/supplierorders";

    private static readonly string PurchaseUri =
        $"{PurchasesUri}/{ApiFactory.PurchaseId.ToString(CultureInfo.InvariantCulture)}";

    private static readonly string SupplierOrderUri =
        $"{SupplierOrdersUri}/{ApiFactory.SupplierOrderId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The one restricted field on a stock movement. The other three money fields are deliberately
    /// absent from this list: <c>unitCost</c> and <c>totalCost</c> are the cost of the movement
    /// itself, which an Operator enters when they record a restock, and
    /// <c>averageUnitCostAfter</c> is a unit cost. The recorded decision keeps all three.
    /// </summary>
    private static readonly string[] RestrictedMovementFields = ["inventoryValueAfter"];

    /// <summary>The one restricted field on the catalogue snapshot a line carries (issue #698).</summary>
    private static readonly string[] RestrictedProductFields = ["inventoryValue"];

    private readonly ApiFactory _factory;

    public StockAndPurchasingFinancialRedactionApiTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task An_operator_reading_a_product_is_given_no_inventory_value_on_its_movements()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.OperatorObjectId).GetAsync(ProductUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var product = await ReadJsonAsync(response);
        AssertNull(product, RestrictedProductFields);

        var movement = Single(product.GetProperty("stockAdjustments"));
        AssertNull(movement, RestrictedMovementFields);
        AssertMovementCostsVisible(movement);
    }

    [Fact]
    public async Task An_operator_reading_a_products_stock_history_is_given_no_inventory_value()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.OperatorObjectId).GetAsync(ProductStockUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var movement = Single(await ReadJsonAsync(response));
        AssertNull(movement, RestrictedMovementFields);
        AssertMovementCostsVisible(movement);
    }

    [Fact]
    public async Task An_operator_reading_the_global_stock_history_is_given_no_inventory_value()
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(ApiFactory.OperatorObjectId).GetAsync(StockHistoryUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var movement = SeededMovement(await ReadJsonAsync(response));
        AssertNull(movement, RestrictedMovementFields);
        AssertMovementCostsVisible(movement);

        // The page the endpoint exists to serve is intact: the product is still named on the row,
        // which is the whole reason this response is not the per-product shape.
        Assert.Equal(ApiFactory.ProductName, movement.GetProperty("productName").GetString());
    }

    /// <summary>
    /// Withheld, not zeroed, and withheld on the write too: recording a restock answers the
    /// movement that was created, and that movement's resulting inventory value is the business's
    /// current valuation. The Operator's own restock quantity comes back, so the action is
    /// unimpaired - only the figure is gone.
    /// </summary>
    [Fact]
    public async Task An_operator_recording_a_movement_is_given_no_inventory_value_back()
    {
        await _factory.SeedAsync();

        var response = await _factory
            .Authenticated(ApiFactory.OperatorObjectId)
            .PostAsJsonAsync(AdjustableProductStockUri, Restock(3));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var movement = await ReadJsonAsync(response);
        AssertNull(movement, RestrictedMovementFields);
        Assert.Equal(3, movement.GetProperty("quantityChange").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, movement.GetProperty("averageUnitCostAfter").ValueKind);
    }

    [Fact]
    public async Task A_manager_recording_a_movement_is_given_the_inventory_value_back()
    {
        await _factory.SeedAsync();

        var response = await _factory
            .Authenticated(ApiFactory.ManagerObjectId)
            .PostAsJsonAsync(AdjustableProductStockUri, Restock(5));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var movement = await ReadJsonAsync(response);
        Assert.NotEqual(JsonValueKind.Null, movement.GetProperty("inventoryValueAfter").ValueKind);
        Assert.True(movement.GetProperty("inventoryValueAfter").GetDecimal() > 0m);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_operator_reading_a_purchase_is_given_no_inventory_value_on_its_lines(bool list)
    {
        await _factory.SeedAsync();

        var response = await _factory
            .Authenticated(ApiFactory.OperatorObjectId)
            .GetAsync(list ? PurchasesUri : PurchaseUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);
        var item = Single(Envelope(json, list).GetProperty("purchase").GetProperty("items"));
        AssertNull(item.GetProperty("product"), RestrictedProductFields);

        // The purchase's own purchasing figures are not withheld: an Operator entered them.
        Assert.Equal(ApiFactory.PurchaseUnitCost, item.GetProperty("unitCost").GetDecimal());
        Assert.Equal(ApiFactory.PurchaseQuantity, item.GetProperty("quantity").GetDecimal());
        Assert.Equal(
            ApiFactory.PurchaseUnitCost * ApiFactory.PurchaseQuantity,
            item.GetProperty("lineTotal").GetDecimal());
        Assert.Equal(
            ApiFactory.PurchaseTotal,
            Envelope(json, list).GetProperty("purchase").GetProperty("totalAmount").GetDecimal());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_operator_reading_a_supplier_order_is_given_no_inventory_value_on_its_lines(bool list)
    {
        await _factory.SeedAsync();

        var response = await _factory
            .Authenticated(ApiFactory.OperatorObjectId)
            .GetAsync(list ? SupplierOrdersUri : SupplierOrderUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);
        var line = Single((list ? Single(json) : json).GetProperty("lines"));
        AssertNull(line.GetProperty("product"), RestrictedProductFields);

        // What is ordered, at what price, and how much of it is still outstanding: purchasing data.
        Assert.Equal(ApiFactory.OrderedQuantity, line.GetProperty("quantityOrdered").GetDecimal());
        Assert.Equal(ApiFactory.OrderUnitPrice, line.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(ApiFactory.OrderedQuantity, line.GetProperty("outstandingQuantity").GetDecimal());
    }

    /// <summary>
    /// The figure is real for a role that may see it, on every one of the five responses - which is
    /// what makes an Operator's <c>null</c> a withheld figure rather than an empty one.
    /// </summary>
    [Theory]
    [InlineData(ApiFactory.ManagerObjectId)]
    [InlineData(ApiFactory.OwnerObjectId)]
    public async Task A_role_that_holds_the_financial_capability_is_given_every_inventory_value(string objectId)
    {
        await _factory.SeedAsync();
        var client = _factory.Authenticated(objectId);

        var product = await ReadJsonAsync(await client.GetAsync(ProductUri));
        Assert.Equal(ApiFactory.InventoryValue, product.GetProperty("inventoryValue").GetDecimal());
        Assert.Equal(
            ApiFactory.InventoryValueAfter,
            Single(product.GetProperty("stockAdjustments")).GetProperty("inventoryValueAfter").GetDecimal());

        Assert.Equal(
            ApiFactory.InventoryValueAfter,
            Single(await ReadJsonAsync(await client.GetAsync(ProductStockUri)))
                .GetProperty("inventoryValueAfter").GetDecimal());

        Assert.Equal(
            ApiFactory.InventoryValueAfter,
            SeededMovement(await ReadJsonAsync(await client.GetAsync(StockHistoryUri)))
                .GetProperty("inventoryValueAfter").GetDecimal());

        Assert.Equal(
            ApiFactory.InventoryValue,
            Single((await ReadJsonAsync(await client.GetAsync(PurchaseUri)))
                    .GetProperty("purchase").GetProperty("items"))
                .GetProperty("product").GetProperty("inventoryValue").GetDecimal());

        Assert.Equal(
            ApiFactory.InventoryValue,
            Single((await ReadJsonAsync(await client.GetAsync(SupplierOrderUri))).GetProperty("lines"))
                .GetProperty("product").GetProperty("inventoryValue").GetDecimal());
    }

    /// <summary>
    /// Redaction withholds the one figure and changes nothing else: every other key of every one of
    /// the four read responses is byte-identical to the one a Manager is given over the same seeded
    /// state, at every nesting level.
    /// </summary>
    [Theory]
    [InlineData(nameof(ProductUri))]
    [InlineData(nameof(ProductStockUri))]
    [InlineData(nameof(StockHistoryUri))]
    [InlineData(nameof(PurchasesUri))]
    [InlineData(nameof(SupplierOrdersUri))]
    public async Task An_operator_is_given_the_same_non_financial_fields_as_a_manager(string surface)
    {
        await _factory.SeedAsync();
        var uri = surface switch
        {
            nameof(ProductUri) => ProductUri,
            nameof(ProductStockUri) => ProductStockUri,
            nameof(StockHistoryUri) => StockHistoryUri,
            nameof(PurchasesUri) => PurchasesUri,
            _ => SupplierOrdersUri,
        };

        var asOperator = await (await _factory.Authenticated(ApiFactory.OperatorObjectId).GetAsync(uri))
            .Content.ReadAsStringAsync();
        var asManager = await (await _factory.Authenticated(ApiFactory.ManagerObjectId).GetAsync(uri))
            .Content.ReadAsStringAsync();

        Assert.NotEqual(asManager, asOperator);
        Assert.Equal(Withhold(asManager), asOperator);
    }

    /// <summary>
    /// Not renamed, not reported elsewhere in the payload: neither seeded value appears anywhere in
    /// an Operator's body on any of the five responses, under any name.
    /// </summary>
    [Fact]
    public async Task An_operators_bodies_carry_no_inventory_value_at_all()
    {
        await _factory.SeedAsync();
        var client = _factory.Authenticated(ApiFactory.OperatorObjectId);

        foreach (var uri in new[]
        {
            ProductUri, ProductStockUri, StockHistoryUri, PurchasesUri, PurchaseUri,
            SupplierOrdersUri, SupplierOrderUri,
        })
        {
            var body = await (await client.GetAsync(uri)).Content.ReadAsStringAsync();

            Assert.DoesNotContain(Literal(ApiFactory.InventoryValue), body, StringComparison.Ordinal);
            Assert.DoesNotContain(Literal(ApiFactory.InventoryValueAfter), body, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The movement's own costs are not the business's valuation and stay visible: the cost of the
    /// restock, its extended total, and the average unit cost it left behind.
    /// </summary>
    private static void AssertMovementCostsVisible(JsonElement movement)
    {
        Assert.Equal(ApiFactory.MovementUnitCost, movement.GetProperty("unitCost").GetDecimal());
        Assert.Equal(ApiFactory.MovementTotalCost, movement.GetProperty("totalCost").GetDecimal());
        Assert.Equal(ApiFactory.AverageUnitCostAfter, movement.GetProperty("averageUnitCostAfter").GetDecimal());
        Assert.Equal(ApiFactory.CostingQuantityAfter, movement.GetProperty("costingQuantityAfter").GetInt32());
    }

    private static void AssertNull(JsonElement element, IEnumerable<string> fields)
    {
        foreach (var field in fields)
        {
            Assert.Equal(JsonValueKind.Null, element.GetProperty(field).ValueKind);
        }
    }

    /// <summary>
    /// A Manager's body with every restricted figure replaced by <c>null</c>. Comparing the two raw
    /// bodies this way asserts the whole payload at once - key names, ordering and every nested
    /// value - rather than the properties a test remembered to name.
    /// </summary>
    private static string Withhold(string body) => body
        .Replace(
            $"\"inventoryValue\":{Literal(ApiFactory.InventoryValue)}",
            "\"inventoryValue\":null",
            StringComparison.Ordinal)
        .Replace(
            $"\"inventoryValueAfter\":{Literal(ApiFactory.InventoryValueAfter)}",
            "\"inventoryValueAfter\":null",
            StringComparison.Ordinal);

    /// <summary>
    /// The seeded purchase, whether the response is the list or the single-purchase envelope. The
    /// list carries one element, because one purchase is seeded.
    /// </summary>
    private static JsonElement Envelope(JsonElement json, bool list) => list ? Single(json) : json;

    /// <summary>
    /// The seeded movement on the global history page, found by its id rather than by position: the
    /// write tests in this class append movements of their own, and the page is ordered by instant.
    /// </summary>
    private static JsonElement SeededMovement(JsonElement page) =>
        Assert.Single(
            page.GetProperty("items").EnumerateArray()
                .Where(item => item.GetProperty("id").GetInt32() == ApiFactory.StockAdjustmentId));

    private static JsonElement Single(JsonElement array) => Assert.Single(array.EnumerateArray());

    private static object Restock(int quantityChange) => new
    {
        quantityChange,
        reason = (int)StockAdjustmentReason.Restock,
        notes = "Issue #700 redaction test",
        unitCost = ApiFactory.MovementUnitCost,
    };

    private static string Literal(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string DirectoryTenantId = "77777777-7777-7777-7777-777777777777";
        public const string OwnerObjectId = "11111111-1111-1111-1111-111111111111";
        public const string ManagerObjectId = "22222222-2222-2222-2222-222222222222";
        public const string OperatorObjectId = "33333333-3333-3333-3333-333333333333";

        /// <summary>The product every read assertion is made over. Nothing writes to it.</summary>
        public const long ProductId = 200;

        public const string ProductName = "Smiths Chips";

        /// <summary>
        /// A second product, so the two write tests can record a movement without moving the
        /// figures the read tests assert on <see cref="ProductId"/>.
        /// </summary>
        public const long AdjustableProductId = 201;

        public const int PurchaseId = 300;
        public const int SupplierOrderId = 400;
        public const int StockAdjustmentId = 500;

        /// <summary>The withheld figures. Both are distinctive, so the whole-body scan is meaningful.</summary>
        public const decimal InventoryValue = 643.21m;

        public const decimal InventoryValueAfter = 591.37m;

        /// <summary>The figures the recorded decision keeps visible.</summary>
        public const decimal AverageUnitCost = 1.47m;

        public const decimal UnitPrice = 3.95m;
        public const decimal MovementUnitCost = 1.52m;
        public const decimal MovementTotalCost = 18.24m;
        public const decimal AverageUnitCostAfter = 1.49m;
        public const int CostingQuantityAfter = 397;
        public const decimal PurchaseUnitCost = 1.52m;
        public const decimal PurchaseQuantity = 12m;
        public const decimal PurchaseTotal = 18.24m;
        public const decimal OrderedQuantity = 24m;
        public const decimal OrderUnitPrice = 1.55m;

        private const int BusinessId = 1;

        private static readonly DateTime CreatedAt = new(2026, 3, 9, 4, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private bool _seeded;

        public ApiFactory() => _connection.Open();

        /// <summary>An unrestricted context for arranging state outside the request boundary.</summary>
        public AppDbContext Database()
        {
            // Resolving Services builds and starts the host, which is what applies the migrations
            // this context then reads and writes through.
            _ = Services;
            return TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        }

        public HttpClient Authenticated(string objectId)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.DirectoryTenantIdHeader, DirectoryTenantId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ObjectIdHeader, objectId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ScopeHeader, "access_as_user");
            return client;
        }

        public async Task SeedAsync()
        {
            await using var db = Database();
            if (_seeded)
            {
                return;
            }

            db.Businesses.Add(new Business
            {
                Id = BusinessId,
                Name = "Vending Co",
                TimeZoneId = "Australia/Sydney",
                Status = BusinessStatus.Active,
                CreatedAtUtc = CreatedAt,
            });

            db.BusinessMemberships.AddRange(
                Membership(OwnerObjectId, BusinessRole.Owner),
                Membership(ManagerObjectId, BusinessRole.Manager),
                Membership(OperatorObjectId, BusinessRole.Operator));

            db.Products.AddRange(CatalogueProduct(ProductId, ProductName), CatalogueProduct(AdjustableProductId, "Mars Bar"));

            db.Suppliers.Add(new Supplier { Id = 1, BusinessId = BusinessId, Name = "Metro Wholesale" });

            // One movement in the read product's history, carrying both its own costs and the
            // resulting business valuation.
            db.StockAdjustments.Add(new StockAdjustment
            {
                Id = StockAdjustmentId,
                BusinessId = BusinessId,
                ProductId = ProductId,
                QuantityChange = 12,
                QuantityAfter = 30,
                UnitCost = MovementUnitCost,
                TotalCost = MovementTotalCost,
                CostingQuantityAfter = CostingQuantityAfter,
                AverageUnitCostAfter = AverageUnitCostAfter,
                InventoryValueAfter = InventoryValueAfter,
                Reason = StockAdjustmentReason.Restock,
                Source = StockAdjustmentSource.Manual,
                CreatedAt = CreatedAt,
                EffectiveAt = CreatedAt,
            });

            db.Receipts.Add(new Purchase
            {
                Id = PurchaseId,
                BusinessId = BusinessId,
                Title = "Metro invoice 8821",
                TotalAmount = PurchaseTotal,
                PurchaseDate = CreatedAt,
                SupplierId = 1,
                FileName = "invoice-8821.pdf",
                StoredFileName = "8821.pdf",
                ContentType = "application/pdf",
                FileSizeBytes = 4096,
                CreatedAt = CreatedAt,
                Items =
                [
                    new PurchaseItem
                    {
                        Id = 1,
                        BusinessId = BusinessId,
                        ReceiptId = PurchaseId,
                        ProductId = ProductId,
                        Quantity = PurchaseQuantity,
                        UnitCost = PurchaseUnitCost,
                    },
                ],
            });

            db.SupplierOrders.Add(new SupplierOrder
            {
                Id = SupplierOrderId,
                BusinessId = BusinessId,
                SupplierId = 1,
                OrderDate = CreatedAt,
                Status = SupplierOrderStatus.Ordered,
                CreatedAt = CreatedAt,
                UpdatedAt = CreatedAt,
                Lines =
                [
                    new SupplierOrderLine
                    {
                        Id = 1,
                        BusinessId = BusinessId,
                        SupplierOrderId = SupplierOrderId,
                        ProductId = ProductId,
                        QuantityOrdered = OrderedQuantity,
                        QuantityReceived = 0m,
                        UnitPrice = OrderUnitPrice,
                    },
                ],
            });

            await db.SaveChangesAsync();
            _seeded = true;
        }

        private static Product CatalogueProduct(long id, string name) => new()
        {
            Id = id,
            BusinessId = BusinessId,
            Name = name,
            UnitPrice = UnitPrice,
            AverageUnitCost = AverageUnitCost,
            CostingQuantity = 400,
            InventoryValue = InventoryValue,
            QuantityInStock = 30,
            LowStockThreshold = 6,
            RestockTo = 48,
            IsActive = true,
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt,
        };

        private static BusinessMembership Membership(string objectId, BusinessRole role) =>
            new()
            {
                BusinessId = BusinessId,
                DirectoryTenantId = DirectoryTenantId,
                ObjectId = objectId,
                Role = role,
                IsActive = true,
                CreatedAtUtc = CreatedAt,
                StatusChangedAtUtc = CreatedAt,
            };

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Testing applies pending migrations at startup, so the schema under these tests is
            // the migration history rather than a model snapshot.
            builder.UseEnvironment("Testing");

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

            builder.ConfigureTestServices(services =>
                services.AddAuthentication(TestActorAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestActorAuthenticationHandler>(
                        TestActorAuthenticationHandler.SchemeName, _ => { }));
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

    /// <summary>
    /// Stands in for Entra: the caller's <c>(tid, oid)</c> pair and delegated scope come from
    /// request headers, so membership resolution, the scope requirement and the capability policy
    /// are exercised exactly as they are in production without a real token.
    /// </summary>
    private sealed class TestActorAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestActor";
        public const string DirectoryTenantIdHeader = "X-Test-Directory-Tenant-Id";
        public const string ObjectIdHeader = "X-Test-Object-Id";
        public const string ScopeHeader = "X-Test-Scope";

        public TestActorAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(ObjectIdHeader, out var objectId))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new("tid", Request.Headers[DirectoryTenantIdHeader].ToString()),
                new("oid", objectId.ToString()),
            };
            if (Request.Headers.TryGetValue(ScopeHeader, out var scope))
            {
                claims.Add(new Claim("scp", scope.ToString()));
            }

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
        }
    }
}
