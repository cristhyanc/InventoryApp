using Azure.Identity;
using Azure.Storage.Blobs;

namespace Inventory.Infrastructure.Backups;

/// <summary>
/// Builds the backup upload destination (issue #332).
///
/// The <c>backup-database</c> command runs before the web host is built, so it has no service
/// container to resolve an uploader from - the same situation
/// <see cref="Documents.Migration.DocumentMigrationDestinationFactory"/> is in. This factory is
/// the one place that turns a validated configuration into a working uploader, and it keeps the
/// Azure SDK inside Infrastructure: the command that calls it never names a
/// <c>BlobServiceClient</c> or a credential type, which an architecture test enforces.
/// </summary>
public static class BackupSnapshotUploaderFactory
{
    /// <summary>
    /// Creates the uploader for the destination described by <paramref name="configuration"/>.
    ///
    /// Authentication is <c>DefaultAzureCredential</c>: the App Service's managed identity when the
    /// scheduled job runs the command, and the operator's own Azure sign-in when they run it by
    /// hand. No account key, SAS token, client secret or connection string is read anywhere, and
    /// the credential chain never produces one that could be copied out of the process.
    /// </summary>
    public static IBackupSnapshotUploader CreateAzureBlob(ResolvedBackupStorageConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var container = new BlobServiceClient(configuration.BlobServiceUri, new DefaultAzureCredential())
            .GetBlobContainerClient(configuration.ContainerName);

        return new AzureBlobBackupUploader(new AzureBackupBlobContainer(container));
    }
}
