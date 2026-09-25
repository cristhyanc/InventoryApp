using Inventory.Application.Documents;
using Inventory.Application.Tenancy;
using Inventory.Infrastructure;
using Inventory.Infrastructure.Documents;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Documents;

/// <summary>
/// How the <c>DocumentStorage</c> configuration selects an implementation (issue #39,
/// checkpoint 2).
///
/// The important case is the failure one. A half-configured Azure provider must stop the
/// application at startup rather than quietly falling back to the local disk: an API that
/// looked healthy while writing a business's documents to a machine nobody backs up, and that
/// then recycled, would lose them silently.
/// </summary>
public sealed class DocumentStorageProviderSelectionTests
{
    private const string BlobServiceUri = "https://inventoryappdocs.blob.core.windows.net";

    #region Provider parsing

    [Fact]
    public void An_absent_provider_keeps_the_filesystem_the_application_already_used()
    {
        var resolved = DocumentStorageConfiguration.Resolve(new DocumentStorageOptions());

        Assert.Equal(DocumentStorageProvider.FileSystem, resolved.Provider);
        Assert.Null(resolved.BlobServiceUri);
        Assert.Null(resolved.ContainerName);
    }

    [Theory]
    [InlineData("FileSystem", DocumentStorageProvider.FileSystem)]
    [InlineData("filesystem", DocumentStorageProvider.FileSystem)]
    [InlineData(" AzureBlob ", DocumentStorageProvider.AzureBlob)]
    [InlineData("azureblob", DocumentStorageProvider.AzureBlob)]
    public void A_recognised_provider_is_selected(string configured, DocumentStorageProvider expected)
    {
        var resolved = DocumentStorageConfiguration.Resolve(new DocumentStorageOptions
        {
            Provider = configured,
            BlobServiceUri = BlobServiceUri,
            ContainerName = "business-documents-dev",
        });

        Assert.Equal(expected, resolved.Provider);
    }

    [Theory]
    [InlineData("Blob")]
    [InlineData("S3")]
    [InlineData("2")]
    public void An_unsupported_provider_is_refused_by_name(string configured)
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            DocumentStorageConfiguration.Resolve(new DocumentStorageOptions { Provider = configured }));

        Assert.Contains("DocumentStorage:Provider", failure.Message, StringComparison.Ordinal);
        Assert.Contains(configured, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_azure_provider_reads_its_endpoint_and_container()
    {
        var resolved = DocumentStorageConfiguration.Resolve(new DocumentStorageOptions
        {
            Provider = "AzureBlob",
            BlobServiceUri = BlobServiceUri,
            ContainerName = "business-documents-dev",
        });

        Assert.Equal(DocumentStorageProvider.AzureBlob, resolved.Provider);
        Assert.Equal(new Uri(BlobServiceUri), resolved.BlobServiceUri);
        Assert.Equal("business-documents-dev", resolved.ContainerName);
    }

    #endregion

    #region Incomplete or unsafe Azure configuration

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void The_azure_provider_without_a_service_uri_is_refused(string? blobServiceUri)
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            DocumentStorageConfiguration.Resolve(new DocumentStorageOptions
            {
                Provider = "AzureBlob",
                BlobServiceUri = blobServiceUri,
                ContainerName = "business-documents-dev",
            }));

        Assert.Contains("DocumentStorage:BlobServiceUri", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void The_azure_provider_without_a_container_is_refused(string? containerName)
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            DocumentStorageConfiguration.Resolve(new DocumentStorageOptions
            {
                Provider = "AzureBlob",
                BlobServiceUri = BlobServiceUri,
                ContainerName = containerName,
            }));

        Assert.Contains("DocumentStorage:ContainerName", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_service_uri_that_is_not_absolute_is_refused()
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            DocumentStorageConfiguration.Resolve(new DocumentStorageOptions
            {
                Provider = "AzureBlob",
                BlobServiceUri = "inventoryappdocs.blob.core.windows.net",
                ContainerName = "business-documents-dev",
            }));

        Assert.Contains("DocumentStorage:BlobServiceUri", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A query string on a storage endpoint is what a SAS token looks like, and user info is
    /// what an embedded credential looks like. This deployment authenticates with a managed
    /// identity; a standing, copyable credential must not be able to arrive through
    /// configuration instead.
    /// </summary>
    [Theory]
    [InlineData("https://inventoryappdocs.blob.core.windows.net/?sv=2024-11-04&sig=redacted")]
    [InlineData("https://account:key@inventoryappdocs.blob.core.windows.net")]
    public void A_service_uri_carrying_a_credential_is_refused(string blobServiceUri)
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            DocumentStorageConfiguration.Resolve(new DocumentStorageOptions
            {
                Provider = "AzureBlob",
                BlobServiceUri = blobServiceUri,
                ContainerName = "business-documents-dev",
            }));

        Assert.Contains("DocumentStorage:BlobServiceUri", failure.Message, StringComparison.Ordinal);
        Assert.Contains("managed identity", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plaintext_service_uri_is_refused()
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            DocumentStorageConfiguration.Resolve(new DocumentStorageOptions
            {
                Provider = "AzureBlob",
                BlobServiceUri = "http://inventoryappdocs.blob.core.windows.net",
                ContainerName = "business-documents-dev",
            }));

        Assert.Contains("https", failure.Message, StringComparison.Ordinal);
    }

    #endregion

    #region Registration

    [Fact]
    public void The_filesystem_provider_registers_the_filesystem_adapter()
    {
        using var provider = Build(new DocumentStorageOptions { Provider = "FileSystem" });
        using var scope = provider.CreateScope();

        Assert.IsType<FileSystemDocumentStorage>(scope.ServiceProvider.GetRequiredService<IDocumentStorage>());
    }

    [Fact]
    public void The_azure_provider_registers_the_blob_adapter()
    {
        using var provider = Build(new DocumentStorageOptions
        {
            Provider = "AzureBlob",
            BlobServiceUri = BlobServiceUri,
            ContainerName = "business-documents-dev",
        });
        using var scope = provider.CreateScope();

        Assert.IsType<AzureBlobDocumentStorage>(scope.ServiceProvider.GetRequiredService<IDocumentStorage>());
    }

    /// <summary>
    /// The filesystem implementation stays resolvable under either provider. Switching the
    /// provider does not move the documents already on disk, so reading them has to remain
    /// possible - the migration in the next checkpoint depends on exactly this.
    /// </summary>
    [Theory]
    [InlineData("FileSystem")]
    [InlineData("AzureBlob")]
    public void The_filesystem_implementation_stays_available_under_either_provider(string configured)
    {
        using var provider = Build(new DocumentStorageOptions
        {
            Provider = configured,
            BlobServiceUri = BlobServiceUri,
            ContainerName = "business-documents-dev",
        });
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<FileSystemDocumentStorage>());
    }

    /// <summary>
    /// The refusal has to happen while the container is being built, not on the first upload
    /// months later.
    /// </summary>
    [Fact]
    public void An_incomplete_azure_configuration_fails_at_registration_rather_than_at_first_use()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Build(new DocumentStorageOptions { Provider = "AzureBlob", ContainerName = "business-documents-dev" }));
    }

    /// <summary>
    /// The one thing that must never happen: an Azure provider that cannot be configured turning
    /// into filesystem storage, quietly, in production.
    /// </summary>
    [Fact]
    public void An_invalid_azure_configuration_never_falls_back_to_the_filesystem()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddDocumentStorage(
            new DocumentStorageOptions { Provider = "AzureBlob", BlobServiceUri = "not a uri" },
            FileSystemOptions()));

        Assert.DoesNotContain(services, service => service.ServiceType == typeof(IDocumentStorage));
    }

    #endregion

    private static ServiceProvider Build(DocumentStorageOptions options)
    {
        var services = new ServiceCollection();

        // The Azure adapter reads the request's business scope; the filesystem one does not.
        // Registering it for both keeps this test about provider selection alone.
        services.AddScoped<IBusinessScope, BusinessScope>();
        services.AddDocumentStorage(options, FileSystemOptions());

        return services.BuildServiceProvider();
    }

    private static FileSystemDocumentStorageOptions FileSystemOptions() => new()
    {
        ContentRootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()),
    };
}
