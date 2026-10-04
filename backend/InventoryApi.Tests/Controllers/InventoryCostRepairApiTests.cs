using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Inventory.Application.Costing;
using Inventory.Application.Time;
using Inventory.Domain.Costing;
using Inventory.Domain.FinancialConfiguration;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Tests.Application.Time;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The costing-repair HTTP contract (issue #360), exercised through the real request pipeline:
/// authentication, the required scope, the business-scope middleware, model binding, the
/// controller, the issue #359 use cases, relational SQLite, and the registered exception handlers.
///
/// It is hosted rather than unit tested because every criterion this endpoint carries is a
/// pipeline fact, not a controller fact: that a stale preview surfaces as 400 ProblemDetails
/// through <see cref="InventoryApi.Http.DomainExceptionHandler"/>, that the ledger fingerprint
/// survives a JSON round trip, that an effective instant comes back as the UTC instant it named,
/// that an unauthenticated or unscoped caller never reaches the use case, and that another
/// business's product is indistinguishable from one that does not exist. A mocked controller
/// cannot prove any of them.
///
/// The seeded product is the situation a repair exists for (the same shape as
/// <c>InventoryCostRepairUseCaseTests</c>): it entered the inventory-cost transition with an
/// opening costing quantity of zero while stock was still in the machines, so its two completed
/// sales have no costed stock to consume. Each test seeds its own product, so one test's applied
/// repair cannot change another's ledger.
/// </summary>
public sealed class InventoryCostRepairApiTests : IClassFixture<InventoryCostRepairApiTests.ApiFactory>
{
    private const string PreviewUri = "/api/admin/inventory-cost-repair/preview";
    private const string ApplyUri = "/api/admin/inventory-cost-repair/apply";
    private const string Reason = "Machine stock at the 2026 cutover was never costed.";
    private const long MissingProductId = 999999;

    private readonly ApiFactory _factory;

    public InventoryCostRepairApiTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Preview_returns_every_field_the_use_case_produces_and_persists_nothing()
    {
        var productId = await _factory.SeedRepairableProductAsync(ApiFactory.CallerBusinessId);

        var response = await _factory.Caller().PostAsJsonAsync(PreviewUri, PreviewRequest(productId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);
        Assert.Equal(
            [
                "averageUnitCostAfter",
                "costingQuantityAfter",
                "costingQuantityBefore",
                "effectiveAt",
                "firstUncostableSale",
                "inventoryValueAfter",
                "inventoryValueBefore",
                "ledgerFingerprint",
                "productId",
                "productName",
                "projectedAverageUnitCost",
                "projectedCostingQuantity",
                "projectedInventoryValue",
                "quantity",
                "reason",
                "remainingFatalIssues",
                "replaysBeforeFirstUncostableSale",
                "totalValue",
                "unitCost",
            ],
            json.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(productId, json.GetProperty("productId").GetInt64());
        Assert.Equal("Coke Zero", json.GetProperty("productName").GetString());
        Assert.Equal("2026-01-02T12:00:00Z", json.GetProperty("effectiveAt").GetString());
        Assert.Equal((4, 2m, 8m, Reason), (
            json.GetProperty("quantity").GetInt32(),
            json.GetProperty("unitCost").GetDecimal(),
            json.GetProperty("totalValue").GetDecimal(),
            json.GetProperty("reason").GetString()!));
        Assert.Equal((0, 0m), (
            json.GetProperty("costingQuantityBefore").GetInt32(),
            json.GetProperty("inventoryValueBefore").GetDecimal()));
        Assert.Equal((4, 8m, 2m), (
            json.GetProperty("costingQuantityAfter").GetInt32(),
            json.GetProperty("inventoryValueAfter").GetDecimal(),
            json.GetProperty("averageUnitCostAfter").GetDecimal()));
        Assert.Equal((2, 4m, 2m), (
            json.GetProperty("projectedCostingQuantity").GetInt32(),
            json.GetProperty("projectedInventoryValue").GetDecimal(),
            json.GetProperty("projectedAverageUnitCost").GetDecimal()));
        Assert.True(json.GetProperty("replaysBeforeFirstUncostableSale").GetBoolean());
        Assert.Empty(json.GetProperty("remainingFatalIssues").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("ledgerFingerprint").GetString()));

        var sale = json.GetProperty("firstUncostableSale");
        Assert.Equal(
            ["authorizationTime", "transactionId"],
            sale.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(ApiFactory.FirstTransactionId(productId), sale.GetProperty("transactionId").GetInt64());
        Assert.Equal("2026-01-03T12:00:00Z", sale.GetProperty("authorizationTime").GetString());

        await _factory.AssertNothingWasRepairedAsync(productId);
    }

    /// <summary>
    /// A repair too small to cover the uncostable sales is still previewable: the operator has to
    /// be able to see, before applying anything, which fatal data-quality issue it would leave
    /// behind - which is also the reason the apply would refuse it.
    /// </summary>
    [Fact]
    public async Task Preview_reports_the_fatal_issues_a_partial_repair_would_leave_behind()
    {
        var productId = await _factory.SeedRepairableProductAsync(ApiFactory.CallerBusinessId);

        var response = await _factory.Caller().PostAsJsonAsync(
            PreviewUri, new { productId, effectiveAt = Day(2), quantity = 1, unitCost = 2m, reason = Reason });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);
        Assert.Equal(0, json.GetProperty("projectedCostingQuantity").GetInt32());
        var issue = json.GetProperty("remainingFatalIssues").EnumerateArray().Single();
        Assert.Equal(
            ["code", "message"],
            issue.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(CostDataQualityIssueCodes.UnknownCost, issue.GetProperty("code").GetString());
        Assert.Contains(
            $"Completed Nayax sale {ApiFactory.FirstTransactionId(productId) + 1}",
            issue.GetProperty("message").GetString(),
            StringComparison.Ordinal);
        await _factory.AssertNothingWasRepairedAsync(productId);
    }

    /// <summary>
    /// The preview's fingerprint has to survive the JSON round trip, because it is the whole
    /// stale-preview guarantee: an apply carrying a fingerprint the client echoed back must be
    /// accepted, and the preview's projected position must be what the apply actually produces.
    /// </summary>
    [Fact]
    public async Task Apply_accepts_the_previewed_fingerprint_and_returns_the_complete_applied_result()
    {
        var productId = await _factory.SeedRepairableProductAsync(ApiFactory.CallerBusinessId);
        var client = _factory.Caller();
        var preview = await ReadJsonAsync(await client.PostAsJsonAsync(PreviewUri, PreviewRequest(productId)));
        var fingerprint = preview.GetProperty("ledgerFingerprint").GetString();

        var response = await client.PostAsJsonAsync(ApplyUri, ApplyRequest(productId, fingerprint!));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);
        Assert.Equal(
            ["averageUnitCost", "costingQuantity", "inventoryValue", "recostedSaleCount", "repair"],
            json.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal((2, 4m, 2m, 2), (
            json.GetProperty("costingQuantity").GetInt32(),
            json.GetProperty("inventoryValue").GetDecimal(),
            json.GetProperty("averageUnitCost").GetDecimal(),
            json.GetProperty("recostedSaleCount").GetInt32()));

        var repair = json.GetProperty("repair");
        Assert.Equal(
            [
                "createdAt",
                "createdByDirectoryTenantId",
                "createdByObjectId",
                "effectiveAt",
                "id",
                "productId",
                "quantity",
                "reason",
                "totalValue",
                "unitCost",
            ],
            repair.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.True(repair.GetProperty("id").GetInt32() > 0);
        Assert.Equal(productId, repair.GetProperty("productId").GetInt64());
        Assert.Equal("2026-01-02T12:00:00Z", repair.GetProperty("effectiveAt").GetString());
        Assert.Equal((4, 2m, 8m, Reason), (
            repair.GetProperty("quantity").GetInt32(),
            repair.GetProperty("unitCost").GetDecimal(),
            repair.GetProperty("totalValue").GetDecimal(),
            repair.GetProperty("reason").GetString()!));
        Assert.Equal("2026-02-10T04:00:00Z", repair.GetProperty("createdAt").GetString());
        Assert.Equal(ApiFactory.DirectoryTenantId, repair.GetProperty("createdByDirectoryTenantId").GetString());
        Assert.Equal(ApiFactory.CallerObjectId, repair.GetProperty("createdByObjectId").GetString());

        // Stamped by the central ownership enforcer from the caller's resolved business, and
        // costing-only: the repair never restates physical stock.
        await using var db = _factory.Database();
        var stored = await db.InventoryCostRepairs.AsNoTracking().SingleAsync(x => x.ProductId == productId);
        Assert.Equal(ApiFactory.CallerBusinessId, stored.BusinessId);
        var product = await db.Products.AsNoTracking().SingleAsync(x => x.Id == productId);
        Assert.Equal(10, product.QuantityInStock);
        Assert.Equal((2, 4m), (product.CostingQuantity, product.InventoryValue));
    }

    /// <summary>
    /// Issue #359's stale-preview model over HTTP: a ledger that changed after the preview is a
    /// <c>DomainValidationException</c>, so it is a 400 - deliberately not a 409 - and it persists
    /// nothing. The response carries the use case's caller-safe message in both <c>detail</c> and
    /// the <c>message</c> extension the frontend reads, plus a trace identifier.
    /// </summary>
    [Fact]
    public async Task Apply_refuses_a_preview_whose_ledger_changed_with_400_problem_details_and_persists_nothing()
    {
        var productId = await _factory.SeedRepairableProductAsync(ApiFactory.CallerBusinessId);
        var client = _factory.Caller();
        var preview = await ReadJsonAsync(await client.PostAsJsonAsync(PreviewUri, PreviewRequest(productId)));
        await _factory.AddCompletedSaleAsync(productId, new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc));

        var response = await client.PostAsJsonAsync(
            ApplyUri, ApplyRequest(productId, preview.GetProperty("ledgerFingerprint").GetString()!));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith(
            "application/problem+json",
            response.Content.Headers.ContentType?.ToString() ?? string.Empty,
            StringComparison.Ordinal);
        var json = await ReadJsonAsync(response);
        const string Expected =
            "This product's cost history changed after the preview. Run the preview again before applying the repair.";
        Assert.Equal("Request validation failed", json.GetProperty("title").GetString());
        Assert.Equal(Expected, json.GetProperty("detail").GetString());
        Assert.Equal(Expected, json.GetProperty("message").GetString());
        Assert.Equal(400, json.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("traceId").GetString()));
        await _factory.AssertNothingWasRepairedAsync(productId);
    }

    /// <summary>
    /// Every deliberate repair rule reaches the caller through the existing validation mapping,
    /// with the Application's own message: an unusable quantity, cost or reason, an effective
    /// instant at the transition cutoff, a placement the replay reaches after the sale it must
    /// cover, and a repair that would leave the history fatally incomplete. None of them persists
    /// a repair - including the last one, which fails only once the rebuild has run.
    /// </summary>
    [Theory]
    [InlineData(0, 2, Reason, "2026-01-02T12:00:00Z", "A costing repair must add a positive quantity.")]
    [InlineData(-4, 2, Reason, "2026-01-02T12:00:00Z", "A costing repair must add a positive quantity.")]
    [InlineData(4, -1, Reason, "2026-01-02T12:00:00Z", "A costing repair unit cost cannot be negative.")]
    [InlineData(4, 2, "", "2026-01-02T12:00:00Z", "Record a specific reason for this costing repair: at least 10 characters, and not a placeholder.")]
    [InlineData(4, 2, "correction", "2026-01-02T12:00:00Z", "Record a specific reason for this costing repair: at least 10 characters, and not a placeholder.")]
    [InlineData(4, 2, Reason, "2026-01-01T12:00:00Z", "A costing repair must take effect after the product's inventory-cost transition cutoff")]
    [InlineData(4, 2, Reason, "2026-01-05T12:00:00Z", "The repair does not replay before completed sale")]
    [InlineData(1, 2, Reason, "2026-01-02T12:00:00Z", "This repair does not complete the product's cost history, so nothing was saved:")]
    public async Task Apply_maps_a_refused_repair_to_400_problem_details_and_persists_nothing(
        int quantity,
        int unitCost,
        string reason,
        string effectiveAt,
        string expectedDetail)
    {
        var productId = await _factory.SeedRepairableProductAsync(ApiFactory.CallerBusinessId);
        var client = _factory.Caller();
        var preview = await ReadJsonAsync(await client.PostAsJsonAsync(PreviewUri, PreviewRequest(productId)));

        var response = await client.PostAsJsonAsync(ApplyUri, new
        {
            productId,
            effectiveAt,
            quantity,
            unitCost,
            reason,
            ledgerFingerprint = preview.GetProperty("ledgerFingerprint").GetString(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await ReadJsonAsync(response);
        Assert.Equal("Request validation failed", json.GetProperty("title").GetString());
        Assert.Contains(expectedDetail, json.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(json.GetProperty("detail").GetString(), json.GetProperty("message").GetString());
        await _factory.AssertNothingWasRepairedAsync(productId);
    }

    /// <summary>
    /// An unexpected failure keeps the generic 500 mapping: no exception message reaches the
    /// caller, and the repair the apply had already appended is rolled back with its transaction.
    /// </summary>
    [Fact]
    public async Task An_unexpected_rebuild_failure_returns_the_generic_500_and_persists_nothing()
    {
        const string InternalDetail = "internal-rebuild-failure-must-not-be-published";
        var rebuild = new Mock<IRebuildProductCost>();
        rebuild.Setup(x => x.RebuildCostingOnlyAsync(It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(InternalDetail));
        await using var factory = new ApiFactory(services => services.AddScoped(_ => rebuild.Object));
        var productId = await factory.SeedRepairableProductAsync(ApiFactory.CallerBusinessId);
        var client = factory.Caller();
        var preview = await ReadJsonAsync(await client.PostAsJsonAsync(PreviewUri, PreviewRequest(productId)));

        var response = await client.PostAsJsonAsync(
            ApplyUri, ApplyRequest(productId, preview.GetProperty("ledgerFingerprint").GetString()!));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(InternalDetail, body, StringComparison.Ordinal);
        Assert.Contains("An unexpected error occurred", body, StringComparison.Ordinal);
        await factory.AssertNothingWasRepairedAsync(productId);
    }

    /// <summary>
    /// The effective instant is accepted and returned as UTC. An explicit <c>Z</c> and an
    /// offset-bearing timestamp both name a real instant and come back as that instant; a
    /// timezone-less value follows issue #359's existing policy of reading it as UTC. The response
    /// is always the UTC spelling, whichever of the three the caller sent.
    /// </summary>
    [Theory]
    [InlineData("2026-01-02T12:00:00Z")]
    [InlineData("2026-01-02T14:00:00+02:00")]
    [InlineData("2026-01-02T08:00:00-04:00")]
    [InlineData("2026-01-02T12:00:00")]
    public async Task An_effective_timestamp_is_accepted_and_returned_as_the_utc_instant_it_names(string effectiveAt)
    {
        var productId = await _factory.SeedRepairableProductAsync(ApiFactory.CallerBusinessId);
        var client = _factory.Caller();
        var request = new { productId, effectiveAt, quantity = 4, unitCost = 2m, reason = Reason };

        var preview = await ReadJsonAsync(await client.PostAsJsonAsync(PreviewUri, request));
        var applied = await ReadJsonAsync(await client.PostAsJsonAsync(ApplyUri, new
        {
            productId,
            effectiveAt,
            quantity = 4,
            unitCost = 2m,
            reason = Reason,
            ledgerFingerprint = preview.GetProperty("ledgerFingerprint").GetString(),
        }));
        var history = await ReadJsonAsync(await client.GetAsync(HistoryUri(productId)));

        Assert.Equal("2026-01-02T12:00:00Z", preview.GetProperty("effectiveAt").GetString());
        Assert.Equal("2026-01-02T12:00:00Z", applied.GetProperty("repair").GetProperty("effectiveAt").GetString());
        Assert.Equal(
            "2026-01-02T12:00:00Z",
            history.EnumerateArray().Single().GetProperty("effectiveAt").GetString());
    }

    [Fact]
    public async Task History_returns_the_products_repairs_newest_first_scoped_to_the_callers_business()
    {
        var productId = await _factory.SeedRepairableProductAsync(ApiFactory.CallerBusinessId);
        var otherProductId = await _factory.SeedRepairableProductAsync(ApiFactory.OtherBusinessId);
        await _factory.AddStoredRepairAsync(ApiFactory.CallerBusinessId, productId, Day(2), quantity: 1);
        await _factory.AddStoredRepairAsync(ApiFactory.CallerBusinessId, productId, Day(2).AddHours(1), quantity: 2);
        await _factory.AddStoredRepairAsync(ApiFactory.CallerBusinessId, productId, Day(2), quantity: 3);
        await _factory.AddStoredRepairAsync(ApiFactory.OtherBusinessId, otherProductId, Day(2), quantity: 9);

        var response = await _factory.Caller().GetAsync(HistoryUri(productId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);
        Assert.Equal(
            [("2026-01-02T13:00:00Z", 2), ("2026-01-02T12:00:00Z", 3), ("2026-01-02T12:00:00Z", 1)],
            json.EnumerateArray()
                .Select(repair => (
                    repair.GetProperty("effectiveAt").GetString()!,
                    repair.GetProperty("quantity").GetInt32()))
                .ToArray());
        Assert.All(json.EnumerateArray(), repair =>
            Assert.Equal(productId, repair.GetProperty("productId").GetInt64()));
    }

    /// <summary>
    /// Another business's product must be refused exactly as a product that does not exist at all:
    /// same status, same message, no hint that the row is there. Nothing in the controller filters
    /// by business - the central query filter makes the product invisible, and the use case reports
    /// it as missing.
    /// </summary>
    [Fact]
    public async Task Another_businesss_product_is_refused_exactly_like_a_product_that_does_not_exist()
    {
        var otherProductId = await _factory.SeedRepairableProductAsync(ApiFactory.OtherBusinessId);
        await _factory.AddStoredRepairAsync(ApiFactory.OtherBusinessId, otherProductId, Day(2), quantity: 4);
        var client = _factory.Caller();

        var previewResponses = new[]
        {
            await client.PostAsJsonAsync(PreviewUri, PreviewRequest(otherProductId)),
            await client.PostAsJsonAsync(PreviewUri, PreviewRequest(MissingProductId)),
        };
        var applyResponses = new[]
        {
            await client.PostAsJsonAsync(ApplyUri, ApplyRequest(otherProductId, "any-fingerprint")),
            await client.PostAsJsonAsync(ApplyUri, ApplyRequest(MissingProductId, "any-fingerprint")),
        };
        var historyResponses = new[]
        {
            await client.GetAsync(HistoryUri(otherProductId)),
            await client.GetAsync(HistoryUri(MissingProductId)),
        };

        foreach (var (crossBusiness, missing, productId) in new[]
        {
            (previewResponses[0], previewResponses[1], otherProductId),
            (applyResponses[0], applyResponses[1], otherProductId),
            (historyResponses[0], historyResponses[1], otherProductId),
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, crossBusiness.StatusCode);
            Assert.Equal(missing.StatusCode, crossBusiness.StatusCode);
            var crossBusinessJson = await ReadJsonAsync(crossBusiness);
            var missingJson = await ReadJsonAsync(missing);
            Assert.Equal($"Product {productId} does not exist.", crossBusinessJson.GetProperty("detail").GetString());
            Assert.Equal(
                $"Product {MissingProductId} does not exist.",
                missingJson.GetProperty("detail").GetString());
            Assert.Equal(
                missingJson.GetProperty("title").GetString(),
                crossBusinessJson.GetProperty("title").GetString());
        }

        // The other business's data is untouched, and its own member still reads it.
        var otherHistory = await ReadJsonAsync(
            await _factory.Authenticated(ApiFactory.OtherObjectId).GetAsync(HistoryUri(otherProductId)));
        Assert.Equal(4, otherHistory.EnumerateArray().Single().GetProperty("quantity").GetInt32());
    }

    [Theory]
    [InlineData("POST", PreviewUri)]
    [InlineData("POST", ApplyUri)]
    [InlineData("GET", "/api/admin/inventory-cost-repair/1")]
    public async Task An_unauthenticated_request_never_reaches_the_use_case(string method, string uri)
    {
        var response = await _factory.CreateClient().SendAsync(BoundaryRequest(method, uri));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A token without the API's required scope is refused by the endpoint's own
    /// <c>[RequiredScope]</c> filter, not by the business-scope middleware: the caller used here is
    /// a member of the business, so the refusal can only come from the missing scope.
    /// </summary>
    [Theory]
    [InlineData("POST", PreviewUri)]
    [InlineData("POST", ApplyUri)]
    [InlineData("GET", "/api/admin/inventory-cost-repair/1")]
    public async Task An_authenticated_caller_without_the_required_scope_is_refused(string method, string uri)
    {
        var response = await _factory.WithoutScope().SendAsync(BoundaryRequest(method, uri));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(
            "not associated with a business",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    private static object PreviewRequest(long productId) =>
        new { productId, effectiveAt = Day(2), quantity = 4, unitCost = 2m, reason = Reason };

    private static object ApplyRequest(long productId, string ledgerFingerprint) =>
        new { productId, effectiveAt = Day(2), quantity = 4, unitCost = 2m, reason = Reason, ledgerFingerprint };

    private static string HistoryUri(long productId) => $"/api/admin/inventory-cost-repair/{productId}";

    /// <summary>A well-formed request for an authorization-boundary test, body included for a POST.</summary>
    private static HttpRequestMessage BoundaryRequest(string method, string uri)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), uri);
        if (request.Method == HttpMethod.Post)
        {
            request.Content = JsonContent.Create(PreviewRequest(1));
        }

        return request;
    }

    private static DateTime Day(int day) => new(2026, 1, day, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Hosts the real application over one in-memory SQLite database - relational, so transactions,
    /// constraints and SQL translation are the real ones - with two businesses, their memberships,
    /// a fixed clock and a test authentication scheme that turns request headers into the
    /// <c>(tid, oid, scp)</c> claims a validated Entra token would carry. Nothing else is replaced:
    /// the controller, use cases, EF adapters, tenant middleware, query filters, ownership stamping
    /// and exception handlers are the production ones.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const int CallerBusinessId = 1;
        public const int OtherBusinessId = 2;
        public const string DirectoryTenantId = "33333333-3333-3333-3333-333333333333";
        public const string CallerObjectId = "11111111-1111-1111-1111-111111111111";
        public const string OtherObjectId = "22222222-2222-2222-2222-222222222222";

        private static readonly DateTime AppliedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly Action<IServiceCollection>? _overrides;
        private bool _seeded;

        public ApiFactory() : this(null)
        {
        }

        /// <summary>
        /// Internal, not public: xUnit allows a class fixture exactly one public constructor, and
        /// only the one test that needs a failing dependency builds a factory with overrides.
        /// </summary>
        internal ApiFactory(Action<IServiceCollection>? overrides)
        {
            _overrides = overrides;
            _connection.Open();
        }

        /// <summary>The transaction id of the first completed sale seeded for a product.</summary>
        public static long FirstTransactionId(long productId) => (productId * 10) + 1;

        /// <summary>An unrestricted context for arranging and verifying state outside the boundary.</summary>
        public AppDbContext Database()
        {
            // Resolving Services builds and starts the host, which is what applies the migrations
            // this context then reads and writes through.
            _ = Services;
            return TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        }

        public HttpClient Caller() => Authenticated(CallerObjectId);

        public HttpClient Authenticated(string objectId)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.DirectoryTenantIdHeader, DirectoryTenantId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ObjectIdHeader, objectId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ScopeHeader, "access_as_user");
            return client;
        }

        /// <summary>A member of the caller's business whose token carries no scope at all.</summary>
        public HttpClient WithoutScope()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.DirectoryTenantIdHeader, DirectoryTenantId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ObjectIdHeader, CallerObjectId);
            return client;
        }

        /// <summary>
        /// One product in the state a costing repair exists for: physical stock on hand, a
        /// transition baseline with no opening costing quantity, and two later completed sales the
        /// ledger therefore cannot cost.
        /// </summary>
        public async Task<long> SeedRepairableProductAsync(int businessId)
        {
            await using var db = Database();
            await SeedBusinessesAsync(db);

            var product = new Product
            {
                BusinessId = businessId,
                Name = "Coke Zero",
                QuantityInStock = 10,
            };
            db.Products.Add(product);
            await db.SaveChangesAsync();

            db.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
            {
                BusinessId = businessId,
                ProductId = product.Id,
                CutoffAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                HomeStockQuantity = 10,
                OpeningCostingQuantity = 0,
                InventoryValue = 0m,
                AverageUnitCost = 0m,
                CreatedAt = AppliedAt,
            });
            db.NayaxSales.AddRange(
                Sale(businessId, product.Id, FirstTransactionId(product.Id), new DateTime(2026, 1, 3, 12, 0, 0, DateTimeKind.Utc)),
                Sale(businessId, product.Id, FirstTransactionId(product.Id) + 1, new DateTime(2026, 1, 4, 12, 0, 0, DateTimeKind.Utc)));
            await db.SaveChangesAsync();
            return product.Id;
        }

        public async Task AddCompletedSaleAsync(long productId, DateTime authorizationTime)
        {
            await using var db = Database();
            var product = await db.Products.AsNoTracking().SingleAsync(x => x.Id == productId);
            db.NayaxSales.Add(Sale(
                product.BusinessId, productId, FirstTransactionId(productId) + 2, authorizationTime));
            await db.SaveChangesAsync();
        }

        public async Task AddStoredRepairAsync(int businessId, long productId, DateTime effectiveAt, int quantity)
        {
            await using var db = Database();
            db.InventoryCostRepairs.Add(new InventoryCostRepair
            {
                BusinessId = businessId,
                ProductId = productId,
                EffectiveAt = effectiveAt,
                Quantity = quantity,
                UnitCost = 2m,
                TotalValue = quantity * 2m,
                Reason = Reason,
                CreatedAt = AppliedAt,
                CreatedByDirectoryTenantId = DirectoryTenantId,
                CreatedByObjectId = businessId == CallerBusinessId ? CallerObjectId : OtherObjectId,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>No repair was stored, and the product's costing position is untouched.</summary>
        public async Task AssertNothingWasRepairedAsync(long productId)
        {
            await using var db = Database();
            Assert.Empty(await db.InventoryCostRepairs.AsNoTracking().Where(x => x.ProductId == productId).ToListAsync());
            var product = await db.Products.AsNoTracking().SingleAsync(x => x.Id == productId);
            Assert.Null(product.CostingQuantity);
            Assert.Null(product.InventoryValue);
            Assert.All(
                await db.NayaxSales.AsNoTracking().Where(x => x.NayaxProductId == productId).ToListAsync(),
                sale => Assert.Null(sale.UnitCostAtSale));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Testing applies pending migrations at startup, so the schema under these tests is the
            // migration history rather than a model snapshot.
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                var dbContextOptions = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (dbContextOptions is not null)
                {
                    services.Remove(dbContextOptions);
                }

                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            });

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IClock>(new FakeClock(AppliedAt));
                services.AddAuthentication(TestActorAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestActorAuthenticationHandler>(
                        TestActorAuthenticationHandler.SchemeName, _ => { });
                _overrides?.Invoke(services);
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

        private async Task SeedBusinessesAsync(AppDbContext db)
        {
            if (_seeded)
            {
                return;
            }

            db.Businesses.AddRange(
                new Business { Id = CallerBusinessId, Name = "Caller business", CreatedAtUtc = AppliedAt },
                new Business { Id = OtherBusinessId, Name = "Other business", CreatedAtUtc = AppliedAt });
            db.BusinessMemberships.AddRange(
                new BusinessMembership
                {
                    BusinessId = CallerBusinessId,
                    DirectoryTenantId = DirectoryTenantId,
                    ObjectId = CallerObjectId,
                    CreatedAtUtc = AppliedAt,
                },
                new BusinessMembership
                {
                    BusinessId = OtherBusinessId,
                    DirectoryTenantId = DirectoryTenantId,
                    ObjectId = OtherObjectId,
                    CreatedAtUtc = AppliedAt,
                });
            await db.SaveChangesAsync();
            _seeded = true;
        }

        private static NayaxSales Sale(int businessId, long productId, long transactionId, DateTime authorizationTime) =>
            new()
            {
                BusinessId = businessId,
                TransactionID = transactionId,
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                MachineID = 1,
                NayaxProductId = productId,
                MachineAuthorizationTime = authorizationTime,
            };
    }

    /// <summary>
    /// Stands in for the JWT bearer handler so a test can choose the caller: the <c>(tid, oid)</c>
    /// pair <see cref="InventoryApi.Auth.EntraActorIdentityAccessor"/> resolves an actor from, and
    /// the <c>scp</c> claim <c>[RequiredScope]</c> checks. No header at all means no credential,
    /// which must be answered by authentication rather than by any application code.
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
