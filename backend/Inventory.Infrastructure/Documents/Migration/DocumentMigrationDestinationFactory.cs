using Azure.Identity;
using Azure.Storage.Blobs;

namespace Inventory.Infrastructure.Documents.Migration;

/// <summary>
/// Builds the migration's Azure Blob destination.
///
/// The migration command runs outside the web host, so it has no service container to resolve a
/// destination from; it also must not be able to build one by accident. This factory is
/// therefore explicit about the one thing that matters: it refuses anything but a fully
/// configured <see cref="DocumentStorageProvider.AzureBlob"/>. The application's own default is
/// <see cref="DocumentStorageProvider.FileSystem"/>, and a migration that quietly accepted that
/// default would "migrate" every document from the filesystem back onto the filesystem and
/// report success.
///
/// It also keeps the Azure SDK inside Infrastructure: the command that calls this never names a
/// <c>BlobServiceClient</c> or a credential type, which an architecture test enforces.
/// </summary>
public static class DocumentMigrationDestinationFactory
{
    /// <summary>
    /// Creates the destination described by <paramref name="configuration"/>.
    ///
    /// Authentication is <c>DefaultAzureCredential</c>: the App Service's system-assigned
    /// managed identity where the application runs, and the operator's own Azure sign-in when
    /// they run the migration by hand. No account key, SAS token, client secret or connection
    /// string is read anywhere.
    /// </summary>
    /// <exception cref="InvalidOperationException">The configured provider is not AzureBlob.</exception>
    public static IDocumentMigrationDestination CreateAzureBlob(
        ResolvedDocumentStorageConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.Provider != DocumentStorageProvider.AzureBlob)
        {
            throw new InvalidOperationException(
                $"Document migration requires {DocumentStorageOptions.SectionName}:{nameof(DocumentStorageOptions.Provider)}"
                    + $"={nameof(DocumentStorageProvider.AzureBlob)}, with "
                    + $"{nameof(DocumentStorageOptions.BlobServiceUri)} and "
                    + $"{nameof(DocumentStorageOptions.ContainerName)}. It is currently "
                    + $"'{configuration.Provider}'. The migration copies documents to Azure Blob "
                    + "Storage; it will not run against any other destination.");
        }

        var container = new BlobServiceClient(configuration.BlobServiceUri, new DefaultAzureCredential())
            .GetBlobContainerClient(configuration.ContainerName);

        return new AzureBlobMigrationDestination(new AzureBlobContainer(container));
    }
}
