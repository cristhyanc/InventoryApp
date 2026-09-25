using Azure.Identity;
using Azure.Storage.Blobs;
using Inventory.Application.Documents;
using Inventory.Application.Time;
using Inventory.Infrastructure.Clock;
using Inventory.Infrastructure.Documents;
using Inventory.Infrastructure.Time;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IBusinessCalendar, SydneyBusinessCalendar>();

        return services;
    }

    /// <summary>
    /// Registers document storage: the configured provider behind
    /// <see cref="IDocumentStorage"/>, and the filesystem implementation as a concrete type
    /// whatever the provider is.
    ///
    /// It is separate from <see cref="AddInfrastructureServices"/> because only the composition
    /// root knows the host's content and web roots and can read configuration; passing both in
    /// keeps <c>IWebHostEnvironment</c> and <c>IConfiguration</c> out of the Application and
    /// Infrastructure layers.
    ///
    /// An invalid or incomplete Azure configuration throws here, at startup. There is
    /// deliberately no fallback from AzureBlob to the filesystem: an application that could not
    /// reach its configured storage would otherwise start writing business documents to a local
    /// disk nobody is watching.
    /// </summary>
    /// <param name="services">The container being built.</param>
    /// <param name="options">The raw <c>DocumentStorage</c> configuration section.</param>
    /// <param name="fileSystemOptions">The host paths the filesystem implementation uses.</param>
    /// <exception cref="InvalidOperationException">The document-storage configuration is unsupported or incomplete.</exception>
    public static IServiceCollection AddDocumentStorage(
        this IServiceCollection services,
        DocumentStorageOptions options,
        FileSystemDocumentStorageOptions fileSystemOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fileSystemOptions);

        var configuration = DocumentStorageConfiguration.Resolve(options);
        services.AddSingleton(configuration);

        // Registered whichever provider is selected. The documents already on disk do not move
        // when the provider changes, so reading them stays possible - which the filesystem-to-Blob
        // migration will need, and which keeps switching the provider back a configuration
        // change rather than a code change.
        services.AddSingleton(fileSystemOptions);
        services.AddSingleton<FileSystemDocumentStorage>();

        switch (configuration.Provider)
        {
            case DocumentStorageProvider.FileSystem:
                services.AddSingleton<IDocumentStorage>(sp => sp.GetRequiredService<FileSystemDocumentStorage>());
                break;

            case DocumentStorageProvider.AzureBlob:
                AddAzureBlobDocumentStorage(services, configuration);
                break;

            default:
                throw new InvalidOperationException(
                    $"Document-storage provider '{configuration.Provider}' has no registration.");
        }

        return services;
    }

    private static void AddAzureBlobDocumentStorage(
        IServiceCollection services,
        ResolvedDocumentStorageConfiguration configuration)
    {
        // DefaultAzureCredential and BlobServiceClient are both thread-safe and cache their
        // tokens and connections, so they are built once. The credential chain is what keeps
        // secrets out of configuration entirely: the App Service authenticates with its
        // system-assigned managed identity, and a developer with the same container role
        // authenticates with their own Azure CLI / Visual Studio / VS Code sign-in. No account
        // key, SAS token, client secret or connection string is read anywhere.
        services.AddSingleton<BlobServiceClient>(_ =>
            new BlobServiceClient(configuration.BlobServiceUri, new DefaultAzureCredential()));

        services.AddSingleton<IDocumentBlobContainer>(sp => new AzureBlobContainer(
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(configuration.ContainerName)));

        // Scoped, unlike the filesystem adapter: it reads the current request's business scope,
        // which is what decides the tenant prefix every blob name is built from.
        services.AddScoped<IDocumentStorage, AzureBlobDocumentStorage>();
    }
}
