namespace Inventory.Infrastructure.Backups;

/// <summary>
/// The seam between <see cref="AzureBlobBackupUploader"/> and the Azure Blob SDK (issue #332).
///
/// It exists so the uploader's rules - the daily and monthly object names, the month's recovery
/// point being created exactly once, no object ever overwritten, an upload verified by reading it
/// back - can be tested deterministically without Azure credentials or a network, while the SDK
/// calls themselves stay in one small wrapper (<see cref="AzureBackupBlobContainer"/>).
///
/// The two operations are deliberately the minimum: there is no delete and no overwrite, because
/// nothing in the upload workflow may remove or replace a recovery point. Retention is separate,
/// human-controlled work.
/// </summary>
public interface IBackupBlobContainer
{
    /// <summary>
    /// Creates the object with the supplied metadata if, and only if, nothing exists under that
    /// name. Returns <c>false</c> when an object is already there, which the caller treats as the
    /// answer to a question rather than an error: for the month's recovery point it means this
    /// month already has one, and for a daily object it means a name collision that must not be
    /// resolved by overwriting.
    /// </summary>
    Task<bool> CreateIfAbsentAsync(
        string blobName,
        Stream content,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken);

    /// <summary>
    /// The object's length and metadata, or <c>null</c> when it does not exist. Used to verify
    /// that an accepted upload actually completed; the content itself is never downloaded.
    /// </summary>
    Task<BackupBlobProperties?> GetPropertiesAsync(string blobName, CancellationToken cancellationToken);
}

/// <summary>
/// What the service reports about a stored snapshot, without reading its bytes.
/// </summary>
/// <param name="ByteLength">The object's length in bytes.</param>
/// <param name="Metadata">The object's metadata, as the service returns it.</param>
public sealed record BackupBlobProperties(long ByteLength, IReadOnlyDictionary<string, string> Metadata);
