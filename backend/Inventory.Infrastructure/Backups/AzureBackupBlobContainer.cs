using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Inventory.Infrastructure.Backups;

/// <summary>
/// The Azure Blob SDK wrapper behind <see cref="IBackupBlobContainer"/> (issue #332). Like
/// <see cref="Documents.AzureBlobContainer"/> it is deliberately thin: it translates SDK results
/// and failures into the seam's plain answers and contains no naming, monthly-selection or
/// verification logic of its own - all of that is decided before a name reaches here, by
/// <see cref="AzureBlobBackupUploader"/> and <see cref="BackupObjectNames"/>.
///
/// Translation is by <see cref="RequestFailedException.ErrorCode"/>, never by HTTP status. The
/// status alone conflates failures that mean entirely different things: a missing container, a
/// revoked role assignment and a missing object can all arrive as 404, and a lease conflict looks
/// exactly like a name collision at 409. Reporting a broken or undeployed container as "this
/// month has no recovery point yet" would be the worst possible answer here - the uploader would
/// conclude the month still needs one, every month, while nothing was ever stored. Only the
/// precise codes below are answers; everything else propagates to the operator.
///
/// It generates no SAS and produces no public URL, and it cannot delete or overwrite: the seam has
/// no operation for either, so retention stays entirely outside this code.
/// </summary>
public sealed class AzureBackupBlobContainer : IBackupBlobContainer
{
    private readonly BlobContainerClient _container;

    /// <summary>Wraps a container client.</summary>
    public AzureBackupBlobContainer(BlobContainerClient container)
    {
        ArgumentNullException.ThrowIfNull(container);

        _container = container;
    }

    /// <inheritdoc />
    public async Task<bool> CreateIfAbsentAsync(
        string blobName,
        Stream content,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(metadata);

        try
        {
            // IfNoneMatch: ETag.All sends If-None-Match: *, so the service itself refuses to
            // replace an existing recovery point; the decision is never left to a check-then-write
            // race here, which is exactly what the monthly object relies on.
            await _container.GetBlobClient(blobName).UploadAsync(
                content,
                new BlobUploadOptions
                {
                    Metadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal),
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                },
                cancellationToken);

            return true;
        }
        catch (RequestFailedException failure) when (IsDestinationBlobAlreadyPresent(failure))
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<BackupBlobProperties?> GetPropertiesAsync(
        string blobName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        try
        {
            var properties = await _container.GetBlobClient(blobName)
                .GetPropertiesAsync(cancellationToken: cancellationToken);

            return new BackupBlobProperties(
                properties.Value.ContentLength,
                // The service returns metadata names lower-case; comparing case-insensitively
                // keeps the uploader's reads independent of that.
                new Dictionary<string, string>(properties.Value.Metadata, StringComparer.OrdinalIgnoreCase));
        }
        catch (RequestFailedException failure) when (Is(failure, BlobErrorCode.BlobNotFound))
        {
            // This object, specifically, is not there. A missing container or a refused role
            // assignment is not this - it means the deployment is pointed at storage that does not
            // exist or cannot be written to - so it is left to propagate.
            return null;
        }
    }

    /// <summary>
    /// Whether a failed create-if-absent means the destination object is already there.
    ///
    /// <see cref="BlobErrorCode.BlobAlreadyExists"/> says so outright.
    /// <see cref="BlobErrorCode.ConditionNotMet"/> is admissible only because of how the request
    /// was made: exactly one condition is set, <c>If-None-Match: *</c>, so the only condition that
    /// can fail is "an object of this name already exists". Every other conflict or precondition
    /// failure - a lease held on the blob, a container being deleted, an immutability policy - is a
    /// real fault and must reach an operator rather than be reported as "this month already has a
    /// recovery point".
    /// </summary>
    private static bool IsDestinationBlobAlreadyPresent(RequestFailedException failure) =>
        Is(failure, BlobErrorCode.BlobAlreadyExists) || Is(failure, BlobErrorCode.ConditionNotMet);

    private static bool Is(RequestFailedException failure, BlobErrorCode errorCode) =>
        string.Equals(failure.ErrorCode, errorCode.ToString(), StringComparison.Ordinal);
}
