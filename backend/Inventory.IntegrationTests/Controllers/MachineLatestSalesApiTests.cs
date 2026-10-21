using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Inventory.Application.Machines;
using Inventory.Domain.Nayax;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Nayax;
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
/// <c>GET api/machines/{id}/latest-sales</c> (issue #768) through the real request pipeline and the
/// real integration boundary: authentication, the required scope, the <c>Inventory.Operate</c>
/// capability policy, <c>BusinessScopeMiddleware</c>, the controller, the Application read, the real
/// <c>NayaxLynxClient</c> over the real <c>NayaxRequestCredentialProvider</c> and real AES-GCM token
/// encryption, and relational SQLite with the real migration history.
///
/// <para><strong>Only the socket is stubbed.</strong> A replaced primary HTTP handler answers with
/// the documented <c>lastSales</c> payload - including the masked card number, the CLI, the
/// payment-service transaction and provider ids and the card brand that the real response carries -
/// so "the API response is an allow-list of the displayed fields" is proved over real JSON
/// deserialization rather than over a hand-built object that never held those fields. It also
/// records every request, which is what lets these tests state that each business's read carried
/// <em>its own</em> operator id and token.</para>
///
/// <para>Three businesses: one with stored credentials and all three roles, a second with its own
/// different credentials and its own machine, and a third with no connection at all - which is
/// exactly what a business with no sales provider is (issue #759), since storing credentials is the
/// one thing that claims <c>Business.SalesProvider</c>.</para>
/// </summary>
public sealed class MachineLatestSalesApiTests : IClassFixture<MachineLatestSalesApiTests.ApiFactory>
{
    /// <summary>The whole published property set of one sale, in ordinal order.</summary>
    private static readonly string[] SaleProperties =
    [
        "currencyCode", "paymentMethod", "productName", "saleAmount", "saleDateTimeUtc", "transactionId",
    ];

    /// <summary>
    /// Everything the real <c>lastSales</c> payload carries that this response must never publish:
    /// the cardholder-identifying values, the payment-service identifiers, and the transaction-level
    /// cost price the shared DTO holds for costing.
    /// </summary>
    private static readonly string[] ForbiddenValues =
    [
        ApiFactory.CardNumber,
        ApiFactory.Cli,
        ApiFactory.PaymentServiceTransactionId,
        ApiFactory.PaymentServiceProviderName,
        ApiFactory.CardBrand,
        ApiFactory.ProductCostPrice,
    ];

    private static readonly string LatestSalesUri = Uri(ApiFactory.LocalMachineId);

    private readonly ApiFactory _factory;

    public MachineLatestSalesApiTests(ApiFactory factory) => _factory = factory;

    private static string Uri(long machineId) =>
        $"/api/machines/{machineId.ToString(CultureInfo.InvariantCulture)}/latest-sales";

    [Fact]
    public async Task A_role_that_may_see_the_figures_is_answered_the_latest_sales_newest_first()
    {
        await _factory.ResetAsync();

        var response = await _factory.Authenticated(ApiFactory.ManagerObjectId).GetAsync(LatestSalesUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.True(body.GetProperty("isAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("unavailableReason").ValueKind);
        Assert.Equal(ApiFactory.LocalMachineId, body.GetProperty("machineId").GetInt64());

        var sales = body.GetProperty("sales").EnumerateArray().ToList();
        Assert.Equal(3, sales.Count);

        // Newest first, and the sale whose payload item carried no usable GMT instant last - never
        // sorted to the top, and never given a defaulted instant to be sorted by.
        Assert.Equal([2L, 1L, 3L], sales.Select(sale => sale.GetProperty("transactionId").GetInt64()));
        Assert.Equal(
            new DateTime(2026, 10, 17, 23, 30, 0, DateTimeKind.Utc),
            sales[0].GetProperty("saleDateTimeUtc").GetDateTime());
        Assert.Equal(JsonValueKind.Null, sales[2].GetProperty("saleDateTimeUtc").ValueKind);

        Assert.Equal("Bottled Water", sales[0].GetProperty("productName").GetString());
        Assert.Equal("Credit Card", sales[0].GetProperty("paymentMethod").GetString());
        Assert.Equal(ApiFactory.NewestSettlementValue, sales[0].GetProperty("saleAmount").GetDecimal());
        Assert.Equal("AUD", sales[0].GetProperty("currencyCode").GetString());
    }

    /// <summary>
    /// A product name or payment method the provider did not report is <c>null</c>, not an empty
    /// string standing in for one, and the trailing whitespace the live endpoint sends on a product
    /// name does not reach the client.
    /// </summary>
    [Fact]
    public async Task A_value_the_provider_did_not_report_is_published_as_absent()
    {
        await _factory.ResetAsync();

        var sales = await SalesAsync(ApiFactory.ManagerObjectId);

        var unreported = sales[2];
        Assert.Equal(JsonValueKind.Null, unreported.GetProperty("productName").ValueKind);
        Assert.Equal(JsonValueKind.Null, unreported.GetProperty("paymentMethod").ValueKind);
        Assert.Equal(JsonValueKind.Null, unreported.GetProperty("currencyCode").ValueKind);
        Assert.Equal("Unknown(0 = 44.64)", sales[1].GetProperty("productName").GetString());
    }

    /// <summary>
    /// The financial boundary (issue #698's pattern): the endpoint stays open to every role that may
    /// operate inventory, and the amount is withheld instead. An Operator still gets the sale's date
    /// and time, the reported product name and the payment method - the rows stay readable - and
    /// <c>null</c>, never <c>0</c>, where the amount would be.
    /// </summary>
    [Fact]
    public async Task An_operator_is_answered_every_sale_without_its_amount()
    {
        await _factory.ResetAsync();

        var sales = await SalesAsync(ApiFactory.OperatorObjectId);

        Assert.Equal(3, sales.Count);
        foreach (var sale in sales)
        {
            Assert.Equal(SaleProperties, Properties(sale));
            Assert.Equal(JsonValueKind.Null, sale.GetProperty("saleAmount").ValueKind);
            Assert.Equal(JsonValueKind.Null, sale.GetProperty("currencyCode").ValueKind);
        }

        Assert.Equal("Bottled Water", sales[0].GetProperty("productName").GetString());
        Assert.Equal("Credit Card", sales[0].GetProperty("paymentMethod").GetString());
        Assert.Equal(
            new DateTime(2026, 10, 17, 23, 30, 0, DateTimeKind.Utc),
            sales[0].GetProperty("saleDateTimeUtc").GetDateTime());
    }

    /// <summary>
    /// Withholding the amount changes nothing else: every other key of every row is byte-identical
    /// to the one a Manager is given over the same window.
    /// </summary>
    [Fact]
    public async Task An_operator_is_given_the_same_non_financial_fields_as_a_manager()
    {
        await _factory.ResetAsync();

        var asManager = await SalesAsync(ApiFactory.ManagerObjectId);
        var asOperator = await SalesAsync(ApiFactory.OperatorObjectId);

        Assert.Equal(asManager.Count, asOperator.Count);
        var withheldValuesSeen = 0;
        for (var index = 0; index < asManager.Count; index++)
        {
            foreach (var property in asManager[index].EnumerateObject())
            {
                var actual = asOperator[index].GetProperty(property.Name);
                if (property.Name is "saleAmount" or "currencyCode")
                {
                    Assert.Equal(JsonValueKind.Null, actual.ValueKind);
                    if (property.Value.ValueKind != JsonValueKind.Null)
                    {
                        withheldValuesSeen++;
                    }

                    continue;
                }

                Assert.Equal(property.Value.GetRawText(), actual.GetRawText());
            }
        }

        Assert.True(withheldValuesSeen > 0, "The unredacted payload carried no withheld figure to compare.");
    }

    /// <summary>
    /// The allow-list, over the real payload: no role's response carries the card number, the CLI,
    /// the payment-service identifiers, the card brand or the transaction-level cost price, under
    /// their own names or any other, and no response carries a transaction status - the documented
    /// <c>lastSales</c> contract declares none, so inventing one would be a financial claim this
    /// application cannot source.
    /// </summary>
    [Theory]
    [InlineData(ApiFactory.OwnerObjectId)]
    [InlineData(ApiFactory.ManagerObjectId)]
    [InlineData(ApiFactory.OperatorObjectId)]
    public async Task No_response_carries_a_field_the_sales_tab_does_not_show(string objectId)
    {
        await _factory.ResetAsync();

        var body = await (await _factory.Authenticated(objectId).GetAsync(LatestSalesUri))
            .Content.ReadAsStringAsync();

        foreach (var forbidden in ForbiddenValues)
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        }

        // No status under any spelling, and none of the payload's other identifying fields.
        Assert.DoesNotContain("status", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cardNumber", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cardBrand", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("machineName", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("siteName", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            SaleProperties,
            Properties(JsonDocument.Parse(body).RootElement.GetProperty("sales").EnumerateArray().First()));
    }

    /// <summary>
    /// A read, and nothing else. Reading and refreshing the window imports no sale, records no stock
    /// movement and changes no product's quantity or value - which is the whole difference between
    /// this endpoint and <c>POST api/nayax-sales-sync</c>, which calls the very same adapter method.
    /// </summary>
    [Fact]
    public async Task Reading_and_refreshing_the_latest_sales_persists_nothing()
    {
        await _factory.ResetAsync();
        var client = _factory.Authenticated(ApiFactory.OwnerObjectId);
        var before = await _factory.PersistedStateAsync();

        await client.GetAsync(LatestSalesUri);
        await client.GetAsync(LatestSalesUri);
        await client.GetAsync(LatestSalesUri);

        Assert.Equal(before, await _factory.PersistedStateAsync());
    }

    /// <summary>
    /// Each business's read is authenticated with its own stored operator id and token, so the
    /// provider itself is what decides which machine's sales a business may see, and the data each
    /// business is answered is its own. There is no global credential to fall back to.
    /// </summary>
    [Fact]
    public async Task Each_business_s_read_carries_its_own_stored_credentials()
    {
        await _factory.ResetAsync();

        var ours = await SalesAsync(ApiFactory.OwnerObjectId);
        var theirs = await SalesAsync(ApiFactory.OtherBusinessOwnerObjectId, Uri(ApiFactory.OtherLocalMachineId));

        Assert.Equal(
            new[]
            {
                $"https://nayax.invalid/operational/v1/machines/{ApiFactory.MachineId}/lastSales",
                $"https://nayax.invalid/operational/v1/machines/{ApiFactory.OtherMachineId}/lastSales",
            },
            _factory.Handler.Requests.Select(request => request.Url));
        Assert.Equal(
            new[] { $"Bearer {ApiFactory.TokenA}", $"Bearer {ApiFactory.TokenB}" },
            _factory.Handler.Requests.Select(request => request.Authorization));

        Assert.Equal("Bottled Water", ours[0].GetProperty("productName").GetString());
        Assert.Equal(ApiFactory.OtherBusinessProductName, Assert.Single(theirs).GetProperty("productName").GetString());
    }

    /// <summary>
    /// A business with no sales provider at all (issue #759) is answered its own state: <c>200</c>
    /// with <c>isAvailable: false</c> and the <c>NoSalesProvider</c> code - never a failure, and
    /// never an empty list that reads as "this machine has sold nothing". No request leaves the API,
    /// because the credential provider refuses before the connection is read.
    /// </summary>
    [Fact]
    public async Task A_business_with_no_sales_provider_is_answered_the_no_provider_state()
    {
        await _factory.ResetAsync();

        var response = await _factory
            .Authenticated(ApiFactory.NoProviderOwnerObjectId)
            .GetAsync(Uri(ApiFactory.NoProviderLocalMachineId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.False(body.GetProperty("isAvailable").GetBoolean());
        Assert.Equal(
            MachineLatestSalesUnavailableReasons.NoSalesProvider,
            body.GetProperty("unavailableReason").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("unavailableDetail").GetString()));
        Assert.Empty(body.GetProperty("sales").EnumerateArray());
        Assert.Empty(_factory.Handler.Requests);
    }

    /// <summary>
    /// A stored connection the status gate refuses is a different answer from having no provider:
    /// the provider is configured and currently unusable, so an operator has to reconnect it. No
    /// request leaves the API here either.
    /// </summary>
    [Fact]
    public async Task A_connection_that_needs_attention_is_answered_as_not_connected()
    {
        await _factory.ResetAsync();
        await _factory.SetStatusAsync(ApiFactory.BusinessId, NayaxConnectionStatus.NeedsAttention);

        var body = await ReadJsonAsync(
            await _factory.Authenticated(ApiFactory.OwnerObjectId).GetAsync(LatestSalesUri));

        Assert.False(body.GetProperty("isAvailable").GetBoolean());
        Assert.Equal(
            MachineLatestSalesUnavailableReasons.NayaxNotConnected,
            body.GetProperty("unavailableReason").GetString());
        Assert.Empty(_factory.Handler.Requests);
    }

    /// <summary>
    /// Usable credentials that Nayax refuses for lack of permission are the third state: one
    /// feature's answer, not a verdict on the connection, so the stored status is left exactly as it
    /// was.
    /// </summary>
    [Fact]
    public async Task A_nayax_permission_refusal_is_answered_as_its_own_state()
    {
        await _factory.ResetAsync();
        await _factory.SetStatusAsync(ApiFactory.BusinessId, NayaxConnectionStatus.PendingPermissions);
        _factory.Handler.Respond(HttpStatusCode.Forbidden, "{\"message\":\"Insufficient permissions.\"}");

        var body = await ReadJsonAsync(
            await _factory.Authenticated(ApiFactory.OwnerObjectId).GetAsync(LatestSalesUri));

        Assert.False(body.GetProperty("isAvailable").GetBoolean());
        Assert.Equal(
            MachineLatestSalesUnavailableReasons.PermissionNotGranted,
            body.GetProperty("unavailableReason").GetString());
        Assert.Empty(body.GetProperty("sales").EnumerateArray());
        Assert.Equal(
            NayaxConnectionStatus.PendingPermissions,
            await _factory.StatusAsync(ApiFactory.BusinessId));
    }

    /// <summary>
    /// A provider read that succeeds and returns nothing is an <em>available</em> answer with no
    /// sales, which the Sales tab shows as an honest empty state rather than as one of the
    /// unavailable explanations above.
    /// </summary>
    [Fact]
    public async Task A_window_the_provider_reports_as_empty_is_available_and_empty()
    {
        await _factory.ResetAsync();
        _factory.Handler.Respond(HttpStatusCode.OK, "[]");

        var body = await ReadJsonAsync(
            await _factory.Authenticated(ApiFactory.OwnerObjectId).GetAsync(LatestSalesUri));

        Assert.True(body.GetProperty("isAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("unavailableReason").ValueKind);
        Assert.Empty(body.GetProperty("sales").EnumerateArray());
    }

    private async Task<List<JsonElement>> SalesAsync(string objectId, string? uri = null)
    {
        var response = await _factory.Authenticated(objectId).GetAsync(uri ?? LatestSalesUri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return [.. (await ReadJsonAsync(response)).GetProperty("sales").EnumerateArray()];
    }

    private static string[] Properties(JsonElement element) =>
        [.. element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string DirectoryTenantId = "88888888-8888-8888-8888-888888888888";
        public const string OwnerObjectId = "11111111-1111-1111-1111-111111111111";
        public const string ManagerObjectId = "22222222-2222-2222-2222-222222222222";
        public const string OperatorObjectId = "33333333-3333-3333-3333-333333333333";
        public const string OtherBusinessOwnerObjectId = "44444444-4444-4444-4444-444444444444";
        public const string NoProviderOwnerObjectId = "55555555-5555-5555-5555-555555555555";

        public const int BusinessId = 1;
        public const int OtherBusinessId = 2;
        public const int NoProviderBusinessId = 3;

        public const long MachineId = 942488501;
        public const long OtherMachineId = 629665354;

        // Each business's local machine for the provider numbers above (issue #741): what the
        // route takes. The no-provider business's machine carries no provider number at all.
        public const long LocalMachineId = 501;
        public const long OtherLocalMachineId = 502;
        public const long NoProviderLocalMachineId = 503;

        // Obviously fake credentials. No real token or operator account belongs in a test.
        public const string OperatorA = "2002736764";
        public const string OperatorB = "9009009009";
        public const string TokenA = "fake-token-for-business-a";
        public const string TokenB = "fake-token-for-business-b";

        // The values the real lastSales payload carries beside the displayed ones, and that no
        // response may publish. Distinctive strings so a substring search over the body is exact.
        public const string CardNumber = "5453xxxxxxxx5803";
        public const string Cli = "+61400000001";
        public const string PaymentServiceTransactionId = "PS-260122075902";
        public const string PaymentServiceProviderName = "StubBillingProcessor";
        public const string CardBrand = "MASTERCARD";
        public const string ProductCostPrice = "3.2567";

        public const string OtherBusinessProductName = "Rival Chocolate Bar";
        public const decimal NewestSettlementValue = 9.5m;

        private const string EncryptionKeyId = "2026-10";

        private static readonly DateTime CreatedAt = new(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly NayaxTokenProtectionOptions _protection = Protection();
        private bool _seeded;

        public ApiFactory() => _connection.Open();

        public StubNayaxHandler Handler { get; } = new();

        /// <summary>An unrestricted context for arranging and inspecting state outside a request.</summary>
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

        /// <summary>
        /// Puts every test back to the same starting point: both stored connections usable, the stub
        /// answering the documented payload, and no recorded request.
        /// </summary>
        public async Task ResetAsync()
        {
            await SeedAsync();
            Handler.Reset();

            await using var db = Database();
            await db.BusinessNayaxConnections.ExecuteUpdateAsync(
                setters => setters.SetProperty(connection => connection.Status, NayaxConnectionStatus.Ready));
        }

        public async Task SetStatusAsync(int businessId, NayaxConnectionStatus status)
        {
            await using var db = Database();
            await db.BusinessNayaxConnections
                .Where(connection => connection.BusinessId == businessId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(connection => connection.Status, status));
        }

        public async Task<NayaxConnectionStatus> StatusAsync(int businessId)
        {
            await using var db = Database();
            return (await db.BusinessNayaxConnections
                .AsNoTracking()
                .SingleAsync(connection => connection.BusinessId == businessId)).Status;
        }

        /// <summary>
        /// Everything a sales import, a costing run or a stock event would change, as one comparable
        /// value, so "this read persists nothing" is asserted over the tables rather than over an
        /// absence of exceptions.
        /// </summary>
        public async Task<string> PersistedStateAsync()
        {
            await using var db = Database();
            var product = await db.Products.AsNoTracking().SingleAsync();

            var sales = await db.NayaxSales.CountAsync();
            var movements = await db.StockAdjustments.CountAsync();
            var coverages = await db.ProviderDataCoverages.CountAsync();

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{sales}|{movements}|{coverages}|{product.QuantityInStock}|"
                    + $"{product.CostingQuantity}|{product.InventoryValue}|{product.AverageUnitCost}");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Testing applies pending migrations at startup, so the schema under these tests is the
            // migration history rather than a model snapshot.
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
            {
                services.AddAuthentication(TestActorAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestActorAuthenticationHandler>(
                        TestActorAuthenticationHandler.SchemeName, _ => { });

                // The real AES-GCM protector with a test key, so the seeded tokens are genuinely
                // encrypted at rest here rather than refused by the fail-closed unconfigured one.
                services.AddSingleton<INayaxTokenProtector>(new AesGcmNayaxTokenProtector(_protection));

                // The real typed client, the real credential provider and the real resilience
                // handler, with only the socket replaced: a deliberately unroutable base address, so
                // no test here can reach a real Nayax endpoint.
                services
                    .AddHttpClient<Inventory.Application.Nayax.INayaxLynxClient, NayaxLynxClient>(
                        http => http.BaseAddress = new Uri("https://nayax.invalid/operational/v1/"))
                    .ConfigurePrimaryHttpMessageHandler(() => Handler);
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

        private static NayaxTokenProtectionOptions Protection()
        {
            var protection = new NayaxTokenProtectionOptions { ActiveKeyId = EncryptionKeyId };
            protection.Keys[EncryptionKeyId] = Convert.ToBase64String(
                Enumerable.Repeat((byte)7, NayaxTokenProtectionConfiguration.KeySizeInBytes).ToArray());

            return protection;
        }

        private async Task SeedAsync()
        {
            await using var db = Database();
            if (_seeded)
            {
                return;
            }

            db.Businesses.AddRange(
                BusinessRow(BusinessId, "Vending Co", ExternalProvider.Nayax),
                BusinessRow(OtherBusinessId, "Rival Vending", ExternalProvider.Nayax),

                // No provider at all, which is a fully supported way to run this application: the
                // business has no connection row, so its sales provider was never claimed.
                BusinessRow(NoProviderBusinessId, "Local Only", salesProvider: null));

            db.BusinessMemberships.AddRange(
                Membership(BusinessId, OwnerObjectId, BusinessRole.Owner),
                Membership(BusinessId, ManagerObjectId, BusinessRole.Manager),
                Membership(BusinessId, OperatorObjectId, BusinessRole.Operator),
                Membership(OtherBusinessId, OtherBusinessOwnerObjectId, BusinessRole.Owner),
                Membership(NoProviderBusinessId, NoProviderOwnerObjectId, BusinessRole.Owner));

            // One product with real costing figures, so "nothing was persisted" compares values a
            // sales import would have moved rather than a row of zeroes.
            db.Products.Add(new Product
            {
                BusinessId = BusinessId,
                Name = "Coke",
                AverageUnitCost = 1.25m,
                CostingQuantity = 40,
                InventoryValue = 50m,
                QuantityInStock = 60,
                LowStockThreshold = 5,
                RestockTo = 24,
                IsActive = true,
                CreatedAt = CreatedAt,
                UpdatedAt = CreatedAt,
            });

            db.Machines.AddRange(
                new LocalMachine
                {
                    BusinessId = BusinessId,
                    Id = LocalMachineId,
                    Name = "Pavillion Left",
                    ExternalMachineId = NayaxExternalId.From(MachineId),
                },
                new LocalMachine
                {
                    BusinessId = OtherBusinessId,
                    Id = OtherLocalMachineId,
                    Name = "Rival machine",
                    ExternalMachineId = NayaxExternalId.From(OtherMachineId),
                },
                new LocalMachine { BusinessId = NoProviderBusinessId, Id = NoProviderLocalMachineId, Name = "Back office" });

            db.BusinessNayaxConnections.AddRange(
                Connection(BusinessId, OperatorA, TokenA),
                Connection(OtherBusinessId, OperatorB, TokenB));

            await db.SaveChangesAsync();
            _seeded = true;
        }

        private BusinessNayaxConnection Connection(int businessId, string operatorId, string accessToken)
        {
            // The ciphertext is authenticated with the business it belongs to (issue #681), so each
            // row's token only decrypts for that business - the same binding production stores.
            var encrypted = new AesGcmNayaxTokenProtector(_protection)
                .Protect(accessToken, Inventory.Domain.Tenancy.BusinessId.From(businessId));

            return new BusinessNayaxConnection
            {
                BusinessId = businessId,
                OperatorId = operatorId,
                AccessTokenCiphertext = encrypted.Ciphertext,
                EncryptionKeyId = encrypted.KeyId,
                Status = NayaxConnectionStatus.Ready,
                CredentialRevision = 1,
                LastTestedAtUtc = CreatedAt,
                MachineChecksPending = false,
                UpdatedAtUtc = CreatedAt,
            };
        }

        private static Business BusinessRow(int id, string name, ExternalProvider? salesProvider) =>
            new()
            {
                Id = id,
                Name = name,
                TimeZoneId = "Australia/Sydney",
                Status = BusinessStatus.Active,
                SalesProvider = salesProvider,
                CreatedAtUtc = CreatedAt,
            };

        private static BusinessMembership Membership(int businessId, string objectId, BusinessRole role) =>
            new()
            {
                BusinessId = businessId,
                DirectoryTenantId = DirectoryTenantId,
                ObjectId = objectId,
                Role = role,
                IsActive = true,
                CreatedAtUtc = CreatedAt,
                StatusChangedAtUtc = CreatedAt,
            };

        /// <summary>
        /// Stands in for the Nayax socket. It records every request - method, absolute URL and
        /// <c>Authorization</c> header - and answers the documented <c>lastSales</c> payload for
        /// whichever business's token the request carried, so a test can state both what left the API
        /// and what each business was answered.
        /// </summary>
        public sealed class StubNayaxHandler : HttpMessageHandler
        {
            private HttpStatusCode _status = HttpStatusCode.OK;
            private string? _body;

            public List<(string Method, string? Url, string? Authorization)> Requests { get; } = [];

            /// <summary>Answers every subsequent request with this status and body instead.</summary>
            public void Respond(HttpStatusCode status, string body)
            {
                _status = status;
                _body = body;
            }

            public void Reset()
            {
                Requests.Clear();
                _status = HttpStatusCode.OK;
                _body = null;
            }

            /// <summary>
            /// Deliberately inert: <c>IHttpClientFactory</c> disposes a primary handler when its
            /// handler lifetime expires, and this one is shared by the whole fixture.
            /// </summary>
            protected override void Dispose(bool disposing)
            {
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var authorization = request.Headers.Authorization?.ToString();
                Requests.Add((request.Method.Method, request.RequestUri?.ToString(), authorization));

                return Task.FromResult(new HttpResponseMessage(_status)
                {
                    Content = new StringContent(
                        _body ?? PayloadFor(authorization), Encoding.UTF8, "application/json"),
                    RequestMessage = request,
                });
            }

            /// <summary>
            /// The documented response shape, verbatim in its field names and types, including the
            /// fields the machine page must never publish. Business B's own window is a different
            /// single sale, so one business cannot be answered the other's data unnoticed.
            /// </summary>
            private static string PayloadFor(string? authorization) =>
                authorization == $"Bearer {TokenB}"
                    ? $$"""
                        [
                          {
                            "TransactionID": 99,
                            "PaymentServiceTransactionID": "{{PaymentServiceTransactionId}}",
                            "PaymentServiceProviderName": "{{PaymentServiceProviderName}}",
                            "MachineID": {{OtherMachineId}},
                            "MachineName": "Rival Machine",
                            "AuthorizationValue": 2.5,
                            "SettlementValue": 2.5,
                            "CurrencyCode": "AUD",
                            "PaymentMethod": "Credit Card",
                            "RecognitionMethod": "Chip",
                            "CardNumber": "{{CardNumber}}",
                            "CardBrand": "{{CardBrand}}",
                            "CLI": "{{Cli}}",
                            "ProductName": "{{OtherBusinessProductName}}",
                            "ProductCostPrice": {{ProductCostPrice}},
                            "Quantity": 1,
                            "AuthorizationDateTimeGMT": "2026-10-16T02:00:00.000Z",
                            "MachineAuthorizationTime": "2026-10-16T13:00:00",
                            "SettlementDateTimeGMT": "2026-10-16T02:01:00.000Z",
                            "SiteID": 4,
                            "SiteName": "US"
                          }
                        ]
                        """
                    : $$"""
                        [
                          {
                            "TransactionID": 1,
                            "PaymentServiceTransactionID": "{{PaymentServiceTransactionId}}",
                            "PaymentServiceProviderName": "{{PaymentServiceProviderName}}",
                            "MachineID": {{MachineId}},
                            "MachineName": "Pavillion Left",
                            "AuthorizationValue": 44.64,
                            "SettlementValue": 44.64,
                            "CurrencyCode": "AUD",
                            "PaymentMethod": "Prepaid Credit",
                            "RecognitionMethod": "Prepaid Credit",
                            "CardNumber": "{{CardNumber}}",
                            "CardBrand": null,
                            "CLI": null,
                            "ProductName": "Unknown(0 = 44.64)\n",
                            "ProductCostPrice": {{ProductCostPrice}},
                            "Quantity": 1,
                            "AuthorizationDateTimeGMT": "2026-10-16T09:28:18.380",
                            "MachineAuthorizationTime": "2026-10-16T20:28:18.117",
                            "SettlementDateTimeGMT": "2026-10-16T09:29:37.527",
                            "SiteID": 4,
                            "SiteName": "US"
                          },
                          {
                            "TransactionID": 2,
                            "PaymentServiceTransactionID": null,
                            "PaymentServiceProviderName": null,
                            "MachineID": {{MachineId}},
                            "MachineName": "Pavillion Left",
                            "AuthorizationValue": 10.00,
                            "SettlementValue": {{NewestSettlementValue}},
                            "CurrencyCode": "AUD",
                            "PaymentMethod": "Credit Card",
                            "RecognitionMethod": "Chip",
                            "CardNumber": "{{CardNumber}}",
                            "CardBrand": "{{CardBrand}}",
                            "CLI": "{{Cli}}",
                            "ProductName": "Bottled Water",
                            "ProductCostPrice": {{ProductCostPrice}},
                            "Quantity": 2,
                            "AuthorizationDateTimeGMT": "2026-10-17T23:30:00.000Z",
                            "MachineAuthorizationTime": "2026-10-18T10:30:00.000",
                            "SettlementDateTimeGMT": "2026-10-17T23:31:00.000Z",
                            "SiteID": 2,
                            "SiteName": "IL1"
                          },
                          {
                            "TransactionID": 3,
                            "PaymentServiceTransactionID": null,
                            "PaymentServiceProviderName": null,
                            "MachineID": {{MachineId}},
                            "MachineName": "Pavillion Left",
                            "AuthorizationValue": 1.00,
                            "SettlementValue": 1.00,
                            "CurrencyCode": null,
                            "PaymentMethod": null,
                            "RecognitionMethod": null,
                            "CardNumber": null,
                            "CardBrand": null,
                            "CLI": null,
                            "ProductName": null,
                            "ProductCostPrice": null,
                            "Quantity": 1,
                            "AuthorizationDateTimeGMT": "",
                            "MachineAuthorizationTime": "2026-10-14T10:30:00.000",
                            "SettlementDateTimeGMT": "2026-10-14T00:30:00.000Z",
                            "SiteID": 2,
                            "SiteName": "IL1"
                          }
                        ]
                        """;
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
}
