using Inventory.Application.Documents;

namespace Inventory.Infrastructure.Documents.Reconciliation;

/// <summary>
/// One stored attachment file as the reconciliation found it (issue #968).
/// </summary>
/// <param name="Category">Which kind of attachment it is stored as.</param>
/// <param name="BusinessId">
/// The business the storage layout attributes it to, or <see langword="null"/> when the layout
/// cannot: the filesystem adapter stores every business's documents in one folder per category, so
/// a file there is attributable only through the record that refers to it. Blob storage is
/// per-business by prefix, so it always answers one.
/// </param>
/// <param name="StoredFileName">The server-generated stored name, which names no business data.</param>
/// <param name="ByteLength">What the stored file actually occupies.</param>
public sealed record StoredAttachmentFile(
    DocumentCategory Category, int? BusinessId, string StoredFileName, long ByteLength);

/// <summary>
/// The read-only inspection of stored attachment files the reconciliation command needs
/// (issue #968), and deliberately <strong>not</strong> part of <c>IDocumentStorage</c>.
///
/// <para>That port is the narrow one a request uses: one document, by the stored name already held
/// on a tenant-owned record, inside the business the request resolved. Listing what is in storage
/// is the opposite shape - every file, across businesses - and belongs to a human-invoked command
/// rather than to any request path. Keeping it here means no controller, use case or adapter can
/// reach it, and <c>IDocumentStorage</c> stays as small as it was.</para>
///
/// <para>It is read-only by construction: there is no method that writes or deletes anything. The
/// reconciliation never deletes a file, and this interface is what makes that structural rather
/// than a promise.</para>
/// </summary>
public interface IAttachmentFileInventory
{
    /// <summary>
    /// Every stored file of <paramref name="category"/>, for the businesses given where the layout
    /// is per business and for the whole category where it is not.
    /// </summary>
    Task<IReadOnlyList<StoredAttachmentFile>> ListAsync(
        DocumentCategory category, IReadOnlyList<int> businessIds, CancellationToken cancellationToken);

    /// <summary>
    /// What one record's stored file occupies, or <see langword="null"/> when there is no such file
    /// - which is the "row whose file is missing" the report names.
    /// </summary>
    Task<long?> MeasureAsync(
        DocumentCategory category, int businessId, string storedFileName, CancellationToken cancellationToken);
}
