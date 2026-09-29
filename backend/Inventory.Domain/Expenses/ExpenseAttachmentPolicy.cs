namespace Inventory.Domain.Expenses;

/// <summary>
/// The deterministic acceptance rules for an operating-expense supporting document: size and
/// extension. Reading or writing the bytes is an Application/Infrastructure concern through
/// <c>IDocumentStorage</c>; this only decides whether a candidate file is acceptable and what
/// content type it should be served with.
/// </summary>
public static class ExpenseAttachmentPolicy
{
    public const string SizeErrorMessage = "Supporting document must be between 1 byte and 10 MB.";
    public const string ExtensionErrorMessage = "Supporting document must be an image or PDF.";
    public const long MaxFileSizeBytes = 10 * 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, string> ContentTypesByExtension =
        new Dictionary<string, string>
        {
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".pdf"] = "application/pdf",
            [".webp"] = "image/webp",
            [".heic"] = "image/heic",
        };

    public static bool IsSizeValid(long lengthBytes) => lengthBytes is > 0 and <= MaxFileSizeBytes;

    public static bool IsExtensionAllowed(string extension) =>
        ContentTypesByExtension.ContainsKey(extension.ToLowerInvariant());

    public static string ContentTypeFor(string extension) =>
        ContentTypesByExtension.TryGetValue(extension.ToLowerInvariant(), out var contentType)
            ? contentType
            : "application/octet-stream";
}
