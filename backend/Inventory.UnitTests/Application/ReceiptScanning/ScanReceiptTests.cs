using System.Text;
using Inventory.Application.Purchases;
using Inventory.Application.ReceiptScanning;
using Inventory.Domain.Purchases;
using Inventory.Domain.ReceiptScanning;
using InventoryApi.Tests.Application.Gst;
using Xunit;

namespace InventoryApi.Tests.Application.ReceiptScanning;

/// <summary>
/// The scan use case (issue #1163): what is refused before the reader is reached, what one call
/// produces, and what the single log line records.
///
/// <para><strong>No test makes a live call.</strong> Issue #1163 excludes them, and every test here
/// runs against <see cref="FakeReceiptReader"/> - which is also what makes "the reader was not
/// called" a count rather than an inference.</para>
/// </summary>
public class ScanReceiptTests
{
    private const int BusinessId = 42;

    private static readonly string OneLine =
        """
        {
          "supplierName": "Metro Beverages",
          "deliveryDate": "2026-10-27",
          "receiptTotal": 30.00,
          "receiptTotalIncludesGst": "yes",
          "lines": [
            { "text": "COLA 375ML X 24", "kind": "stock", "deliveredPacks": 2, "packSize": "printed",
              "unitsPerPack": 24, "lineTotal": 30.00, "priceIncludesGst": "yes", "taxable": "yes",
              "productId": 91, "isDrinkContainer": true }
          ]
        }
        """;

    /// <summary>
    /// One carton whose pack size the receipt did not print, matched to this business's product and
    /// supplier - the receipt issue #1165 is about.
    /// </summary>
    private static readonly string NoPackSize =
        """
        {
          "supplierName": "Metro Beverages",
          "supplierId": 7,
          "deliveryDate": "2026-10-27",
          "receiptTotal": 28.96,
          "receiptTotalIncludesGst": "yes",
          "lines": [
            { "text": "45G CC'S TASTY CHEESE", "kind": "stock", "deliveredPacks": 1,
              "packSize": "notPrinted", "lineTotal": 28.96, "priceIncludesGst": "yes",
              "taxable": "yes", "productId": 91 }
          ]
        }
        """;

    /// <summary>
    /// The issue's first criterion through the use case: a valid photo becomes a draft whose line has
    /// 48 units at $0.625, and the suggested product is this business's own.
    /// </summary>
    [Fact]
    public async Task A_valid_photo_becomes_a_draft()
    {
        var reader = new FakeReceiptReader(OneLine);
        var result = await Scan(reader).Handle(Image(), CancellationToken.None);

        Assert.Equal(ScanReceiptOutcome.Draft, result.Outcome);
        var line = Assert.Single(result.Draft!.Lines);
        Assert.Equal(48, line.Units!.Value);
        Assert.Equal(0.625m, line.UnitCost!.Value);
        Assert.Equal(91, line.ProductId!.Value);
        Assert.Equal(1, reader.Calls);
    }

    /// <summary>
    /// The issue's switched-off criterion: with no API key the endpoint's use case refuses and
    /// <strong>no outbound call is made</strong> - and nothing is read about the business either.
    /// </summary>
    [Fact]
    public async Task With_no_api_key_nothing_is_called()
    {
        var reader = new FakeReceiptReader(OneLine);
        var result = await Scan(reader, settings: FakeReceiptScanSettings.SwitchedOff())
            .Handle(Image(), CancellationToken.None);

        Assert.Equal(ScanReceiptOutcome.SwitchedOff, result.Outcome);
        Assert.Equal("Receipt scanning is not available.", result.Message);
        Assert.Equal(0, reader.Calls);
    }

    /// <summary>
    /// The issue's HEIC criterion: refused with the convert message, and the reader is not called.
    /// </summary>
    [Fact]
    public async Task A_heic_photo_is_refused_and_the_reader_is_not_called()
    {
        var reader = new FakeReceiptReader(OneLine);
        var result = await Scan(reader).Handle(Image("receipt.heic"), CancellationToken.None);

        Assert.Equal(ScanReceiptOutcome.InvalidFile, result.Outcome);
        Assert.Equal(ReceiptScanFilePolicy.HeicMessage, result.Message);
        Assert.Equal(0, reader.Calls);
    }

    /// <summary>
    /// The issue's size criterion: a file over the configured limit is refused before the reader is
    /// reached. Both ceilings are exercised - the purchase document rules' 10 MB and the reader's own
    /// configured image limit - because a scan is judged by both.
    /// </summary>
    [Theory]
    [InlineData(PurchaseDocumentPolicy.MaxFileSizeBytes + 1)]
    [InlineData(2049)]
    [InlineData(0)]
    public async Task An_oversized_file_is_refused_and_the_reader_is_not_called(long length)
    {
        var reader = new FakeReceiptReader(OneLine);
        var settings = new FakeReceiptScanSettings { MaxImageBytes = 2048 };
        var result = await Scan(reader, settings: settings)
            .Handle(Image(length: length), CancellationToken.None);

        Assert.Equal(ScanReceiptOutcome.InvalidFile, result.Outcome);
        Assert.Equal(0, reader.Calls);
    }

    /// <summary>
    /// And its PDF half: a document over the configured page limit is refused before the reader is
    /// reached, which is the whole reason the pages are counted in process.
    /// </summary>
    [Fact]
    public async Task A_pdf_over_the_page_limit_is_refused_and_the_reader_is_not_called()
    {
        var reader = new FakeReceiptReader(OneLine);
        var settings = new FakeReceiptScanSettings { MaxPdfPages = 3 };
        var result = await Scan(reader, settings: settings, pages: new FakePdfPageCounter(4))
            .Handle(Image("receipt.pdf"), CancellationToken.None);

        Assert.Equal(ScanReceiptOutcome.InvalidFile, result.Outcome);
        Assert.Equal(ReceiptScanFilePolicy.PdfTooManyPagesMessage(3), result.Message);
        Assert.Equal(0, reader.Calls);
    }

    /// <summary>
    /// A file named <c>.pdf</c> that nothing could count is refused rather than uploaded whole and
    /// hoped about.
    /// </summary>
    [Fact]
    public async Task A_pdf_whose_pages_cannot_be_counted_is_refused()
    {
        var reader = new FakeReceiptReader(OneLine);
        var result = await Scan(reader, pages: new FakePdfPageCounter(null))
            .Handle(Image("receipt.pdf"), CancellationToken.None);

        Assert.Equal(ScanReceiptOutcome.InvalidFile, result.Outcome);
        Assert.Equal(ReceiptScanFilePolicy.PdfUnreadableMessage, result.Message);
        Assert.Equal(0, reader.Calls);
    }

    /// <summary>A request with no file at all is a refusal, not an empty scan.</summary>
    [Fact]
    public async Task A_request_with_no_file_is_refused()
    {
        var reader = new FakeReceiptReader(OneLine);
        var result = await Scan(reader).Handle(null, CancellationToken.None);

        Assert.Equal(ScanReceiptOutcome.InvalidFile, result.Outcome);
        Assert.Equal(ReceiptScanFilePolicy.MissingFileMessage, result.Message);
        Assert.Equal(0, reader.Calls);
    }

    /// <summary>
    /// The issue's malformed-answer criterion: an answer with nothing valid left in it is "could not
    /// read this receipt", and the request does not fail with anything else.
    /// </summary>
    [Theory]
    [InlineData("I'm sorry, I can't read that.")]
    [InlineData("{\"lines\": []}")]
    [InlineData("{\"lines\": [{\"text\": \"COLA\", \"kind\": \"groceries\"}]}")]
    public async Task An_answer_with_nothing_valid_left_is_an_unreadable_receipt(string json)
    {
        var result = await Scan(new FakeReceiptReader(json)).Handle(Image(), CancellationToken.None);

        Assert.Equal(ScanReceiptOutcome.Unreadable, result.Outcome);
        Assert.Equal("Could not read this receipt.", result.Message);
        Assert.Null(result.Draft);
    }

    /// <summary>
    /// And its other half: an answer with one bad line and one good one succeeds with the good one,
    /// rather than failing the request.
    /// </summary>
    [Fact]
    public async Task An_answer_with_one_bad_line_keeps_the_good_one()
    {
        var reader = new FakeReceiptReader(
            """
            { "lines": [
                { "text": "MYSTERY", "kind": "groceries" },
                { "text": "COLA 375ML X 24", "kind": "stock", "deliveredPacks": 2, "packSize": "printed",
                  "unitsPerPack": 24, "lineTotal": 30.00, "priceIncludesGst": "yes" }
            ] }
            """);

        var result = await Scan(reader).Handle(Image(), CancellationToken.None);

        Assert.Equal(ScanReceiptOutcome.Draft, result.Outcome);
        Assert.Equal("COLA 375ML X 24", Assert.Single(result.Draft!.Lines).Text.Value);
    }

    /// <summary>
    /// The issue's timeout criterion: a timeout is reported as its own outcome and
    /// <strong>exactly one call was made</strong> - there is no automatic retry, because a retried
    /// scan is a second payment.
    /// </summary>
    [Fact]
    public async Task A_timeout_is_reported_and_exactly_one_call_was_made()
    {
        var reader = FakeReceiptReader.TimingOut();
        var result = await Scan(reader).Handle(Image(), CancellationToken.None);

        Assert.Equal(ScanReceiptOutcome.TimedOut, result.Outcome);
        Assert.Equal(1, reader.Calls);
    }

    /// <summary>An unusable reader is its own outcome, and is not retried either.</summary>
    [Fact]
    public async Task An_unavailable_reader_is_reported_and_not_retried()
    {
        var reader = FakeReceiptReader.Unavailable();
        var result = await Scan(reader).Handle(Image(), CancellationToken.None);

        Assert.Equal(ScanReceiptOutcome.Unavailable, result.Outcome);
        Assert.Equal(1, reader.Calls);
    }

    /// <summary>
    /// Issue #1165 through the use case: a line with no printed pack size is suggested the one the
    /// business last paid this supplier for, and the history that is read is that supplier's and that
    /// product's - the supplier being the one the draft itself suggests.
    /// </summary>
    [Fact]
    public async Task A_missing_pack_size_is_suggested_from_the_last_purchase()
    {
        var purchases = new FakeReceiptScanPurchaseHistoryStore(
            new ReceiptScanLastPurchase(91, 1.2067m, new DateOnly(2026, 9, 14)));

        var result = await Scan(new FakeReceiptReader(NoPackSize), purchases: purchases)
            .Handle(Image(), CancellationToken.None);

        var line = Assert.Single(result.Draft!.Lines);
        Assert.Equal(24, line.UnitsPerPack!.Value);
        Assert.Equal(ReceiptValueProvenance.Suggested, line.UnitsPerPack.Provenance);
        Assert.Equal(24, line.Units!.Value);
        Assert.Equal(
            new ReceiptPackSizeSuggestion(24, ReceiptPackSizeSource.LastPurchase, new DateOnly(2026, 9, 14)),
            line.UnitsPerPackSuggestion);

        Assert.Equal(1, purchases.Calls);
        Assert.Equal(7, purchases.LastSupplierId);
        Assert.Equal([91L], purchases.LastProductIds);
    }

    /// <summary>
    /// And the history a scan cannot use is never read at all: a receipt whose pack sizes were all
    /// printed, a receipt with no supplier of this business to read a history of, and an answer
    /// nothing could be parsed out of each read no purchase history.
    /// </summary>
    [Theory]
    [InlineData("printed")]
    [InlineData("no supplier")]
    [InlineData("unreadable")]
    public async Task No_purchase_history_is_read_when_nothing_could_be_suggested_from_it(string receipt)
    {
        var purchases = new FakeReceiptScanPurchaseHistoryStore(
            new ReceiptScanLastPurchase(91, 1.2067m, new DateOnly(2026, 9, 14)));
        var json = receipt switch
        {
            "printed" => OneLine,
            "no supplier" => NoPackSize.Replace("\"supplierId\": 7", "\"supplierId\": 4242", StringComparison.Ordinal),
            _ => "I'm sorry, I can't read that.",
        };

        await Scan(new FakeReceiptReader(json), purchases: purchases).Handle(Image(), CancellationToken.None);

        Assert.Equal(0, purchases.Calls);
    }

    /// <summary>
    /// The issue's criterion about what is sent: only this business's products and suppliers, as ids
    /// and names, and the media type the server derived rather than anything the client claimed.
    /// </summary>
    [Fact]
    public async Task Only_this_businesss_product_and_supplier_names_are_sent()
    {
        var reader = new FakeReceiptReader(OneLine);
        await Scan(reader).Handle(Image(), CancellationToken.None);

        var request = reader.LastRequest!;
        Assert.Equal([new ReceiptScanProduct(91, "Cola 375mL")], request.Products);
        Assert.Equal([new ReceiptScanSupplier(7, "Metro Beverages")], request.Suppliers);
        Assert.Equal("image/jpeg", request.File.MediaType);
        Assert.Equal(ReceiptScanMediaKind.Image, request.File.Kind);
    }

    /// <summary>
    /// The issue's logging criterion: one line per attempt, carrying the business, the outcome, the
    /// duration, the page count and the token counts - and the port has no parameter that could
    /// carry receipt text, a file name or a product name.
    /// </summary>
    [Fact]
    public async Task One_line_is_logged_with_the_business_outcome_duration_pages_and_tokens()
    {
        var log = new RecordingReceiptScanLog();
        await Scan(new FakeReceiptReader(OneLine, inputTokens: 1200, outputTokens: 400), log: log)
            .Handle(Image("receipt.pdf"), CancellationToken.None);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(BusinessId, entry.BusinessId);
        Assert.Equal(ScanReceiptOutcome.Draft, entry.Outcome);
        Assert.True(entry.Duration >= TimeSpan.Zero);
        Assert.Equal(1, entry.PageCount);
        Assert.Equal(1200, entry.InputTokens);
        Assert.Equal(400, entry.OutputTokens);
    }

    /// <summary>
    /// Every refusal is logged too, with no tokens: a refused scan still has to be countable, and the
    /// absence of a token count is what says nothing was spent.
    /// </summary>
    [Theory]
    [InlineData(true, ScanReceiptOutcome.SwitchedOff)]
    [InlineData(false, ScanReceiptOutcome.Draft)]
    public async Task A_refusal_is_logged_as_well_as_a_draft(bool switchedOff, ScanReceiptOutcome expected)
    {
        var log = new RecordingReceiptScanLog();
        var settings = switchedOff ? FakeReceiptScanSettings.SwitchedOff() : new FakeReceiptScanSettings();

        await Scan(new FakeReceiptReader(OneLine), settings: settings, log: log)
            .Handle(Image(), CancellationToken.None);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(expected, entry.Outcome);
        Assert.Equal(switchedOff ? null : 1200, entry.InputTokens);
    }

    private static ScanReceipt Scan(
        IReceiptReader reader,
        IReceiptScanSettings? settings = null,
        IPdfPageCounter? pages = null,
        IReceiptScanLog? log = null,
        IReceiptScanPurchaseHistoryStore? purchases = null) =>
        new(
            settings ?? new FakeReceiptScanSettings(),
            new FakeReceiptScanCatalogueStore(
                [new ReceiptScanProduct(91, "Cola 375mL")],
                [new ReceiptScanSupplier(7, "Metro Beverages")]),
            purchases ?? new FakeReceiptScanPurchaseHistoryStore(),
            pages ?? new FakePdfPageCounter(),
            reader,
            log ?? new RecordingReceiptScanLog(),
            new FakeCurrentBusinessProvider(BusinessId));

    /// <summary>
    /// A submitted file, as the controller adapts one. The bytes are a placeholder: nothing in this
    /// use case decodes them, and the reader is substituted.
    /// </summary>
    private static PurchaseFileInput Image(string fileName = "receipt.jpg", long? length = null)
    {
        var content = Encoding.UTF8.GetBytes("not a real image");

        return new PurchaseFileInput(
            fileName,
            "application/octet-stream",
            length ?? content.Length,
            () => new MemoryStream(content));
    }
}
