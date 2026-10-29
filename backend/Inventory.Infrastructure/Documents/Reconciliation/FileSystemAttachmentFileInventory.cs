using Inventory.Application.Documents;

namespace Inventory.Infrastructure.Documents.Reconciliation;

/// <summary>
/// The filesystem half of <see cref="IAttachmentFileInventory"/> (issue #968): what is actually on
/// disk under the protected storage root, and under the legacy web-root location documents
/// uploaded before protected storage still sit in.
///
/// <para><strong>A filesystem file names no business.</strong> That layout is one folder per
/// category for every business, so a file found here is attributed to a business only by the
/// record that refers to it; one no record refers to is reported as an orphan nobody can be
/// charged for, which is exactly how the quota treats it - usage is summed from records, never
/// from storage.</para>
///
/// <para>It only reads: enumerating a folder and measuring a file. There is no delete here and the
/// reconciliation has no way to ask for one.</para>
/// </summary>
public sealed class FileSystemAttachmentFileInventory : IAttachmentFileInventory
{
    private readonly FileSystemDocumentStorage _storage;
    private readonly string _contentRootPath;
    private readonly string _webRootPath;

    public FileSystemAttachmentFileInventory(FileSystemDocumentStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ContentRootPath);

        _storage = new FileSystemDocumentStorage(options);
        _contentRootPath = options.ContentRootPath;
        _webRootPath = options.WebRootPath ?? Path.Combine(options.ContentRootPath, "wwwroot");
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredAttachmentFile>> ListAsync(
        DocumentCategory category, IReadOnlyList<int> businessIds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var folderName = FileSystemDocumentStorage.FolderNameFor(category);
        var files = new List<StoredAttachmentFile>();

        // Protected storage first, then the legacy location: a document that exists in both is the
        // same document, and the read path prefers the protected copy, so it is listed once.
        AddFolder(Path.Combine(_contentRootPath, FileSystemDocumentStorage.ProtectedRootFolderName, folderName));
        AddFolder(Path.Combine(_webRootPath, folderName));

        return Task.FromResult<IReadOnlyList<StoredAttachmentFile>>(files);

        void AddFolder(string folder)
        {
            if (!Directory.Exists(folder)) return;

            foreach (var path in Directory.EnumerateFiles(folder))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var name = Path.GetFileName(path);
                if (files.Any(file => string.Equals(file.StoredFileName, name, StringComparison.Ordinal)))
                    continue;

                files.Add(new StoredAttachmentFile(
                    category, BusinessId: null, name, new FileInfo(path).Length));
            }
        }
    }

    /// <inheritdoc />
    public async Task<long?> MeasureAsync(
        DocumentCategory category, int businessId, string storedFileName, CancellationToken cancellationToken)
    {
        // The business is not part of a filesystem path, so it is deliberately unused here: the
        // caller has already established which business the record belongs to.
        var document = await _storage.OpenReadAsync(category, storedFileName, cancellationToken)
            .ConfigureAwait(false);
        if (document is null) return null;

        await using var content = document.Content;
        return document.ByteLength;
    }
}
