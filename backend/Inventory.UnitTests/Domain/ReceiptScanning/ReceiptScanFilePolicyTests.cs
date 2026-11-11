using Inventory.Domain.Purchases;
using Inventory.Domain.ReceiptScanning;
using Xunit;

namespace InventoryApi.Tests.Domain.ReceiptScanning;

/// <summary>
/// What a receipt scan may be given (issue #1163): the purchase document rules this application
/// already applies to a receipt, then the reader's own.
/// </summary>
public class ReceiptScanFilePolicyTests
{
    private const long ImageLimit = 5L * 1024 * 1024;
    private const long PdfLimit = 32L * 1024 * 1024;

    /// <summary>Every type the reader reads, at a size inside both sets of rules.</summary>
    [Theory]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".JPG")]
    [InlineData(".png")]
    [InlineData(".webp")]
    [InlineData(".pdf")]
    public void A_supported_file_is_accepted(string extension) =>
        Assert.Equal(ReceiptScanFileVerdict.Accepted, Check(extension, 1024));

    /// <summary>
    /// The issue's HEIC criterion. It is a legitimate purchase document - the create and attach
    /// paths take one - so it is refused for the one reason it is refused, with the message that
    /// says what to do about it.
    /// </summary>
    [Theory]
    [InlineData(".heic")]
    [InlineData(".HEIC")]
    [InlineData(".heif")]
    public void A_heic_photo_is_refused_with_the_convert_message(string extension)
    {
        Assert.Equal(ReceiptScanFileVerdict.Heic, Check(extension, 1024));
        Assert.Equal(
            "Convert the photo to JPEG and try again.",
            ReceiptScanFilePolicy.MessageFor(ReceiptScanFileVerdict.Heic, ImageLimit, 100));
    }

    /// <summary>
    /// A HEIC file is a purchase document, which is what makes the refusal above the reader's and
    /// not the document rules'. If that ever stopped being true, this test says so.
    /// </summary>
    [Fact]
    public void A_heic_photo_is_still_an_allowed_purchase_document() =>
        Assert.True(PurchaseDocumentPolicy.IsExtensionAllowed(".heic"));

    /// <summary>The purchase document rules are applied first, and their own message is used.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(PurchaseDocumentPolicy.MaxFileSizeBytes + 1)]
    public void A_file_the_purchase_document_rules_refuse_is_refused_first(long length)
    {
        Assert.Equal(ReceiptScanFileVerdict.PurchaseDocumentSize, Check(".jpg", length));
        Assert.Equal(
            PurchaseDocumentPolicy.SizeErrorMessage,
            ReceiptScanFilePolicy.MessageFor(ReceiptScanFileVerdict.PurchaseDocumentSize, ImageLimit, 100));
    }

    /// <summary>A type no purchase document may be is refused as that, before the reader's rules.</summary>
    [Theory]
    [InlineData(".xlsx")]
    [InlineData(".txt")]
    [InlineData("")]
    [InlineData(null)]
    public void A_file_no_purchase_document_may_be_is_refused_as_that(string? extension) =>
        Assert.Equal(ReceiptScanFileVerdict.PurchaseDocumentType, Check(extension, 1024));

    /// <summary>
    /// The issue's size criterion: an image over the configured limit is refused, and the limit is
    /// the configured one rather than a constant.
    /// </summary>
    [Fact]
    public void An_image_over_the_configured_limit_is_refused()
    {
        Assert.Equal(ReceiptScanFileVerdict.Accepted, ReceiptScanFilePolicy.Check(".jpg", 2048, 2048, PdfLimit));
        Assert.Equal(
            ReceiptScanFileVerdict.ImageTooLarge,
            ReceiptScanFilePolicy.Check(".jpg", 2049, 2048, PdfLimit));
    }

    /// <summary>And a PDF against its own configured limit, which is a different setting.</summary>
    [Fact]
    public void A_pdf_over_the_configured_limit_is_refused()
    {
        Assert.Equal(ReceiptScanFileVerdict.Accepted, ReceiptScanFilePolicy.Check(".pdf", 2048, ImageLimit, 2048));
        Assert.Equal(
            ReceiptScanFileVerdict.PdfTooLarge,
            ReceiptScanFilePolicy.Check(".pdf", 2049, ImageLimit, 2048));
    }

    /// <summary>The issue's page criterion, and the limit is the configured one.</summary>
    [Theory]
    [InlineData(1, ReceiptScanFileVerdict.Accepted)]
    [InlineData(100, ReceiptScanFileVerdict.Accepted)]
    [InlineData(101, ReceiptScanFileVerdict.PdfTooManyPages)]
    public void A_pdf_is_judged_against_the_configured_page_limit(
        int pages,
        ReceiptScanFileVerdict expected) =>
        Assert.Equal(expected, ReceiptScanFilePolicy.CheckPdfPages(pages, 100));

    /// <summary>
    /// A PDF nobody could count is refused rather than sent on: a file named <c>.pdf</c> that is not
    /// one would otherwise be uploaded whole, and a PDF nobody can count must not be assumed to be
    /// one page.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_pdf_whose_pages_cannot_be_counted_is_refused(int? pages) =>
        Assert.Equal(
            ReceiptScanFileVerdict.PdfUnreadable,
            ReceiptScanFilePolicy.CheckPdfPages(pages, 100));

    /// <summary>The media type is derived from the extension, never echoed from the client.</summary>
    [Theory]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".jpeg", "image/jpeg")]
    [InlineData(".PNG", "image/png")]
    [InlineData(".webp", "image/webp")]
    [InlineData(".pdf", "application/pdf")]
    [InlineData(".heic", null)]
    [InlineData(".xlsx", null)]
    public void The_media_type_comes_from_the_extension(string extension, string? expected) =>
        Assert.Equal(expected, ReceiptScanFilePolicy.MediaTypeOf(extension));

    /// <summary>Which kind of file each extension is, and that HEIC is neither.</summary>
    [Theory]
    [InlineData(".jpg", ReceiptScanMediaKind.Image)]
    [InlineData(".pdf", ReceiptScanMediaKind.Pdf)]
    [InlineData(".heic", null)]
    public void The_media_kind_comes_from_the_extension(string extension, ReceiptScanMediaKind? expected) =>
        Assert.Equal(expected, ReceiptScanFilePolicy.KindOf(extension));

    /// <summary>
    /// Every refusal has a message, and an acceptance has none: a message for "accepted" would be a
    /// sentence nobody could be shown.
    /// </summary>
    [Fact]
    public void Every_refusal_has_a_message_and_an_acceptance_has_none()
    {
        Assert.All(
            Enum.GetValues<ReceiptScanFileVerdict>()
                .Where(verdict => verdict is not ReceiptScanFileVerdict.Accepted),
            verdict => Assert.False(
                string.IsNullOrWhiteSpace(ReceiptScanFilePolicy.MessageFor(verdict, ImageLimit, 100))));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ReceiptScanFilePolicy.MessageFor(ReceiptScanFileVerdict.Accepted, ImageLimit, 100));
    }

    /// <summary>
    /// The size and page messages quote the configured limit, so changing a setting changes what the
    /// operator is told rather than leaving a sentence about a number nobody enforces.
    /// </summary>
    [Fact]
    public void A_size_or_page_message_quotes_the_configured_limit()
    {
        Assert.Contains("5 MB", ReceiptScanFilePolicy.ImageTooLargeMessage(ImageLimit), StringComparison.Ordinal);
        Assert.Contains("32 MB", ReceiptScanFilePolicy.PdfTooLargeMessage(PdfLimit), StringComparison.Ordinal);
        Assert.Contains("20 pages", ReceiptScanFilePolicy.PdfTooManyPagesMessage(20), StringComparison.Ordinal);
    }

    private static ReceiptScanFileVerdict Check(string? extension, long length) =>
        ReceiptScanFilePolicy.Check(extension, length, ImageLimit, PdfLimit);
}

/// <summary>
/// Which printed date is the purchase date (issue #1163, decided 10 November 2026).
/// </summary>
public class ReceiptPurchaseDatePolicyTests
{
    /// <summary>
    /// The issue's own criterion: order 25 Oct, invoice 26 Oct, delivery 27 Oct is a purchase dated
    /// 27 Oct, because a purchase records stock arriving.
    /// </summary>
    [Fact]
    public void The_delivery_date_wins_when_one_is_printed() =>
        Assert.Equal(
            new DateOnly(2026, 10, 27),
            ReceiptPurchaseDatePolicy.Choose(
                deliveryDate: new DateOnly(2026, 10, 27),
                receiptDate: new DateOnly(2026, 10, 26),
                orderDate: new DateOnly(2026, 10, 25)));

    /// <summary>And its other half: with only a transaction date, that date.</summary>
    [Fact]
    public void The_receipt_date_is_used_when_no_delivery_date_is_printed() =>
        Assert.Equal(
            new DateOnly(2026, 10, 26),
            ReceiptPurchaseDatePolicy.Choose(
                deliveryDate: null,
                receiptDate: new DateOnly(2026, 10, 26),
                orderDate: new DateOnly(2026, 10, 25)));

    /// <summary>
    /// An order date is never the purchase date, even when it is the only date printed. A purchase
    /// dated when the order was placed would move the stock movement to a day nothing arrived.
    /// </summary>
    [Fact]
    public void An_order_date_is_never_the_purchase_date() =>
        Assert.Null(
            ReceiptPurchaseDatePolicy.Choose(
                deliveryDate: null, receiptDate: null, orderDate: new DateOnly(2026, 10, 25)));

    /// <summary>
    /// A receipt with no usable date leaves the operator to date the purchase, rather than this
    /// application dating it today - which would be a figure nothing on the receipt supports.
    /// </summary>
    [Fact]
    public void No_printed_date_is_no_purchase_date() =>
        Assert.Null(ReceiptPurchaseDatePolicy.Choose(null, null, null));
}
