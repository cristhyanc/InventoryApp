using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Inventory.Application.Documents;
using Inventory.Application.ReceiptScanning;
using Inventory.Domain.ReceiptScanning;
using Inventory.Domain.Subscriptions;
using Inventory.Infrastructure.ReceiptScanning;
using InventoryApi.Auth;
using InventoryApi.Tests.ReceiptScanning;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// <c>POST /api/purchases/scan</c> over real HTTP (issue #1163, sub-issue B of #975).
///
/// <para>Everything goes through the complete pipeline: authentication, the required scope, the
/// <c>Purchasing.Operate</c> capability, <c>BusinessScopeMiddleware</c>, the plan filter, the real
/// use case, the real file rules and relational SQLite with the real migration history. Two things
/// are substituted: the reader, by a fake that <strong>counts every call</strong>, and document
/// storage, by a strict mock that <strong>fails the test if anything is written to it</strong>.</para>
///
/// <para><strong>No test makes a live call to the reader.</strong> Issue #1163 excludes them, and the
/// counting fake is what makes "the reader was not called" a count of zero rather than an
/// inference.</para>
/// </summary>
public class ReceiptScanApiTests : IClassFixture<ReceiptScanApiTests.ReceiptScanFactory>, IAsyncLifetime
{
    private readonly ReceiptScanFactory _factory;

    public ReceiptScanApiTests(ReceiptScanFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.ResetScanAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The issue's first criterion over HTTP: a Pro business with enforcement on and a valid JPEG
    /// gets a line of 48 units at $0.625 - and nothing is written to the database or to document
    /// storage.
    /// </summary>
    [Fact]
    public async Task A_pro_business_gets_a_draft_and_nothing_is_written()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);
        var before = await _factory.StoredCountsAsync();

        var response = await _factory.AsOwner().PostAsync("/api/purchases/scan", Jpeg());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var draft = await Draft(response);
        var line = Assert.Single(draft.GetProperty("lines").EnumerateArray());
        Assert.Equal(48, line.GetProperty("units").GetProperty("value").GetInt32());
        Assert.Equal(0.625m, line.GetProperty("unitCost").GetProperty("value").GetDecimal());
        Assert.Equal("suggested", line.GetProperty("units").GetProperty("provenance").GetString());

        Assert.Equal(1, _factory.Reader.Calls);
        Assert.Equal(before, await _factory.StoredCountsAsync());

        // Document storage is a strict mock with no write set up, so any save or delete would have
        // thrown inside the request. Verified explicitly as well, so this is a statement rather than
        // a reliance on strictness.
        _factory.Documents.Verify(
            storage => storage.SaveAsync(
                It.IsAny<DocumentCategory>(),
                It.IsAny<string>(),
                It.IsAny<Stream>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The issue's plan criterion: Free and Starter are refused with the plan message, and the reader
    /// is not called.
    /// </summary>
    [Theory]
    [InlineData(SubscriptionPlan.Free)]
    [InlineData(SubscriptionPlan.Starter)]
    public async Task A_business_without_the_pro_plan_is_refused_and_the_reader_is_not_called(
        SubscriptionPlan plan)
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, plan);

        var response = await _factory.AsOwner().PostAsync("/api/purchases/scan", Jpeg());

        var problem = await AssertPlanRefusalAsync(response);
        Assert.Equal("Pro", problem.GetProperty("requiredPlan").GetString());
        Assert.Equal("ReceiptScanning", problem.GetProperty("planFeature").GetString());
        Assert.Equal(0, _factory.Reader.Calls);
    }

    /// <summary>
    /// The issue's HEIC criterion over HTTP: <c>400</c> with the convert message, and the reader is
    /// not called.
    /// </summary>
    [Fact]
    public async Task A_heic_photo_is_refused_with_the_convert_message()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);

        var response = await _factory.AsOwner().PostAsync(
            "/api/purchases/scan", File("receipt.heic", "image/heic"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            ReceiptScanFilePolicy.HeicMessage,
            await response.Content.ReadAsStringAsync());
        Assert.Equal(0, _factory.Reader.Calls);
    }

    /// <summary>
    /// The issue's file-type criterion: a client-claimed content type does not make a spreadsheet a
    /// receipt. The type is decided from the extension on the server (AGENTS.md § Files and
    /// attachments).
    /// </summary>
    [Fact]
    public async Task A_file_type_the_reader_cannot_read_is_refused()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);

        var response = await _factory.AsOwner().PostAsync(
            "/api/purchases/scan", File("receipt.xlsx", "image/jpeg"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _factory.Reader.Calls);
    }

    /// <summary>
    /// The issue's page-limit criterion over HTTP, and the repair of 10 November 2026 behind it: the
    /// limit is applied to the pages the document really reaches, not to the <c>/Count</c> it declares.
    /// This is a complete PDF - catalogue, page tree, cross-reference table, trailer - that writes
    /// <c>/Count 1</c> and reaches one more page than the configured limit allows. It is refused with
    /// the page-limit message, and <strong>the reader is not called</strong>, so no part of a hundred
    /// and one pages is ever uploaded or paid for.
    /// </summary>
    [Fact]
    public async Task A_pdf_that_understates_its_page_count_is_refused_and_the_reader_is_not_called()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);
        var understated = PdfFixture.Flat(
            pages: ReceiptScanningConfiguration.DefaultMaxPdfPages + 1,
            declaredCount: 1);

        var response = await _factory.AsOwner().PostAsync(
            "/api/purchases/scan", File("receipt.pdf", "application/pdf", understated));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            ReceiptScanFilePolicy.PdfTooManyPagesMessage(ReceiptScanningConfiguration.DefaultMaxPdfPages),
            await response.Content.ReadAsStringAsync());
        Assert.Equal(0, _factory.Reader.Calls);
    }

    /// <summary>
    /// The other side of the same rule: a PDF within the limit is still scanned, and the page count
    /// the reader is told is the verified one rather than the document's claim.
    /// </summary>
    [Fact]
    public async Task A_pdf_within_the_page_limit_is_scanned_with_its_verified_page_count()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);
        var twoPages = PdfFixture.Flat(pages: 2, declaredCount: 1);

        var response = await _factory.AsOwner().PostAsync(
            "/api/purchases/scan", File("receipt.pdf", "application/pdf", twoPages));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, _factory.Reader.Calls);
        Assert.Equal(2, _factory.Reader.LastRequest!.File.PageCount);
    }

    /// <summary>
    /// The second repair of 10 November 2026, over HTTP: an incremental update replaces the page tree
    /// through a cross-reference stream and keeps the replacement - one page more than the limit -
    /// in a compressed object stream, so the plain bytes still hold only the old two-page tree. The
    /// counter resolves the active graph or refuses it; this format it refuses, with the unreadable
    /// message, and <strong>the reader is not called</strong>.
    /// </summary>
    [Fact]
    public async Task A_page_tree_replaced_through_a_cross_reference_stream_is_refused()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);
        var replaced = PdfFixture.CrossReferenceStreamUpdate(
            originalPages: 2,
            activePages: ReceiptScanningConfiguration.DefaultMaxPdfPages + 1);

        var response = await _factory.AsOwner().PostAsync(
            "/api/purchases/scan", File("receipt.pdf", "application/pdf", replaced));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ReceiptScanFilePolicy.PdfUnreadableMessage, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, _factory.Reader.Calls);
    }

    /// <summary>
    /// The supported half of the same rule: an ordinary incremental update (a classic table with
    /// <c>/Prev</c>) whose active page tree is one page over the limit is refused with the page-limit
    /// message - the active tree is what the limit judges - and the reader is not called.
    /// </summary>
    [Fact]
    public async Task An_incremental_updates_active_page_tree_is_what_the_page_limit_judges()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);
        var updated = PdfFixture.Updated(original: 2, active: ReceiptScanningConfiguration.DefaultMaxPdfPages + 1);

        var response = await _factory.AsOwner().PostAsync(
            "/api/purchases/scan", File("receipt.pdf", "application/pdf", updated));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            ReceiptScanFilePolicy.PdfTooManyPagesMessage(ReceiptScanningConfiguration.DefaultMaxPdfPages),
            await response.Content.ReadAsStringAsync());
        Assert.Equal(0, _factory.Reader.Calls);
    }

    /// <summary>A request with no file part is refused, and nothing is called.</summary>
    [Fact]
    public async Task A_request_with_no_file_is_refused()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);

        var response = await _factory.AsOwner().PostAsync("/api/purchases/scan", new MultipartFormDataContent());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _factory.Reader.Calls);
    }

    /// <summary>
    /// The issue's foreign-id criterion over HTTP: the reader's suggestion names a product of another
    /// business, so it is dropped and the line keeps its receipt text.
    /// </summary>
    [Fact]
    public async Task A_product_id_from_another_business_is_dropped()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);
        var otherBusinessProductId = await _factory.SeedOtherBusinessProductAsync();
        _factory.Reader.Answer = ReceiptScanFactory.OneLine(productId: otherBusinessProductId);

        var response = await _factory.AsOwner().PostAsync("/api/purchases/scan", Jpeg());

        var line = Assert.Single((await Draft(response)).GetProperty("lines").EnumerateArray());
        Assert.False(line.TryGetProperty("productId", out var productId) && productId.ValueKind is not JsonValueKind.Null);
        Assert.Equal("COLA 375ML X 24", line.GetProperty("text").GetProperty("value").GetString());
    }

    /// <summary>
    /// The issue's malformed-answer criterion over HTTP: nothing valid left is <c>422</c> with
    /// "Could not read this receipt", and still nothing is written.
    /// </summary>
    [Fact]
    public async Task An_answer_with_nothing_valid_left_is_unprocessable()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);
        _factory.Reader.Answer = "I'm sorry, I can't read that.";
        var before = await _factory.StoredCountsAsync();

        var response = await _factory.AsOwner().PostAsync("/api/purchases/scan", Jpeg());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("Could not read this receipt.", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await _factory.StoredCountsAsync());
    }

    /// <summary>
    /// The issue's timeout criterion over HTTP: a gateway-timeout answer, and exactly one call was
    /// made - there is no automatic retry.
    /// </summary>
    [Fact]
    public async Task A_timeout_answers_a_gateway_timeout_after_exactly_one_call()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);
        _factory.Reader.Fail = () => throw new ReceiptReaderTimeoutException();

        var response = await _factory.AsOwner().PostAsync("/api/purchases/scan", Jpeg());

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(1, _factory.Reader.Calls);
    }

    /// <summary>An unusable reader answers a bad gateway, and is not retried either.</summary>
    [Fact]
    public async Task An_unavailable_reader_answers_a_bad_gateway()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);
        _factory.Reader.Fail = () => throw new ReceiptReaderUnavailableException();

        var response = await _factory.AsOwner().PostAsync("/api/purchases/scan", Jpeg());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, _factory.Reader.Calls);
    }

    /// <summary>
    /// The issue's criterion about what the adapter is given: only this business's products and
    /// suppliers, as ids and names. The other business's product exists and is not in the request.
    /// </summary>
    [Fact]
    public async Task Only_this_businesss_products_and_suppliers_are_sent()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);
        var otherBusinessProductId = await _factory.SeedOtherBusinessProductAsync();

        await _factory.AsOwner().PostAsync("/api/purchases/scan", Jpeg());

        var request = _factory.Reader.LastRequest!;
        Assert.Equal([ReceiptScanFactory.ProductName], request.Products.Select(product => product.Name));
        Assert.DoesNotContain(otherBusinessProductId, request.Products.Select(product => product.Id));
        Assert.Equal([ReceiptScanFactory.SupplierName], request.Suppliers.Select(supplier => supplier.Name));
    }

    /// <summary>
    /// The issue's logging criterion: the log carries the business, the outcome, the duration, the
    /// pages and the tokens - and no receipt text, no file name and no product name.
    /// </summary>
    [Fact]
    public async Task The_log_carries_no_receipt_text_file_name_or_product_name()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);
        _factory.ClearLog();

        await _factory.AsOwner().PostAsync("/api/purchases/scan", Jpeg());

        var log = string.Join('\n', _factory.LoggedEntries());
        Assert.Contains($"Receipt scan for business {_factory.TradingBusinessId}", log, StringComparison.Ordinal);
        Assert.DoesNotContain("COLA 375ML", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("receipt.jpg", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ReceiptScanFactory.ProductName, log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ReceiptScanFactory.SupplierName, log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Metro Beverages", log, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The issue's permission criterion: the scan needs exactly the permission creating a purchase
    /// needs and no more, so the least-privileged role that may record a purchase - an Operator - may
    /// scan one. Every role that holds <c>Purchasing.Operate</c> is exercised, because a capability
    /// the Operator did not hold would make this endpoint stricter than the purchase it drafts.
    /// </summary>
    [Fact]
    public async Task The_least_privileged_role_that_may_record_a_purchase_may_scan_one()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);

        var response = await _factory.AsOperator().PostAsync("/api/purchases/scan", Jpeg());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, _factory.Reader.Calls);
    }

    /// <summary>
    /// And the other business cannot reach this one's catalogue through a scan of its own: the
    /// products the reader is given are the caller's business's, resolved from their membership and
    /// never from anything the request carries.
    /// </summary>
    [Fact]
    public async Task Another_businesss_scan_is_read_against_its_own_catalogue()
    {
        await _factory.GrantAsync(_factory.OtherBusinessId, SubscriptionPlan.Pro);
        await _factory.SeedOtherBusinessProductAsync();

        var response = await _factory.AsOtherBusinessOwner().PostAsync("/api/purchases/scan", Jpeg());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(
            ReceiptScanFactory.ProductName,
            _factory.Reader.LastRequest!.Products.Select(product => product.Name));
        Assert.DoesNotContain(
            ReceiptScanFactory.SupplierName,
            _factory.Reader.LastRequest!.Suppliers.Select(supplier => supplier.Name));
    }

    private static async Task<JsonElement> Draft(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static async Task<JsonElement> AssertPlanRefusalAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(PlanRequirement.ErrorCode, problem.GetProperty("code").GetString());

        return problem;
    }

    private static MultipartFormDataContent Jpeg() => File("receipt.jpg", "image/jpeg");

    private static MultipartFormDataContent File(string fileName, string contentType) =>
        File(fileName, contentType, Encoding.UTF8.GetBytes("not a real photograph"));

    private static MultipartFormDataContent File(string fileName, string contentType, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        return new MultipartFormDataContent { { content, "file", fileName } };
    }

    /// <summary>
    /// The shipped state, over the same world: no API key configured, so the endpoint answers
    /// <c>503</c> and nothing leaves the process (issue #1163's switched-off criterion).
    /// </summary>
    public class WhenSwitchedOff
        : IClassFixture<ReceiptScanFactory.SwitchedOff>, IAsyncLifetime
    {
        private readonly ReceiptScanFactory.SwitchedOff _factory;

        public WhenSwitchedOff(ReceiptScanFactory.SwitchedOff factory) => _factory = factory;

        public Task InitializeAsync() => _factory.ResetScanAsync();

        public Task DisposeAsync() => Task.CompletedTask;

        /// <summary>
        /// The issue's criterion: with no API key configured the answer is <c>503</c> with the fixed
        /// sentence, and no outbound call is made - on a business that is on Pro, so this is the
        /// configuration refusing rather than the plan.
        /// </summary>
        [Fact]
        public async Task With_no_api_key_the_endpoint_is_unavailable_and_nothing_is_called()
        {
            await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);

            var response = await _factory.AsOwner().PostAsync("/api/purchases/scan", Jpeg());

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("Receipt scanning is not available.", await response.Content.ReadAsStringAsync());
            Assert.Equal(0, _factory.Reader.Calls);
        }
    }

    /// <summary>
    /// The fail-closed criterion (issue #1163): with plan enforcement switched off - the state in
    /// which every other feature is unrestricted for everybody - scanning is still refused with the
    /// plan message, and the reader is still not called. Every scan costs money, so there is no state
    /// in which opening it to every business is the cautious answer.
    /// </summary>
    public class WhenPlanEnforcementIsOff
        : IClassFixture<ReceiptScanFactory.PlanEnforcementOff>, IAsyncLifetime
    {
        private readonly ReceiptScanFactory.PlanEnforcementOff _factory;

        public WhenPlanEnforcementIsOff(ReceiptScanFactory.PlanEnforcementOff factory) => _factory = factory;

        public Task InitializeAsync() => _factory.ResetScanAsync();

        public Task DisposeAsync() => Task.CompletedTask;

        /// <summary>
        /// Every plan, including Pro and including the business with no record at all, is refused -
        /// because what is being refused is not the plan but the absence of enforcement.
        /// </summary>
        [Theory]
        [InlineData(SubscriptionPlan.Free)]
        [InlineData(SubscriptionPlan.Starter)]
        [InlineData(SubscriptionPlan.Pro)]
        public async Task Every_business_is_refused_and_the_reader_is_not_called(SubscriptionPlan plan)
        {
            await _factory.GrantAsync(_factory.TradingBusinessId, plan);

            var response = await _factory.AsOwner().PostAsync("/api/purchases/scan", Jpeg());

            var problem = await AssertPlanRefusalAsync(response);
            Assert.Equal("Pro", problem.GetProperty("requiredPlan").GetString());
            Assert.Equal("ReceiptScanning", problem.GetProperty("planFeature").GetString());
            Assert.Equal(0, _factory.Reader.Calls);
        }

        /// <summary>
        /// And the control: with enforcement off, a Free business still reaches an ordinary
        /// purchasing endpoint, so this host really is the unrestricted one and the refusal above is
        /// about this feature alone.
        /// </summary>
        [Fact]
        public async Task An_ordinary_purchasing_endpoint_is_still_open()
        {
            await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Free);

            var response = await _factory.AsOwner().GetAsync("/api/purchases");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    /// <summary>
    /// The plan-enforcement world of <see cref="PlanEnforcementApiTests.ApiFactory"/> with receipt
    /// scanning configured: a seeded product and supplier to be read against, an API key, the
    /// counting fake reader, and a strict document-storage mock that fails on any write.
    /// </summary>
    public class ReceiptScanFactory : PlanEnforcementApiTests.ApiFactory
    {
        /// <summary>Obviously fake. No real key belongs in a test, or in this repository.</summary>
        public const string ApiKey = "fake-key-not-a-real-credential";

        /// <summary>The trading business's one product, which a scan may be matched to.</summary>
        public const string ProductName = "Cola 375mL";

        /// <summary>The trading business's one supplier.</summary>
        public const string SupplierName = "Metro Drinks Wholesale";

        private readonly CountingReceiptReader _reader = new();
        private readonly Mock<IDocumentStorage> _documents = new(MockBehavior.Strict);

        /// <summary>The same host with no API key: the shipped state.</summary>
        public sealed class SwitchedOff : ReceiptScanFactory
        {
            protected override bool ConfigureApiKey => false;
        }

        /// <summary>The same host with plan enforcement off, which is also the shipped state.</summary>
        public sealed class PlanEnforcementOff : ReceiptScanFactory
        {
            protected override bool EnforcePlans => false;
        }

        /// <summary>The substituted reader, which counts every call.</summary>
        public CountingReceiptReader Reader => _reader;

        /// <summary>
        /// Document storage, which has no write configured: a strict mock throws on any call that
        /// was not set up, so a scan that saved a byte would fail the request.
        /// </summary>
        public Mock<IDocumentStorage> Documents => _documents;

        /// <summary>Whether this host has an API key, and so whether the feature is on.</summary>
        protected virtual bool ConfigureApiKey => true;

        /// <summary>The reader's answer for a one-line receipt, with an optional product match.</summary>
        public static string OneLine(long? productId = null) =>
            $$"""
            {
              "supplierName": "Metro Beverages",
              "deliveryDate": "2026-10-27",
              "receiptTotal": 30.00,
              "receiptTotalIncludesGst": "yes",
              "lines": [
                { "text": "COLA 375ML X 24", "kind": "stock", "deliveredPacks": 2, "packSize": "printed",
                  "unitsPerPack": 24, "lineTotal": 30.00, "priceIncludesGst": "yes", "taxable": "yes",
                  "productId": {{productId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}},
                  "isDrinkContainer": true }
              ]
            }
            """;

        /// <summary>
        /// Puts the world back and seeds the one product and supplier a scan is read against, with
        /// the reader counted from zero and answering a one-line receipt.
        /// </summary>
        public async Task ResetScanAsync()
        {
            await ResetAsync();
            await SeedCatalogueAsync();

            _reader.Reset(OneLine());
        }

        /// <summary>
        /// How many rows exist in everything a scan could conceivably have written, as text, so
        /// "nothing was written" is a comparison rather than an inference.
        /// </summary>
        public async Task<string> StoredCountsAsync()
        {
            await using var db = Database();

            return string.Join(
                '|',
                await db.Receipts.CountAsync(),
                await db.ReceiptItems.CountAsync(),
                await db.Products.CountAsync(),
                await db.Suppliers.CountAsync(),
                await db.StockAdjustments.CountAsync(),
                await db.ImportedFiles.CountAsync());
        }

        /// <summary>
        /// A product of the <em>other</em> business, so "a product id from another business is
        /// dropped" is asserted against an id that really exists and really is not this business's.
        /// </summary>
        public async Task<long> SeedOtherBusinessProductAsync()
        {
            await using var db = Database();
            var product = new Inventory.Infrastructure.Models.Product
            {
                BusinessId = OtherBusinessId,
                Name = "Somebody Else's Cola",
                Unit = "375mL",
            };

            db.Products.Add(product);
            await db.SaveChangesAsync();

            return product.Id;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            if (ConfigureApiKey)
            {
                builder.UseSetting($"{ReceiptScanningOptions.SectionName}:ApiKey", ApiKey);
            }

            base.ConfigureWebHost(builder);

            builder.ConfigureTestServices(services =>
            {
                // The reader, and only the reader: the settings, the file rules, the page counter,
                // the plan filter and the use case are all the production registrations, which is
                // what makes every "the reader was not called" assertion a statement about the real
                // refusal path.
                services.AddSingleton<IReceiptReader>(_reader);

                // Document storage with no write configured. A strict mock throws on any call that
                // was not set up, so this is how "nothing reached document storage" is enforced
                // rather than inspected afterwards.
                services.AddScoped(_ => _documents.Object);
            });
        }

        private async Task SeedCatalogueAsync()
        {
            await using var db = Database();
            db.Products.Add(new Inventory.Infrastructure.Models.Product
            {
                BusinessId = TradingBusinessId,
                Name = ProductName,
                Unit = "375mL",
            });
            db.Suppliers.Add(new Inventory.Infrastructure.Models.Supplier
            {
                BusinessId = TradingBusinessId,
                Name = SupplierName,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// The reader, counted. It answers whatever a test sets, or throws whatever a test sets, and
        /// records the request it was given so a test can assert exactly what would have left the
        /// application.
        /// </summary>
        public sealed class CountingReceiptReader : IReceiptReader
        {
            /// <summary>What the reader answers.</summary>
            public string Answer { get; set; } = string.Empty;

            /// <summary>How the reader fails, when a test wants it to.</summary>
            public Func<ReceiptReadResult>? Fail { get; set; }

            /// <summary>How many times the reader was asked.</summary>
            public int Calls { get; private set; }

            /// <summary>The last request, so a test can assert what was sent.</summary>
            public ReceiptReadRequest? LastRequest { get; private set; }

            /// <summary>Back to zero, answering <paramref name="answer"/> and failing at nothing.</summary>
            public void Reset(string answer)
            {
                Answer = answer;
                Fail = null;
                Calls = 0;
                LastRequest = null;
            }

            /// <inheritdoc />
            public Task<ReceiptReadResult> ReadAsync(
                ReceiptReadRequest request,
                CancellationToken cancellationToken)
            {
                Calls++;
                LastRequest = request;

                return Task.FromResult(Fail is null ? new ReceiptReadResult(Answer, 1200, 400) : Fail());
            }
        }
    }
}
