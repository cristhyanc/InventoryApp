namespace Inventory.Infrastructure.Documents;

/// <summary>
/// The seam between <see cref="AzureBlobDocumentStorage"/> and the Azure Blob SDK.
///
/// It exists so the adapter's rules - tenant-scoped names, no overwrite, missing blob means
/// <c>null</c> - can be tested deterministically without Azure credentials or a network, while
/// the SDK calls themselves stay in one small wrapper
/// (<see cref="AzureBlobContainer"/>). Blob names arriving here are already server-derived by
/// <see cref="BlobDocumentPath"/>; an implementation must address exactly the name it is given
/// and must never widen it.
/// </summary>
public interface IDocumentBlobContainer
{
    /// <summary>
    /// Creates the blob if it does not exist. Returns <c>false</c> when a blob of that name is
    /// already there, which the caller treats as a conflict: an existing blob is never
    /// overwritten.
    /// </summary>
    Task<bool> CreateAsync(string blobName, Stream content, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the blob, or returns <c>null</c> when it does not exist.
    /// </summary>
    Task<BlobDocument?> OpenReadAsync(string blobName, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the blob and reports whether one was there to delete.
    /// </summary>
    Task<bool> DeleteAsync(string blobName, CancellationToken cancellationToken);

    /// <summary>
    /// The blobs stored under <paramref name="prefix"/>, with their lengths (issue #968).
    ///
    /// <para>It exists for the human-invoked attachment storage reconciliation, which has to be
    /// able to say "this file is stored and no record refers to it". The prefix is server-derived
    /// by <see cref="BlobDocumentPath.PrefixFor"/> from a business and a category, exactly as
    /// every other name reaching this seam is, so listing can no more cross a business than
    /// reading can. No request path uses it: the only caller is
    /// <see cref="Reconciliation.AzureBlobAttachmentFileInventory"/>, behind the command.</para>
    /// </summary>
    Task<IReadOnlyList<BlobDocumentListing>> ListAsync(string prefix, CancellationToken cancellationToken);
}

/// <summary>
/// One blob the container holds: its full name and what it occupies. Deliberately no content and
/// no URL - a listing is for reconciling storage against records, not for reading documents.
/// </summary>
/// <param name="BlobName">The blob's full name, including the prefix it was listed under.</param>
/// <param name="ByteLength">Its length in bytes, as the service reports it.</param>
public sealed record BlobDocumentListing(string BlobName, long ByteLength);

/// <summary>
/// A blob opened for reading: the content stream plus the two pieces of metadata the document
/// endpoints need. The caller owns the stream.
/// </summary>
/// <param name="Content">The blob's bytes.</param>
/// <param name="ByteLength">The blob's length in bytes.</param>
/// <param name="LastModified">When the blob was last written, as the service reports it.</param>
public sealed record BlobDocument(Stream Content, long ByteLength, DateTimeOffset? LastModified);
