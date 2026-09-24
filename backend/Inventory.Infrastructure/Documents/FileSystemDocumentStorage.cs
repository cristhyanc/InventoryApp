using Inventory.Application.Documents;

namespace Inventory.Infrastructure.Documents;

/// <summary>
/// Stores uploaded business documents on the local filesystem: the behaviour the application
/// has always had, now behind <see cref="IDocumentStorage"/> (issue #39, checkpoint 1).
///
/// Documents are deliberately kept outside the static web root. Static-file middleware does not
/// run controller authorization, so anything under <c>wwwroot</c> would be downloadable by
/// anyone who knew its generated file name no matter what <c>[Authorize]</c> says. New
/// documents therefore go to <c>{ContentRootPath}/protected-files/{category}</c>, and the API
/// registers no static-file middleware at all.
///
/// Documents uploaded before protected storage existed still sit in <c>{WebRootPath}/{category}</c>.
/// They stay readable and deletable through the same authenticated endpoints - the read falls
/// back to that location - and they no longer have an anonymous URL. Retiring that fallback is a
/// separate, deliberate step after the documents have been migrated.
/// </summary>
public sealed class FileSystemDocumentStorage : IDocumentStorage
{
    /// <summary>Content-root-relative folder holding every protected uploaded document.</summary>
    public const string ProtectedRootFolderName = "protected-files";

    /// <summary>
    /// Category folder for purchase documents. The value keeps its legacy "receipts" name so
    /// documents uploaded before the Purchase rename stay reachable by their persisted stored
    /// file name; renaming it would need its own verified file migration.
    /// </summary>
    public const string PurchaseDocumentsFolderName = "receipts";

    /// <summary>Category folder for operating-expense supporting documents.</summary>
    public const string ExpenseAttachmentsFolderName = "expenses";

    private readonly string _contentRootPath;
    private readonly string _webRootPath;

    /// <summary>Creates the adapter over the configured filesystem roots.</summary>
    public FileSystemDocumentStorage(FileSystemDocumentStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ContentRootPath);

        _contentRootPath = options.ContentRootPath;
        _webRootPath = options.WebRootPath ?? Path.Combine(options.ContentRootPath, "wwwroot");
    }

    /// <summary>The folder name a category is stored under. Exposed so tests can assert layout.</summary>
    public static string FolderNameFor(DocumentCategory category) => category switch
    {
        DocumentCategory.PurchaseDocument => PurchaseDocumentsFolderName,
        DocumentCategory.ExpenseAttachment => ExpenseAttachmentsFolderName,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown document category."),
    };

    /// <inheritdoc />
    public async Task SaveAsync(
        DocumentCategory category,
        string storedFileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storedFileName);
        ArgumentNullException.ThrowIfNull(content);

        var folder = Path.Combine(_contentRootPath, ProtectedRootFolderName, FolderNameFor(category));
        var path = ConfineToFolder(folder, storedFileName)
            ?? throw new ArgumentException(
                "Stored document name does not name a file inside its storage folder.", nameof(storedFileName));
        Directory.CreateDirectory(folder);

        // CreateNew, never Create: the stored name is server-generated and unique, so an existing
        // file means something is wrong and overwriting it would destroy another record's
        // document. Opening the file before the try block matters: if this throws, the document
        // that is already there was not written by this call and must not be cleaned up.
        var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            await using (destination)
            {
                await content.CopyToAsync(destination, cancellationToken);
            }
        }
        catch
        {
            // A half-written document must not survive the failure that produced it.
            DeleteIfExists(path);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<DocumentContent?> OpenReadAsync(
        DocumentCategory category,
        string? storedFileName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = ExistingPath(category, storedFileName);
        if (path is null) return Task.FromResult<DocumentContent?>(null);

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        return Task.FromResult<DocumentContent?>(new DocumentContent(stream, stream.Length));
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(
        DocumentCategory category,
        string? storedFileName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = ExistingPath(category, storedFileName);
        if (path is null) return Task.FromResult(false);

        File.Delete(path);
        return Task.FromResult(true);
    }

    /// <summary>
    /// The path of an existing document, or <c>null</c> when it is in neither the protected nor
    /// the legacy location. Protected storage wins, so a migrated document is read from its new
    /// home even while the legacy copy is still on disk awaiting deliberate removal.
    /// </summary>
    private string? ExistingPath(DocumentCategory category, string? storedFileName)
    {
        if (string.IsNullOrWhiteSpace(storedFileName)) return null;

        var folderName = FolderNameFor(category);
        var protectedPath = ConfineToFolder(
            Path.Combine(_contentRootPath, ProtectedRootFolderName, folderName), storedFileName);
        if (protectedPath is not null && File.Exists(protectedPath)) return protectedPath;

        var legacyPath = ConfineToFolder(Path.Combine(_webRootPath, folderName), storedFileName);
        return legacyPath is not null && File.Exists(legacyPath) ? legacyPath : null;
    }

    /// <summary>
    /// Reduces a stored name to its final path segment and proves the result is inside the
    /// category folder, or returns <c>null</c> when it is not a name this storage will touch.
    /// The name comes from persisted metadata rather than a request - no endpoint accepts one -
    /// but it is still treated as untrusted, so a crafted value such as
    /// <c>../../appsettings.json</c> can neither be read nor deleted.
    /// </summary>
    private static string? ConfineToFolder(string folder, string storedFileName)
    {
        var fileName = Path.GetFileName(storedFileName);
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var folderFullPath = Path.GetFullPath(folder);
        var candidate = Path.GetFullPath(Path.Combine(folderFullPath, fileName));
        var prefix = folderFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? folderFullPath
            : folderFullPath + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
