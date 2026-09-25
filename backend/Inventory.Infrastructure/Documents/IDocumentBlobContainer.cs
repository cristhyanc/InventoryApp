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
}

/// <summary>
/// A blob opened for reading: the content stream plus the two pieces of metadata the document
/// endpoints need. The caller owns the stream.
/// </summary>
/// <param name="Content">The blob's bytes.</param>
/// <param name="ByteLength">The blob's length in bytes.</param>
/// <param name="LastModified">When the blob was last written, as the service reports it.</param>
public sealed record BlobDocument(Stream Content, long ByteLength, DateTimeOffset? LastModified);
