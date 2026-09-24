using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Inventory.Infrastructure.Documents;

/// <summary>
/// The Azure Blob SDK wrapper behind <see cref="IDocumentBlobContainer"/>. It is deliberately
/// thin: it translates SDK results and failures into the seam's plain answers and contains no
/// tenant, category or naming logic of its own - all of that is decided before a name reaches
/// here, by <see cref="AzureBlobDocumentStorage"/> and <see cref="BlobDocumentPath"/>.
///
/// Translation is by <see cref="RequestFailedException.ErrorCode"/>, never by HTTP status. The
/// status alone conflates failures that mean entirely different things: a missing container, a
/// revoked role assignment and a missing document can all arrive as 404, and a lease conflict
/// looks exactly like a name collision at 409. Turning any of those into "no document" or
/// "already there" would hide a broken deployment behind an ordinary-looking empty result -
/// documents would appear to vanish while the application reported nothing wrong. Only the
/// precise codes below are answers; everything else propagates.
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
        catch (RequestFailedException failure) when (IsDestinationBlobAlreadyPresent(failure))
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
        catch (RequestFailedException failure) when (Is(failure, BlobErrorCode.BlobNotFound))
        {
            // This blob, specifically, is not there: an ordinary "no document", which the API
            // turns into the same 404 a caller gets for a record it may not see. A missing
            // container is not this - it means the deployment is pointed at storage that does
            // not exist - so it is left to propagate.
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
        catch (RequestFailedException failure) when (Is(failure, BlobErrorCode.BlobNotFound))
        {
            // DeleteIfExists already answers false for a blob that is not there; this covers the
            // same condition arriving as an exception. Nothing else is turned into "there was
            // nothing to delete", because a delete that quietly did nothing is indistinguishable
            // from one that worked.
            return false;
        }
    }

    /// <summary>
    /// Whether a failed create-if-absent means the destination blob is already there.
    ///
    /// <see cref="BlobErrorCode.BlobAlreadyExists"/> says so outright.
    /// <see cref="BlobErrorCode.ConditionNotMet"/> is admissible only because of how the request
    /// was made: <c>overwrite: false</c> sets exactly one condition, <c>If-None-Match: *</c>, so
    /// the only condition that can fail is "a blob of this name already exists". Every other
    /// conflict or precondition failure - a lease held on the blob, a container being deleted,
    /// an immutability policy - is a real fault and must reach an operator rather than be
    /// reported to the caller as a name collision.
    /// </summary>
    private static bool IsDestinationBlobAlreadyPresent(RequestFailedException failure) =>
        Is(failure, BlobErrorCode.BlobAlreadyExists) || Is(failure, BlobErrorCode.ConditionNotMet);

    private static bool Is(RequestFailedException failure, BlobErrorCode errorCode) =>
        string.Equals(failure.ErrorCode, errorCode.ToString(), StringComparison.Ordinal);
}
