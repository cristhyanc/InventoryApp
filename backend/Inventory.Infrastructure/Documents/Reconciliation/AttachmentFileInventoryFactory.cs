using Azure.Identity;
using Azure.Storage.Blobs;

namespace Inventory.Infrastructure.Documents.Reconciliation;

/// <summary>
/// Builds the read-only storage inspection the attachment storage reconciliation runs against
/// (issue #968).
///
/// <para>The command runs outside the web host, so it has no service container to resolve an
/// adapter from. This factory keeps the Azure SDK inside Infrastructure for the reason
/// <see cref="Migration.DocumentMigrationDestinationFactory"/> does: the command that calls it
/// never names a <c>BlobServiceClient</c> or a credential type, which an architecture test
/// enforces.</para>
///
/// <para>Unlike the migration's factory, this one accepts <em>either</em> provider: reconciling
/// is something a deployment should be able to do whichever storage it uses, and reading the
/// wrong one cannot damage anything - nothing here writes or deletes a file.</para>
/// </summary>
public static class AttachmentFileInventoryFactory
{
    /// <summary>
    /// Creates the inspection for <paramref name="configuration"/>.
    ///
    /// Authentication for Blob storage is <c>DefaultAzureCredential</c>: the App Service's
    /// system-assigned managed identity where the application runs, and the operator's own Azure
    /// sign-in when they run the command by hand. No account key, SAS token, client secret or
    /// connection string is read anywhere.
    /// </summary>
    /// <param name="configuration">The validated storage selection.</param>
    /// <param name="fileSystem">
    /// Where the filesystem adapter keeps documents, used only when that is the configured
    /// provider.
    /// </param>
    public static IAttachmentFileInventory Create(
        ResolvedDocumentStorageConfiguration configuration, FileSystemDocumentStorageOptions fileSystem)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(fileSystem);

        if (configuration.Provider != DocumentStorageProvider.AzureBlob)
            return new FileSystemAttachmentFileInventory(fileSystem);

        var container = new BlobServiceClient(configuration.BlobServiceUri, new DefaultAzureCredential())
            .GetBlobContainerClient(configuration.ContainerName);

        return new AzureBlobAttachmentFileInventory(new AzureBlobContainer(container));
    }
}
