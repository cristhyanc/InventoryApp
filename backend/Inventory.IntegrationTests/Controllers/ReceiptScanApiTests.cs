using System.Globalization;
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

    #region A pack size suggested from the last purchase (issue #1165)

    /// <summary>
    /// Issue #1165's first criterion over HTTP: a carton the receipt printed no pack size for is
    /// suggested the 24 the business last paid this supplier for, marked as read from the last
    /// purchase with that purchase's date, with the units and unit cost filled in - and
    /// <strong>nothing is written</strong>, which is the same comparison the scan's own no-writes
    /// criterion makes.
    /// </summary>
    [Fact]
    public async Task A_missing_pack_size_is_suggested_from_the_last_purchase_and_nothing_is_written()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);
        await _factory.SeedPurchaseAsync(
            _factory.TradingBusinessId, _factory.ProductId, _factory.SupplierId, 1.2067m);
        _factory.Reader.Answer = ReceiptScanFactory.NoPackSize(_factory.ProductId, _factory.SupplierId);
        var before = await _factory.StoredCountsAsync();

        var line = await ScannedLine();

        Assert.Equal(24, line.GetProperty("unitsPerPack").GetProperty("value").GetInt32());
        Assert.Equal("suggested", line.GetProperty("unitsPerPack").GetProperty("provenance").GetString());
        Assert.Equal(24, line.GetProperty("units").GetProperty("value").GetInt32());
        Assert.Equal(28.96m / 24, line.GetProperty("unitCost").GetProperty("value").GetDecimal());
        Assert.Empty(line.GetProperty("missing").EnumerateArray());

        var suggestion = line.GetProperty("unitsPerPackSuggestion");
        Assert.Equal("lastPurchase", suggestion.GetProperty("suggestedFrom").GetString());
        Assert.Equal(Published(ReceiptScanFactory.LastPurchaseDate), SuggestedDate(line));

        Assert.Equal(before, await _factory.StoredCountsAsync());
        _factory.Documents.Verify(
            storage => storage.SaveAsync(
                It.IsAny<DocumentCategory>(),
                It.IsAny<string>(),
                It.IsAny<Stream>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Issue #1165's two-carton criterion over HTTP: $57.92 for two cartons is a pack size of 24 and
    /// 48 units - never 96.
    /// </summary>
    [Fact]
    public async Task Two_cartons_on_one_line_are_forty_eight_units()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);
        await _factory.SeedPurchaseAsync(
            _factory.TradingBusinessId, _factory.ProductId, _factory.SupplierId, 1.2067m);
        _factory.Reader.Answer = ReceiptScanFactory.NoPackSize(
            _factory.ProductId, _factory.SupplierId, packs: 2, lineTotal: 57.92m);

        var line = await ScannedLine();

        Assert.Equal(24, line.GetProperty("unitsPerPack").GetProperty("value").GetInt32());
        Assert.Equal(48, line.GetProperty("units").GetProperty("value").GetInt32());
    }

    /// <summary>
    /// Issue #1165's supplier-match criterion: the history that counts is this supplier's. The same
    /// product bought from another supplier of the same business at the same price suggests nothing,
    /// because another supplier's carton is not evidence about this one's.
    /// </summary>
    [Fact]
    public async Task A_purchase_from_another_supplier_suggests_nothing()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);
        var otherSupplierId = await _factory.SeedSecondSupplierAsync();
        await _factory.SeedPurchaseAsync(
            _factory.TradingBusinessId, _factory.ProductId, otherSupplierId, 1.2067m);
        _factory.Reader.Answer = ReceiptScanFactory.NoPackSize(_factory.ProductId, _factory.SupplierId);

        AssertNoSuggestion(await ScannedLine());
    }

    /// <summary>
    /// Issue #1165's no-history criterion: a product this business has never bought leaves the line
    /// missing its pack size and its unit cost, exactly as it was before this rule existed.
    /// </summary>
    [Fact]
    public async Task A_product_with_no_purchase_history_suggests_nothing()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);
        _factory.Reader.Answer = ReceiptScanFactory.NoPackSize(_factory.ProductId, _factory.SupplierId);

        var line = await ScannedLine();

        AssertNoSuggestion(line);
        Assert.Equal(
            ["unitsPerPack", "unitCost"],
            line.GetProperty("missing").EnumerateArray().Select(value => value.GetString()));
    }

    /// <summary>
    /// And its other half: with no supplier of this business suggested there is no history to read at
    /// all, however much of it the business has.
    /// </summary>
    [Fact]
    public async Task A_receipt_with_no_suggested_supplier_suggests_nothing()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);
        await _factory.SeedPurchaseAsync(
            _factory.TradingBusinessId, _factory.ProductId, _factory.SupplierId, 1.2067m);
        _factory.Reader.Answer = ReceiptScanFactory.NoPackSize(_factory.ProductId, supplierId: 4242);

        AssertNoSuggestion(await ScannedLine());
    }

    /// <summary>
    /// The most recent purchase from that supplier decides, and the date published is that purchase's
    /// own. The older line's $2.4133 would have suggested 12 for this receipt; the newer $1.2067
    /// suggests 24.
    /// </summary>
    [Fact]
    public async Task The_most_recent_purchase_from_that_supplier_decides()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);
        await _factory.SeedPurchaseAsync(
            _factory.TradingBusinessId,
            _factory.ProductId,
            _factory.SupplierId,
            2.4133m,
            ReceiptScanFactory.LastPurchaseDate.AddMonths(-3));
        await _factory.SeedPurchaseAsync(
            _factory.TradingBusinessId, _factory.ProductId, _factory.SupplierId, 1.2067m);
        _factory.Reader.Answer = ReceiptScanFactory.NoPackSize(_factory.ProductId, _factory.SupplierId);

        var line = await ScannedLine();

        Assert.Equal(24, line.GetProperty("unitsPerPack").GetProperty("value").GetInt32());
        Assert.Equal(Published(ReceiptScanFactory.LastPurchaseDate), SuggestedDate(line));
    }

    /// <summary>
    /// Issue #1165's "never overwrite" criterion over HTTP: the receipt printed 24, so 24 is used, it
    /// stays a quotation, and the $2.4133 history that would have suggested 12 changes nothing.
    /// </summary>
    [Fact]
    public async Task A_pack_size_printed_on_the_receipt_is_never_replaced()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);
        await _factory.SeedPurchaseAsync(
            _factory.TradingBusinessId, _factory.ProductId, _factory.SupplierId, 2.4133m);
        _factory.Reader.Answer = ReceiptScanFactory.PrintedPackSize(
            _factory.ProductId, _factory.SupplierId, unitsPerPack: 24);

        var line = await ScannedLine();

        Assert.Equal(24, line.GetProperty("unitsPerPack").GetProperty("value").GetInt32());
        Assert.Equal("read", line.GetProperty("unitsPerPack").GetProperty("provenance").GetString());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("unitsPerPackSuggestion").ValueKind);
    }

    /// <summary>
    /// Issue #1165's tenancy criterion, with two businesses: the other business really has bought
    /// this product from a supplier of that name at $1.2067, and the trading business's scan is
    /// suggested nothing - while the other business's own scan is suggested its own 24. A scan reads
    /// one business's purchases, resolved from the caller's membership (AGENTS.md § Tenant ownership
    /// and data isolation).
    /// </summary>
    [Fact]
    public async Task Only_the_callers_own_businesss_purchases_are_read()
    {
        await _factory.GrantAsync(_factory.TradingBusinessId, SubscriptionPlan.Pro);
        await _factory.GrantAsync(_factory.OtherBusinessId, SubscriptionPlan.Pro);
        var (otherProductId, otherSupplierId) = await _factory.SeedOtherBusinessHistoryAsync(1.2067m);

        _factory.Reader.Answer = ReceiptScanFactory.NoPackSize(_factory.ProductId, _factory.SupplierId);
        AssertNoSuggestion(await ScannedLine());

        _factory.Reader.Answer = ReceiptScanFactory.NoPackSize(otherProductId, otherSupplierId);
        var theirs = await ScannedLine(_factory.AsOtherBusinessOwner());
        Assert.Equal(24, theirs.GetProperty("unitsPerPack").GetProperty("value").GetInt32());
    }

    #endregion

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

    /// <summary>The one line of a scanned draft, over real HTTP.</summary>
    private async Task<JsonElement> ScannedLine(HttpClient? client = null)
    {
        var response = await (client ?? _factory.AsOwner()).PostAsync("/api/purchases/scan", Jpeg());
        var draft = await Draft(response);

        return Assert.Single(draft.GetProperty("lines").EnumerateArray());
    }

    /// <summary>The date a line's pack-size suggestion was read from, as published.</summary>
    private static string? SuggestedDate(JsonElement line) =>
        line.GetProperty("unitsPerPackSuggestion").GetProperty("purchaseDate").GetString();

    /// <summary>A calendar date as this API publishes one.</summary>
    private static string Published(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// The line as it was before issue #1165 existed: no pack size, no units, no unit cost, and no
    /// suggestion marker. Published as JSON <c>null</c> rather than omitted, as every other absent
    /// value on this contract is.
    /// </summary>
    private static void AssertNoSuggestion(JsonElement line)
    {
        Assert.Equal(JsonValueKind.Null, line.GetProperty("unitsPerPack").ValueKind);
        Assert.Equal(JsonValueKind.Null, line.GetProperty("units").ValueKind);
        Assert.Equal(JsonValueKind.Null, line.GetProperty("unitCost").ValueKind);
        Assert.Equal(JsonValueKind.Null, line.GetProperty("unitsPerPackSuggestion").ValueKind);
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

        /// <summary>A second supplier of the trading business, so "the same supplier" has a subject.</summary>
        public const string OtherSupplierName = "Corner Cash And Carry";

        /// <summary>
        /// The date of the purchase issue #1165's suggestions are read from, and the instant it is
        /// stored as. Deliberately a date rather than today's, so the published date is a statement.
        /// </summary>
        public static readonly DateOnly LastPurchaseDate = new(2026, 9, 14);

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

        /// <summary>The trading business's one product, once seeded.</summary>
        public long ProductId { get; private set; }

        /// <summary>The trading business's own supplier, once seeded.</summary>
        public int SupplierId { get; private set; }

        /// <summary>
        /// The reader's answer for a one-line receipt whose pack size the receipt did not print
        /// (issue #1165): one carton of a matched product from a matched supplier, for
        /// <paramref name="lineTotal"/> GST-inclusive.
        /// </summary>
        public static string NoPackSize(
            long productId,
            int supplierId,
            int packs = 1,
            decimal lineTotal = 28.96m) =>
            $$"""
            {
              "supplierName": "Metro Beverages",
              "supplierId": {{supplierId.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
              "deliveryDate": "2026-10-27",
              "receiptTotal": {{lineTotal.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
              "receiptTotalIncludesGst": "yes",
              "lines": [
                { "text": "45G CC'S TASTY CHEESE", "kind": "stock",
                  "deliveredPacks": {{packs.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                  "packSize": "notPrinted",
                  "lineTotal": {{lineTotal.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                  "priceIncludesGst": "yes", "taxable": "yes",
                  "productId": {{productId.ToString(System.Globalization.CultureInfo.InvariantCulture)}} }
              ]
            }
            """;

        /// <summary>
        /// The same receipt with a printed pack size, so "a pack size read from the receipt is never
        /// replaced" can be asserted against a history that would have said something else.
        /// </summary>
        public static string PrintedPackSize(long productId, int supplierId, int unitsPerPack) =>
            NoPackSize(productId, supplierId).Replace(
                "\"packSize\": \"notPrinted\"",
                $"\"packSize\": \"printed\", \"unitsPerPack\": {unitsPerPack.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                StringComparison.Ordinal);

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
            // Before the reset: a purchase line names its product and its business with restricted
            // foreign keys, so a purchase issue #1165's tests seeded has to go before the products
            // and businesses the reset removes.
            await ClearPurchasesAsync();
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

        /// <summary>
        /// One recorded purchase of <paramref name="productId"/> from <paramref name="supplierId"/>
        /// at <paramref name="unitCost"/> a unit (issue #1165), for the business that owns them. The
        /// quantity is deliberately not the pack size: what the suggestion reads is the unit cost,
        /// and a test that seeded 24 units would not show that.
        /// </summary>
        public async Task SeedPurchaseAsync(
            int businessId,
            long productId,
            int supplierId,
            decimal unitCost,
            DateOnly? purchaseDate = null)
        {
            await using var db = Database();
            var purchase = new Inventory.Infrastructure.Models.Purchase
            {
                BusinessId = businessId,
                Title = "Last delivery",
                SupplierId = supplierId,
                PurchaseDate = (purchaseDate ?? LastPurchaseDate).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            };

            purchase.Items.Add(new Inventory.Infrastructure.Models.PurchaseItem
            {
                BusinessId = businessId,
                ProductId = productId,
                Quantity = 6m,
                UnitCost = unitCost,
            });

            db.Receipts.Add(purchase);
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// The <em>other</em> business's own product, supplier and purchase history, so "only this
        /// business's purchases are read" is asserted against a history that really exists and really
        /// is not this business's (AGENTS.md § Tenant ownership and data isolation).
        /// </summary>
        public async Task<(long ProductId, int SupplierId)> SeedOtherBusinessHistoryAsync(decimal unitCost)
        {
            var product = new Inventory.Infrastructure.Models.Product
            {
                BusinessId = OtherBusinessId,
                Name = ProductName,
                Unit = "375mL",
            };
            var supplier = new Inventory.Infrastructure.Models.Supplier
            {
                BusinessId = OtherBusinessId,
                Name = SupplierName,
            };

            await using (var db = Database())
            {
                db.Products.Add(product);
                db.Suppliers.Add(supplier);
                await db.SaveChangesAsync();
            }

            await SeedPurchaseAsync(OtherBusinessId, product.Id, supplier.Id, unitCost);

            return (product.Id, supplier.Id);
        }

        /// <summary>
        /// A second supplier of the trading business, seeded on demand so the scans that assert what
        /// leaves the application still see exactly one supplier.
        /// </summary>
        public async Task<int> SeedSecondSupplierAsync()
        {
            await using var db = Database();
            var supplier = new Inventory.Infrastructure.Models.Supplier
            {
                BusinessId = TradingBusinessId,
                Name = OtherSupplierName,
            };

            db.Suppliers.Add(supplier);
            await db.SaveChangesAsync();

            return supplier.Id;
        }

        /// <summary>
        /// Every recorded purchase and purchase line, removed: the lines first, because a line names
        /// its purchase with a restricted foreign key.
        /// </summary>
        private async Task ClearPurchasesAsync()
        {
            await using var db = Database();
            db.ReceiptItems.RemoveRange(await db.ReceiptItems.ToListAsync());
            await db.SaveChangesAsync();
            db.Receipts.RemoveRange(await db.Receipts.ToListAsync());
            await db.SaveChangesAsync();
        }

        private async Task SeedCatalogueAsync()
        {
            await using var db = Database();
            var product = new Inventory.Infrastructure.Models.Product
            {
                BusinessId = TradingBusinessId,
                Name = ProductName,
                Unit = "375mL",
            };
            var supplier = new Inventory.Infrastructure.Models.Supplier
            {
                BusinessId = TradingBusinessId,
                Name = SupplierName,
            };
            db.Products.Add(product);
            db.Suppliers.Add(supplier);
            await db.SaveChangesAsync();

            ProductId = product.Id;
            SupplierId = supplier.Id;
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
