namespace Inventory.Domain.Purchases;

/// <summary>
/// The deterministic rules about a purchase's supporting document (issue #976, child of #965).
///
/// <para><strong>A purchase may have no document at all.</strong> A business with no receipt to
/// attach - or no storage left to put one in - still has to be able to record what it bought, so
/// the document is optional on the purchase that records it and may be added later. What stays
/// unchanged is what an <em>uploaded</em> document must be: the same 10 MiB ceiling and the same
/// allowed types the create path has always applied, now written here so the create and the
/// later attach cannot drift apart.</para>
///
/// <para><strong>The four metadata values are one fact.</strong> A purchase either has a document -
/// a stored name, a content type, the original name and a size - or it has none of them. A
/// half-recorded document is not a state this application has: it would be a row claiming bytes
/// nothing can open, or bytes nothing names. <see cref="IsMetadataConsistent"/> is that rule, and a
/// database <c>CHECK</c> constraint enforces it underneath (see
/// <c>AllowPurchaseWithoutDocument</c>).</para>
///
/// <para>Reading or writing the bytes is an Application/Infrastructure concern through
/// <c>IDocumentStorage</c>; this only decides whether a candidate document is acceptable and
/// whether a recorded one is coherent. It deliberately mirrors
/// <c>Inventory.Domain.Expenses.ExpenseAttachmentPolicy</c>, which is the same decision for an
/// operating expense's attachment, and the two stay separate because an expense attachment is out
/// of this issue's scope.</para>
/// </summary>
public static class PurchaseDocumentPolicy
{
    /// <summary>The ceiling the create path has always applied, unchanged.</summary>
    public const long MaxFileSizeBytes = 10 * 1024 * 1024;

    /// <summary>What a caller is told when the uploaded document is empty or too large.</summary>
    public const string SizeErrorMessage = "Purchase document must be between 1 byte and 10 MB.";

    /// <summary>What a caller is told when the uploaded document is not an image or a PDF.</summary>
    public const string ExtensionErrorMessage = "Purchase document must be an image or PDF.";

    /// <summary>
    /// What a caller is told when a request to <em>attach</em> a document carried no document at
    /// all. Creating a purchase with no document is the ordinary case and is never refused; asking
    /// to attach nothing is a request that cannot be carried out.
    /// </summary>
    public const string MissingDocumentMessage = "Select a purchase document to attach.";

    /// <summary>
    /// What a caller is told when the purchase it is attaching to already has a document. Replacing
    /// one is deliberately out of scope (issue #976), and this is also the answer the loser of two
    /// concurrent attaches gets, which is why it reads as a statement about the purchase's current
    /// state rather than about the request.
    /// </summary>
    public const string AlreadyHasDocumentMessage =
        "This purchase already has a document, so another one cannot be attached.";

    /// <summary>
    /// What a caller is told when a purchase with no document carries no title either. With a
    /// document the title falls back to the uploaded file's name; with none there is nothing to fall
    /// back to, and a purchase nobody can recognise in a list is not a usable record.
    /// </summary>
    public const string TitleRequiredWithoutDocumentMessage =
        "A purchase with no document must have a title.";

    private static readonly string[] Allowed = [".jpg", ".jpeg", ".png", ".pdf", ".webp", ".heic"];

    /// <summary>The extensions an uploaded purchase document may have, lower-cased and with the dot.</summary>
    public static IReadOnlyList<string> AllowedExtensions => Allowed;

    public static bool IsSizeValid(long lengthBytes) => lengthBytes is > 0 and <= MaxFileSizeBytes;

    public static bool IsExtensionAllowed(string? extension) =>
        extension is not null &&
        Allowed.Contains(extension.ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>
    /// Whether the four stored metadata values describe one coherent state: all four present, or
    /// all four absent. An empty or whitespace name or content type counts as absent, because a
    /// blank stored name names no document.
    /// </summary>
    public static bool IsMetadataConsistent(
        string? storedFileName, string? contentType, string? fileName, long? fileSizeBytes)
    {
        var present = Present(storedFileName) && Present(contentType) && Present(fileName) &&
            fileSizeBytes.HasValue;
        var absent = !Present(storedFileName) && !Present(contentType) && !Present(fileName) &&
            !fileSizeBytes.HasValue;
        return present || absent;

        static bool Present(string? value) => !string.IsNullOrWhiteSpace(value);
    }
}
