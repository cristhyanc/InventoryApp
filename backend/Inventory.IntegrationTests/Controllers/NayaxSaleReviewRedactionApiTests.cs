using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.NayaxSqs;
using Inventory.Domain.Providers;
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
/// The Nayax sales review's financial boundary (issue #1306, audit finding AI-02), exercised through
/// the real request pipeline: authentication, the required scope, the <c>Dashboard.View</c>
/// capability policy, <c>BusinessScopeMiddleware</c>, the controller, the Application read and
/// relational SQLite with the real migration history.
///
/// <para>The list and the Dashboard summary require only <c>Dashboard.View</c>, which an Operator
/// holds - seeing which sales need attention is work an Operator does - so the endpoints stay open
/// and the money is withheld instead, exactly as issue #698's pattern withholds a machine's revenue
/// and issue #768's withholds a sale's amount. What these tests prove is that the three amounts the
/// response carries (<c>settlementValue</c>, the evidence's <c>providerSettlementValue</c> and a
/// history entry's <c>reconciledPeriodDifference</c>) arrive as <c>null</c> - never <c>0</c>, never a
/// figure - for a caller without <c>Dashboard.ViewFinancials</c>, and that nothing else about the
/// two responses changes: the same sales, in the same order, with the same filters, paging, counts
/// and available decisions a Manager is given.</para>
///
/// <para>Three members of one business hold the three roles against one seeded set of queue sales,
/// evidence and decision audits, so every difference between two responses is the role and nothing
/// else.</para>
/// </summary>
public sealed class NayaxSaleReviewRedactionApiTests : IClassFixture<NayaxSaleReviewRedactionApiTests.ApiFactory>
{
    private const string ListUri = "/api/nayax-sale-review";
    private const string SummaryUri = "/api/nayax-sale-review/summary";

    /// <summary>The whole published property set of one reviewed sale, in ordinal order.</summary>
    private static readonly string[] ItemProperties =
    [
        "authorizationInstantUtc", "availableDecisions", "history", "isCounted", "latestAbsence",
        "latestComparison", "latestEvidenceId", "machineId", "machineName", "nayaxProductId",
        "productId", "productName", "receivedViaSqsAtUtc", "saleId", "settlementValue", "state",
        "transactionId",
    ];

    /// <summary>Every figure the seeded data carries that an Operator must never be answered.</summary>
    private static readonly decimal[] WithheldFigures =
    [
        ApiFactory.ConflictSettlementValue,
        ApiFactory.InvestigationSettlementValue,
        ApiFactory.DecidedSettlementValue,
        ApiFactory.ProviderSettlementValue,
        ApiFactory.ReconciledPeriodDifference,
    ];

    private readonly ApiFactory _factory;

    public NayaxSaleReviewRedactionApiTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// The acceptance criterion: an Operator reaches the list and is answered every sale with the
    /// amount withheld - <c>null</c> rather than absent, rather than <c>0</c>, and rather than the
    /// figure - on the stored sale, on Nayax's own record of it and on the decision history.
    /// </summary>
    [Fact]
    public async Task An_operator_is_answered_the_needs_review_list_without_its_amounts()
    {
        await _factory.SeedAsync();

        var items = await ItemsAsync(ApiFactory.OperatorObjectId);

        Assert.Equal(2, items.Count);
        foreach (var item in items)
        {
            Assert.Equal(ItemProperties, Properties(item));
            Assert.Equal(JsonValueKind.Null, item.GetProperty("settlementValue").ValueKind);
        }

        var comparison = items.Single(item => item.GetProperty("saleId").GetInt64() == ApiFactory.ConflictSaleId)
            .GetProperty("latestComparison");
        Assert.Equal(JsonValueKind.Null, comparison.GetProperty("providerSettlementValue").ValueKind);

        // The non-financial evidence is untouched: the Operator still sees which field Nayax's
        // record disagreed about, which is what makes the row worth anyone's attention.
        Assert.Equal((int)NayaxSaleFieldComparison.Mismatch, comparison.GetProperty("amountComparison").GetInt32());
        Assert.Equal(ApiFactory.MachineId, comparison.GetProperty("providerMachineId").GetInt64());
    }

    /// <summary>
    /// The decided list carries the same decision history every role reads, with the reimbursement
    /// difference one decision moved withheld on exactly the same terms.
    /// </summary>
    [Fact]
    public async Task An_operator_is_answered_the_decided_list_without_its_reconciled_difference()
    {
        await _factory.SeedAsync();

        var item = Assert.Single(await ItemsAsync(ApiFactory.OperatorObjectId, view: 20));

        Assert.Equal(ApiFactory.DecidedSaleId, item.GetProperty("saleId").GetInt64());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("settlementValue").ValueKind);

        var entry = Assert.Single(item.GetProperty("history").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("reconciledPeriodDifference").ValueKind);

        // The period the decision moved is not an amount and stays: it says which reimbursement
        // period was affected, never by how much.
        Assert.NotEqual(JsonValueKind.Null, entry.GetProperty("reconciledPeriodFrom").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, entry.GetProperty("reconciledPeriodTo").ValueKind);
    }

    /// <summary>
    /// Withheld on the server, not hidden in the page: no figure the business's money is made of
    /// appears anywhere in an Operator's response body, under its own name or any other.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    public async Task An_operators_response_body_carries_no_amount_at_all(int view)
    {
        await _factory.SeedAsync();

        var body = await BodyAsync(ApiFactory.OperatorObjectId, $"{ListUri}?view={view.ToString(CultureInfo.InvariantCulture)}");

        foreach (var figure in WithheldFigures)
        {
            Assert.DoesNotContain(
                figure.ToString(CultureInfo.InvariantCulture), body, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A role that holds <c>Dashboard.ViewFinancials</c> is answered the contract unchanged: every
    /// seeded figure, exactly as it is stored.
    /// </summary>
    [Theory]
    [InlineData(ApiFactory.ManagerObjectId)]
    [InlineData(ApiFactory.OwnerObjectId)]
    public async Task A_role_that_holds_the_financial_capability_is_answered_every_figure(string objectId)
    {
        await _factory.SeedAsync();

        var items = await ItemsAsync(objectId);
        var conflict = items.Single(item => item.GetProperty("saleId").GetInt64() == ApiFactory.ConflictSaleId);

        Assert.Equal(ApiFactory.ConflictSettlementValue, conflict.GetProperty("settlementValue").GetDecimal());
        Assert.Equal(
            ApiFactory.ProviderSettlementValue,
            conflict.GetProperty("latestComparison").GetProperty("providerSettlementValue").GetDecimal());

        var decided = Assert.Single(await ItemsAsync(objectId, view: 20));
        Assert.Equal(ApiFactory.DecidedSettlementValue, decided.GetProperty("settlementValue").GetDecimal());
        Assert.Equal(
            ApiFactory.ReconciledPeriodDifference,
            Assert.Single(decided.GetProperty("history").EnumerateArray())
                .GetProperty("reconciledPeriodDifference")
                .GetDecimal());
    }

    /// <summary>
    /// Withholding the amounts changes nothing else. Every other key of every row - the sale's
    /// identity, its state, whether it is counted, its evidence, its history and the decisions the
    /// table allows for it - is byte-identical to the one a Manager is given over the same list, as
    /// are the view's own paging figures.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    public async Task An_operator_is_given_the_same_non_financial_response_as_a_manager(int view)
    {
        await _factory.SeedAsync();
        var uri = $"{ListUri}?view={view.ToString(CultureInfo.InvariantCulture)}&pageIndex=0&pageSize=1";

        var asManager = await PageAsync(ApiFactory.ManagerObjectId, uri);
        var asOperator = await PageAsync(ApiFactory.OperatorObjectId, uri);

        foreach (var paging in new[] { "totalCount", "pageIndex", "pageSize" })
        {
            Assert.Equal(asManager.GetProperty(paging).GetRawText(), asOperator.GetProperty(paging).GetRawText());
        }

        var managerItems = asManager.GetProperty("items").EnumerateArray().ToList();
        var operatorItems = asOperator.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(managerItems.Count, operatorItems.Count);
        Assert.NotEmpty(managerItems);

        var withheldValuesSeen = 0;
        for (var index = 0; index < managerItems.Count; index++)
        {
            AssertSameApartFromAmounts(managerItems[index], operatorItems[index], ref withheldValuesSeen);
        }

        Assert.True(withheldValuesSeen > 0, "The unredacted page carried no withheld figure to compare.");
    }

    /// <summary>
    /// The second page of a one-sale page is still the second sale, for both roles: paging is a
    /// property of the view and never of what the caller may be shown.
    /// </summary>
    [Fact]
    public async Task Paging_the_needs_review_list_answers_both_roles_the_same_sale()
    {
        await _factory.SeedAsync();
        const string uri = $"{ListUri}?view=10&pageIndex=1&pageSize=1";

        var asManager = await PageAsync(ApiFactory.ManagerObjectId, uri);
        var asOperator = await PageAsync(ApiFactory.OperatorObjectId, uri);

        Assert.Equal(2, asOperator.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            Assert.Single(asManager.GetProperty("items").EnumerateArray()).GetProperty("saleId").GetInt64(),
            Assert.Single(asOperator.GetProperty("items").EnumerateArray()).GetProperty("saleId").GetInt64());
    }

    /// <summary>
    /// The Dashboard's needs-attention summary counts sales rather than money, so every role is
    /// answered the same two counts and the response carries no amount to withhold.
    /// </summary>
    [Fact]
    public async Task The_summary_counts_are_the_same_for_every_role_and_carry_no_amount()
    {
        await _factory.SeedAsync();

        var asOperator = await BodyAsync(ApiFactory.OperatorObjectId, SummaryUri);
        var asManager = await BodyAsync(ApiFactory.ManagerObjectId, SummaryUri);

        Assert.Equal(asManager, asOperator);
        var summary = JsonDocument.Parse(asOperator).RootElement;
        Assert.Equal(["needsReviewCount", "notCountedCount"], Properties(summary));
        Assert.Equal(2, summary.GetProperty("needsReviewCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("notCountedCount").GetInt32());

        foreach (var figure in WithheldFigures)
        {
            Assert.DoesNotContain(
                figure.ToString(CultureInfo.InvariantCulture), asOperator, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Who may decide is untouched by this change (issue #1105's rules stay): the decision endpoint
    /// still refuses a Manager and an Operator, who hold no <c>Data.Repair</c>.
    /// </summary>
    [Theory]
    [InlineData(ApiFactory.ManagerObjectId)]
    [InlineData(ApiFactory.OperatorObjectId)]
    public async Task The_decision_endpoint_still_refuses_a_role_without_data_repair(string objectId)
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(objectId).PostAsJsonAsync(
            $"{ListUri}/{ApiFactory.ConflictSaleId.ToString(CultureInfo.InvariantCulture)}/decision",
            new
            {
                presentedState = (int)NayaxSqsVerification.Conflict,
                presentedLatestEvidenceId = ApiFactory.ComparisonEvidenceId,
                evidenceId = ApiFactory.ComparisonEvidenceId,
                decision = (int)NayaxSaleOwnerDecision.KeepSqsValues,
                reason = "A reason",
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// One row, key by key: an amount is <c>null</c> for the Operator and everything else is the
    /// Manager's raw JSON, however deeply it is nested.
    /// </summary>
    private static void AssertSameApartFromAmounts(
        JsonElement expected, JsonElement actual, ref int withheldValuesSeen)
    {
        foreach (var property in expected.EnumerateObject())
        {
            var mine = actual.GetProperty(property.Name);

            if (property.Name is "settlementValue" or "providerSettlementValue" or "reconciledPeriodDifference")
            {
                Assert.Equal(JsonValueKind.Null, mine.ValueKind);
                if (property.Value.ValueKind != JsonValueKind.Null)
                {
                    withheldValuesSeen++;
                }

                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                AssertSameApartFromAmounts(property.Value, mine, ref withheldValuesSeen);
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                var expectedRows = property.Value.EnumerateArray().ToList();
                var actualRows = mine.EnumerateArray().ToList();
                Assert.Equal(expectedRows.Count, actualRows.Count);
                for (var index = 0; index < expectedRows.Count; index++)
                {
                    AssertSameApartFromAmounts(expectedRows[index], actualRows[index], ref withheldValuesSeen);
                }

                continue;
            }

            Assert.Equal(property.Value.GetRawText(), mine.GetRawText());
        }
    }

    private async Task<List<JsonElement>> ItemsAsync(string objectId, int view = 10) =>
        [.. (await PageAsync(objectId, $"{ListUri}?view={view.ToString(CultureInfo.InvariantCulture)}"))
            .GetProperty("items")
            .EnumerateArray()];

    private async Task<JsonElement> PageAsync(string objectId, string uri) =>
        JsonDocument.Parse(await BodyAsync(objectId, uri)).RootElement;

    private async Task<string> BodyAsync(string objectId, string uri)
    {
        var response = await _factory.Authenticated(objectId).GetAsync(uri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsStringAsync();
    }

    private static string[] Properties(JsonElement element) =>
        [.. element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string DirectoryTenantId = "77777777-7777-7777-7777-777777777777";
        public const string OwnerObjectId = "11111111-1111-1111-1111-111111111111";
        public const string ManagerObjectId = "22222222-2222-2222-2222-222222222222";
        public const string OperatorObjectId = "33333333-3333-3333-3333-333333333333";

        public const long MachineId = 4200001;
        public const long ConflictSaleId = 9001;
        public const long InvestigationSaleId = 9002;
        public const long DecidedSaleId = 9003;
        public const long ComparisonEvidenceId = 8001;

        /// <summary>
        /// The seeded amounts. Deliberately distinctive, so "this figure is nowhere in the body" is
        /// an exact substring search rather than a search for a digit that could be an id or a date.
        /// </summary>
        public const decimal ConflictSettlementValue = 1234.51m;
        public const decimal InvestigationSettlementValue = 2345.62m;
        public const decimal DecidedSettlementValue = 3456.73m;
        public const decimal ProviderSettlementValue = 4567.84m;
        public const decimal ReconciledPeriodDifference = 567.95m;

        private const int BusinessId = 1;

        private static readonly DateTime CreatedAt = new(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime SaleInstant = new(2026, 11, 5, 2, 42, 17, DateTimeKind.Utc);

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

        /// <summary>
        /// One business, the three roles, and three queue-delivered sales: one Nayax's own record
        /// contradicted on the amount, one still needing investigation, and one an Owner has already
        /// decided, whose audited decision moved a reimbursement period.
        /// </summary>
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
                SalesProvider = ExternalProvider.Nayax,
                CreatedAtUtc = CreatedAt,
            });

            db.BusinessMemberships.AddRange(
                Membership(OwnerObjectId, BusinessRole.Owner),
                Membership(ManagerObjectId, BusinessRole.Manager),
                Membership(OperatorObjectId, BusinessRole.Operator));

            db.NayaxSales.AddRange(
                Sale(ConflictSaleId, 4100000001, ConflictSettlementValue, NayaxSqsVerification.Conflict, SaleInstant),
                Sale(
                    InvestigationSaleId,
                    4100000002,
                    InvestigationSettlementValue,
                    NayaxSqsVerification.NeedsInvestigation,
                    SaleInstant.AddMinutes(-10)),
                Sale(
                    DecidedSaleId,
                    4100000003,
                    DecidedSettlementValue,
                    NayaxSqsVerification.OwnerConfirmed,
                    SaleInstant.AddMinutes(-20)));

            db.NayaxSaleProviderEvidences.AddRange(
                new NayaxSaleProviderEvidence
                {
                    Id = ComparisonEvidenceId,
                    BusinessId = BusinessId,
                    NayaxSaleId = ConflictSaleId,
                    RunId = new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    ObservedAtUtc = SaleInstant.AddHours(1),
                    Kind = NayaxSaleProviderEvidenceKind.Comparison,
                    ResultingState = NayaxSqsVerification.Conflict,
                    ProviderMachineId = MachineId,
                    ProviderSettlementValue = ProviderSettlementValue,
                    ProviderAuthorizationInstantUtc = SaleInstant,
                    ProviderCurrencyCode = "AUD",
                    ProviderProductName = "Fictional Spring Water 600ml",
                    MachineComparison = NayaxSaleFieldComparison.Match,
                    AmountComparison = NayaxSaleFieldComparison.Mismatch,
                    InstantComparison = NayaxSaleFieldComparison.Match,
                    ProductComparison = NayaxSaleFieldComparison.NotCompared,
                    SaleAuthorizationInstantUtc = SaleInstant,
                    Bracketed = true,
                    OutsideReceiptGrace = true,
                },
                new NayaxSaleProviderEvidence
                {
                    Id = ComparisonEvidenceId + 1,
                    BusinessId = BusinessId,
                    NayaxSaleId = InvestigationSaleId,
                    RunId = new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                    ObservedAtUtc = SaleInstant.AddHours(1),
                    Kind = NayaxSaleProviderEvidenceKind.Absence,
                    ResultingState = NayaxSqsVerification.NeedsInvestigation,
                    ReadMachineId = MachineId,
                    ReturnedItemCount = 12,
                    ReturnedEarliestInstantUtc = SaleInstant.AddHours(-1),
                    ReturnedLatestInstantUtc = SaleInstant.AddHours(1),
                    SaleAuthorizationInstantUtc = SaleInstant.AddMinutes(-10),
                    Bracketed = true,
                    OutsideReceiptGrace = true,
                },
                new NayaxSaleProviderEvidence
                {
                    Id = ComparisonEvidenceId + 2,
                    BusinessId = BusinessId,
                    NayaxSaleId = DecidedSaleId,
                    RunId = new Guid("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                    ObservedAtUtc = SaleInstant.AddHours(1),
                    Kind = NayaxSaleProviderEvidenceKind.Comparison,
                    ResultingState = NayaxSqsVerification.Conflict,
                    ProviderMachineId = MachineId,
                    ProviderSettlementValue = ProviderSettlementValue,
                    ProviderAuthorizationInstantUtc = SaleInstant.AddMinutes(-20),
                    MachineComparison = NayaxSaleFieldComparison.Match,
                    AmountComparison = NayaxSaleFieldComparison.Mismatch,
                    InstantComparison = NayaxSaleFieldComparison.Match,
                    ProductComparison = NayaxSaleFieldComparison.NotCompared,
                    SaleAuthorizationInstantUtc = SaleInstant.AddMinutes(-20),
                    Bracketed = true,
                    OutsideReceiptGrace = true,
                });

            // The Owner's audited decision about the decided sale, which moved a reimbursement
            // period that had already been reconciled.
            db.NayaxSaleOwnerDecisionAudits.Add(new NayaxSaleOwnerDecisionAudit
            {
                BusinessId = BusinessId,
                NayaxSaleId = DecidedSaleId,
                TransactionId = 4100000003,
                Kind = NayaxSaleOwnerDecisionAuditKind.Decision,
                Decision = NayaxSaleOwnerDecision.KeepSqsValues,
                EvidenceId = ComparisonEvidenceId + 2,
                Reason = "The till receipt matches the stored sale.",
                ActorDirectoryTenantId = DirectoryTenantId,
                ActorObjectId = OwnerObjectId,
                OccurredAtUtc = SaleInstant.AddHours(2),
                PreviousState = NayaxSqsVerification.Conflict,
                ResultingState = NayaxSqsVerification.OwnerConfirmed,
                PreviousCountedAmount = 0m,
                ResultingCountedAmount = DecidedSettlementValue,
                ReconciledPeriodFrom = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                ReconciledPeriodTo = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc),
                ReconciledPeriodDifference = ReconciledPeriodDifference,
            });

            await db.SaveChangesAsync();
            _seeded = true;
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

        private static NayaxSales Sale(
            long id, long transactionId, decimal settlementValue, NayaxSqsVerification state, DateTime authorizedAt) =>
            new()
            {
                Id = id,
                BusinessId = BusinessId,
                TransactionID = transactionId,
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                MachineID = MachineId,
                MachineName = "Pavillion Left",
                SettlementValue = settlementValue,
                PaymentMethod = "Credit Card",
                ProductName = "Fictional Spring Water 600ml",
                MachineAuthorizationTime = authorizedAt,
                ReceivedViaSqsAtUtc = authorizedAt.AddSeconds(3),
                SqsVerification = state,
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
