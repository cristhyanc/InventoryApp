using System.Net;
using Azure;
using Azure.Storage.Blobs;

namespace Inventory.Infrastructure.Documents;

/// <summary>
/// The Azure Blob SDK wrapper behind <see cref="IDocumentBlobContainer"/>. It is deliberately
/// thin: it translates SDK results and failures into the seam's plain answers and contains no
/// tenant, category or naming logic of its own - all of that is decided before a name reaches
/// here, by <see cref="AzureBlobDocumentStorage"/> and <see cref="BlobDocumentPath"/>.
///
/// It generates no SAS and produces no public URL. The container is private, and the only way
/// bytes leave it is through these calls, made by the application on behalf of an authenticated
/// caller whose business has already been resolved.
/// </summary>
public sealed class AzureBlobContainer : IDocumentBlobContainer
{
    private readonly BlobContainerClient _container;

    /// <summary>Wraps a container client.</summary>
    public AzureBlobContainer(BlobContainerClient container)
    {
        ArgumentNullException.ThrowIfNull(container);

        _container = container;
    }

    /// <inheritdoc />
    public async Task<bool> CreateAsync(string blobName, Stream content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);
        ArgumentNullException.ThrowIfNull(content);

        try
        {
            // overwrite: false sends If-None-Match: *, so the service itself refuses to replace
            // an existing document; the decision is not left to a check-then-write race here.
            await _container.GetBlobClient(blobName).UploadAsync(content, overwrite: false, cancellationToken);
            return true;
        }
        catch (RequestFailedException failure) when (failure.Status == (int)HttpStatusCode.Conflict ||
            failure.Status == (int)HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<BlobDocument?> OpenReadAsync(string blobName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        try
        {
            var download = await _container.GetBlobClient(blobName).DownloadStreamingAsync(
                cancellationToken: cancellationToken);

            return new BlobDocument(
                download.Value.Content,
                download.Value.Details.ContentLength,
                download.Value.Details.LastModified);
        }
        catch (RequestFailedException failure) when (failure.Status == (int)HttpStatusCode.NotFound)
        {
            // A missing blob - or a missing container - is an ordinary "no document", which the
            // API turns into the same 404 a caller gets for a record it may not see.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string blobName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        try
        {
            return await _container.GetBlobClient(blobName).DeleteIfExistsAsync(
                cancellationToken: cancellationToken);
        }
        catch (RequestFailedException failure) when (failure.Status == (int)HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
