namespace Inventory.Domain.ReceiptScanning;

/// <summary>What kind of file a receipt scan was given (issue #1163).</summary>
public enum ReceiptScanMediaKind
{
    /// <summary>A photograph of a receipt.</summary>
    Image = 10,

    /// <summary>A PDF, which has a page count as well as a size.</summary>
    Pdf = 20,
}

/// <summary>Why a candidate receipt file was refused, or that it was not.</summary>
public enum ReceiptScanFileVerdict
{
    /// <summary>It may be read.</summary>
    Accepted = 10,

    /// <summary>No file was submitted at all.</summary>
    Missing = 20,

    /// <summary>
    /// It fails the purchase document rules this application already applies to a receipt - empty,
    /// or over the 10 MB ceiling.
    /// </summary>
    PurchaseDocumentSize = 30,

    /// <summary>It is not a kind of file a purchase document may be at all.</summary>
    PurchaseDocumentType = 40,

    /// <summary>
    /// It is a HEIC/HEIF photograph: a legitimate purchase document, which the reader cannot read.
    /// </summary>
    Heic = 50,

    /// <summary>It is a purchase document of a type the reader cannot read, and not HEIC.</summary>
    UnsupportedByReader = 60,

    /// <summary>The image is over the configured image size limit.</summary>
    ImageTooLarge = 70,

    /// <summary>The PDF is over the configured PDF size limit.</summary>
    PdfTooLarge = 80,

    /// <summary>The PDF has more pages than the configured page limit.</summary>
    PdfTooManyPages = 90,

    /// <summary>The PDF's page count could not be read, so nothing can say it is within the limit.</summary>
    PdfUnreadable = 100,
}

/// <summary>
/// What a receipt scan may be given (issue #1163): the purchase document rules this application
/// already applies to a receipt, plus the reader's own input rules.
///
/// <para><strong>Both sets apply, and the order is what makes the messages useful.</strong> The
/// purchase document size ceiling comes first, then HEIC, then the remaining type rules. A HEIC
/// photograph is a perfectly good purchase document - the create and attach paths accept one - so it
/// is refused here for the one reason it is refused: the reader cannot read it. Telling somebody to
/// convert the photo is actionable; telling them the file type is wrong, when the same file attaches
/// to a purchase, is not.</para>
///
/// <para><strong>The type is decided from the extension, never from the client's content type</strong>
/// (AGENTS.md § Files and attachments). It is the same decision
/// <c>Inventory.Domain.Purchases.PurchaseDocumentPolicy</c> makes, and the media type the reader is
/// told is derived here from that extension rather than echoed from the request.</para>
///
/// <para><strong>The limits are configuration, and this policy only compares against them.</strong>
/// The reader's documented image size, PDF size and page limits change when the vendor changes them,
/// so they are settings with defaults rather than constants here (see
/// <c>Inventory.Application.ReceiptScanning.IReceiptScanSettings</c>).</para>
/// </summary>
public static class ReceiptScanFilePolicy
{
    /// <summary>What a caller is told when the request carried no file at all.</summary>
    public const string MissingFileMessage = "Select a receipt to scan.";

    /// <summary>
    /// What a caller is told when the photograph is HEIC/HEIF. The one message in this policy that
    /// tells somebody what to do instead, because there is something to do.
    /// </summary>
    public const string HeicMessage = "Convert the photo to JPEG and try again.";

    /// <summary>What a caller is told when the file is not a type the reader can read.</summary>
    public const string UnsupportedByReaderMessage =
        "A receipt must be a JPEG, PNG, WebP or PDF file.";

    /// <summary>What a caller is told when a PDF's pages could not be counted.</summary>
    public const string PdfUnreadableMessage =
        "This PDF could not be read. Try a photo of the receipt instead.";

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    private static readonly string[] PdfExtensions = [".pdf"];

    private static readonly string[] HeicExtensionList = [".heic", ".heif"];

    /// <summary>The image extensions the reader accepts, lower-cased and with the dot.</summary>
    public static IReadOnlyList<string> AllowedImageExtensions => ImageExtensions;

    /// <summary>The document extensions the reader accepts.</summary>
    public static IReadOnlyList<string> AllowedPdfExtensions => PdfExtensions;

    /// <summary>
    /// The extensions that are a HEIC/HEIF photograph. Refused with
    /// <see cref="HeicMessage"/> rather than as an unsupported type.
    /// </summary>
    public static IReadOnlyList<string> HeicExtensions => HeicExtensionList;

    /// <summary>
    /// The kind of file <paramref name="extension"/> names, or <see langword="null"/> when the
    /// reader cannot read it. Case-insensitive, like the purchase document rules.
    /// </summary>
    public static ReceiptScanMediaKind? KindOf(string? extension)
    {
        var normalised = Normalise(extension);
        if (normalised is null)
        {
            return null;
        }

        if (ImageExtensions.Contains(normalised, StringComparer.Ordinal))
        {
            return ReceiptScanMediaKind.Image;
        }

        return PdfExtensions.Contains(normalised, StringComparer.Ordinal)
            ? ReceiptScanMediaKind.Pdf
            : null;
    }

    /// <summary>Whether <paramref name="extension"/> names a HEIC/HEIF photograph.</summary>
    public static bool IsHeic(string? extension) =>
        Normalise(extension) is { } normalised
        && HeicExtensionList.Contains(normalised, StringComparer.Ordinal);

    /// <summary>
    /// The media type the reader is told, derived from the extension and never from what the client
    /// claimed. <see langword="null"/> for an extension the reader cannot read.
    /// </summary>
    public static string? MediaTypeOf(string? extension) => Normalise(extension) switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        _ => null,
    };

    /// <summary>What a caller is told when an image is over the configured limit.</summary>
    public static string ImageTooLargeMessage(long maxBytes) =>
        $"A receipt photo must be {Megabytes(maxBytes)} MB or smaller.";

    /// <summary>What a caller is told when a PDF is over the configured limit.</summary>
    public static string PdfTooLargeMessage(long maxBytes) =>
        $"A receipt PDF must be {Megabytes(maxBytes)} MB or smaller.";

    /// <summary>What a caller is told when a PDF has too many pages.</summary>
    public static string PdfTooManyPagesMessage(int maxPages) =>
        $"A receipt PDF must have {maxPages} pages or fewer.";

    /// <summary>
    /// The message for one verdict, so a refusal has exactly one wording wherever it is produced.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="verdict"/> is <see cref="ReceiptScanFileVerdict.Accepted"/>, which is not a
    /// refusal and has no message, or is not declared.
    /// </exception>
    public static string MessageFor(ReceiptScanFileVerdict verdict, long maxBytes, int maxPages) =>
        verdict switch
        {
            ReceiptScanFileVerdict.Missing => MissingFileMessage,
            ReceiptScanFileVerdict.PurchaseDocumentSize => Purchases.PurchaseDocumentPolicy.SizeErrorMessage,
            ReceiptScanFileVerdict.PurchaseDocumentType => Purchases.PurchaseDocumentPolicy.ExtensionErrorMessage,
            ReceiptScanFileVerdict.Heic => HeicMessage,
            ReceiptScanFileVerdict.UnsupportedByReader => UnsupportedByReaderMessage,
            ReceiptScanFileVerdict.ImageTooLarge => ImageTooLargeMessage(maxBytes),
            ReceiptScanFileVerdict.PdfTooLarge => PdfTooLargeMessage(maxBytes),
            ReceiptScanFileVerdict.PdfTooManyPages => PdfTooManyPagesMessage(maxPages),
            ReceiptScanFileVerdict.PdfUnreadable => PdfUnreadableMessage,
            _ => throw new ArgumentOutOfRangeException(
                nameof(verdict), verdict, "Only a refusal has a message."),
        };

    /// <summary>
    /// Whether the submitted file may be read, judged on everything that can be judged before it is
    /// opened: the purchase document rules first, then the reader's type and size rules. A PDF's
    /// page count is a separate step (<see cref="CheckPdfPages"/>) because counting pages means
    /// parsing the file.
    /// </summary>
    /// <param name="extension">The file's extension, with the dot, as the server derived it.</param>
    /// <param name="lengthBytes">The file's length in bytes.</param>
    /// <param name="maxImageBytes">The configured image size limit.</param>
    /// <param name="maxPdfBytes">The configured PDF size limit.</param>
    public static ReceiptScanFileVerdict Check(
        string? extension,
        long lengthBytes,
        long maxImageBytes,
        long maxPdfBytes)
    {
        // The rules this application already applies to a receipt, unchanged and first: a scan is
        // given the same file an operator would attach to the purchase.
        if (!Purchases.PurchaseDocumentPolicy.IsSizeValid(lengthBytes))
        {
            return ReceiptScanFileVerdict.PurchaseDocumentSize;
        }

        // Then the reader's own, and HEIC first of all - before even the purchase document type rule.
        // It is the one refusal somebody can act on, so it gets the message that says what to do, and
        // asking about it first is what makes that message reach a `.heif` photograph too: the same
        // camera format, which is not on the purchase document allow-list at all and would otherwise
        // be refused with a sentence about file types instead.
        if (IsHeic(extension))
        {
            return ReceiptScanFileVerdict.Heic;
        }

        if (!Purchases.PurchaseDocumentPolicy.IsExtensionAllowed(extension))
        {
            return ReceiptScanFileVerdict.PurchaseDocumentType;
        }

        return KindOf(extension) switch
        {
            ReceiptScanMediaKind.Image => lengthBytes <= maxImageBytes
                ? ReceiptScanFileVerdict.Accepted
                : ReceiptScanFileVerdict.ImageTooLarge,
            ReceiptScanMediaKind.Pdf => lengthBytes <= maxPdfBytes
                ? ReceiptScanFileVerdict.Accepted
                : ReceiptScanFileVerdict.PdfTooLarge,
            _ => ReceiptScanFileVerdict.UnsupportedByReader,
        };
    }

    /// <summary>
    /// Whether a PDF's page count is within the configured limit. A <see langword="null"/> count is
    /// a PDF nothing could read, which is refused rather than sent on in the hope the reader manages:
    /// a file named <c>.pdf</c> that is not one would otherwise be uploaded to the reader whole.
    /// </summary>
    public static ReceiptScanFileVerdict CheckPdfPages(int? pageCount, int maxPages) => pageCount switch
    {
        null or <= 0 => ReceiptScanFileVerdict.PdfUnreadable,
        var count when count <= maxPages => ReceiptScanFileVerdict.Accepted,
        _ => ReceiptScanFileVerdict.PdfTooManyPages,
    };

    private static string? Normalise(string? extension) =>
        string.IsNullOrWhiteSpace(extension) ? null : extension.ToLowerInvariant();

    /// <summary>
    /// A byte limit as the whole or one-decimal number of megabytes a message quotes, counted in
    /// the binary megabytes <c>PurchaseDocumentPolicy</c>'s own 10 MB ceiling is written in.
    /// </summary>
    private static string Megabytes(long bytes)
    {
        var megabytes = bytes / (decimal)(1024 * 1024);
        return megabytes == Math.Floor(megabytes)
            ? megabytes.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
            : megabytes.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }
}
