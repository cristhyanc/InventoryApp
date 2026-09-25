using System.Globalization;
using Inventory.Application.Documents;
using Inventory.Domain.Tenancy;

namespace Inventory.Infrastructure.Documents;

/// <summary>
/// Builds the blob name a document is stored under.
///
/// The name is entirely server-derived: the business comes from the trusted current-business
/// scope, the folder from the <see cref="DocumentCategory"/> the caller asked for, and only the
/// last path segment of the stored file name survives. Because every read, write and delete goes
/// through here, one business's documents cannot be addressed while another business is current,
/// and a crafted stored name cannot inject a prefix of its own.
/// </summary>
public static class BlobDocumentPath
{
    /// <summary>The prefix every business's documents live under.</summary>
    public const string TenantPrefix = "tenants";

    /// <summary>Folder holding a business's purchase documents.</summary>
    public const string PurchaseDocumentsFolderName = "purchases";

    /// <summary>Folder holding a business's operating-expense attachments.</summary>
    public const string ExpenseAttachmentsFolderName = "expenses";

    /// <summary>The folder name a category is stored under.</summary>
    public static string FolderNameFor(DocumentCategory category) => category switch
    {
        DocumentCategory.PurchaseDocument => PurchaseDocumentsFolderName,
        DocumentCategory.ExpenseAttachment => ExpenseAttachmentsFolderName,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown document category."),
    };

    /// <summary>
    /// The full blob name, <c>tenants/{businessId}/{folder}/{storedFileName}</c>, or <c>null</c>
    /// when the stored name does not reduce to a usable file name.
    /// </summary>
    public static string? For(BusinessId businessId, DocumentCategory category, string? storedFileName)
    {
        var fileName = ReduceToFileName(storedFileName);
        if (fileName is null) return null;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{TenantPrefix}/{businessId.Value}/{FolderNameFor(category)}/{fileName}");
    }

    /// <summary>
    /// The prefix under which a business's documents of one category live. Nothing outside it is
    /// ever addressed, which is what makes another business's blobs unreachable rather than
    /// merely unlisted.
    /// </summary>
    public static string PrefixFor(BusinessId businessId, DocumentCategory category) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{TenantPrefix}/{businessId.Value}/{FolderNameFor(category)}/");

    /// <summary>
    /// Reduces an untrusted stored name to its last path segment, or returns <c>null</c> when
    /// nothing usable remains.
    ///
    /// Blob names take <c>/</c> as a separator on every platform, and a stored name could have
    /// been written on Windows, so both separators are cut. Relative segments are refused
    /// outright rather than reduced: the Blob service canonicalises nothing, so a name
    /// containing <c>..</c> would become a real, differently-prefixed blob.
    /// </summary>
    public static string? ReduceToFileName(string? storedFileName)
    {
        if (string.IsNullOrWhiteSpace(storedFileName)) return null;

        var lastSeparator = storedFileName.LastIndexOfAny(['/', '\\']);
        var fileName = lastSeparator >= 0 ? storedFileName[(lastSeparator + 1)..] : storedFileName;

        if (string.IsNullOrWhiteSpace(fileName)) return null;
        if (fileName is "." or "..") return null;

        // Control characters cannot appear in a blob name and would otherwise be sent to the
        // service as a malformed request rather than being refused here.
        return fileName.Any(char.IsControl) ? null : fileName;
    }
}
