using Inventory.Infrastructure.Backups;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Backups;

/// <summary>
/// The backup-storage configuration gate (issue #332). Producing a resolved configuration is the
/// only way to build an uploader, so these are the rules a misconfigured deployment hits before
/// any snapshot is taken - and, most importantly, the refusal of anything that looks like a
/// standing credential: this upload authenticates with a managed identity and must not be able to
/// acquire an account key or SAS token by configuration alone.
/// </summary>
public sealed class BackupStorageConfigurationTests
{
    private static BackupStorageOptions Valid() => new()
    {
        BlobServiceUri = "https://examplestorage.blob.core.windows.net",
        ContainerName = "database-backups",
    };

    [Fact]
    public void Resolves_a_complete_configuration()
    {
        var resolved = BackupStorageConfiguration.Resolve(Valid());

        Assert.Equal(new Uri("https://examplestorage.blob.core.windows.net"), resolved.BlobServiceUri);
        Assert.Equal("database-backups", resolved.ContainerName);
    }

    [Fact]
    public void Trims_surrounding_whitespace_rather_than_building_an_unusable_container_name()
    {
        var resolved = BackupStorageConfiguration.Resolve(new BackupStorageOptions
        {
            BlobServiceUri = " https://examplestorage.blob.core.windows.net ",
            ContainerName = " database-backups ",
        });

        Assert.Equal("database-backups", resolved.ContainerName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Requires_a_blob_service_uri(string? blobServiceUri)
    {
        var options = Valid();
        options.BlobServiceUri = blobServiceUri;

        var error = Assert.Throws<InvalidOperationException>(() => BackupStorageConfiguration.Resolve(options));

        Assert.Contains("BackupStorage:BlobServiceUri", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Requires_a_container_name(string? containerName)
    {
        var options = Valid();
        options.ContainerName = containerName;

        var error = Assert.Throws<InvalidOperationException>(() => BackupStorageConfiguration.Resolve(options));

        Assert.Contains("BackupStorage:ContainerName", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_relative_blob_service_uri()
    {
        var options = Valid();
        options.BlobServiceUri = "examplestorage.blob.core.windows.net";

        var error = Assert.Throws<InvalidOperationException>(() => BackupStorageConfiguration.Resolve(options));

        Assert.Contains("BackupStorage:BlobServiceUri", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A query string on a storage endpoint is what a SAS token looks like, and user info is what
    /// an embedded key looks like. Accepting either would turn a non-secret setting into a
    /// standing, copyable credential for the backup container.
    /// </summary>
    [Theory]
    [InlineData("https://examplestorage.blob.core.windows.net?sv=2024-11-04&sig=redacted")]
    [InlineData("https://account:key@examplestorage.blob.core.windows.net")]
    public void Rejects_an_endpoint_carrying_a_sas_token_or_embedded_credentials(string blobServiceUri)
    {
        var options = Valid();
        options.BlobServiceUri = blobServiceUri;

        var error = Assert.Throws<InvalidOperationException>(() => BackupStorageConfiguration.Resolve(options));

        Assert.Contains("managed identity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_a_plain_http_endpoint()
    {
        var options = Valid();
        options.BlobServiceUri = "http://examplestorage.blob.core.windows.net";

        var error = Assert.Throws<InvalidOperationException>(() => BackupStorageConfiguration.Resolve(options));

        Assert.Contains("https", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Loopback stays allowed so a local storage emulator can be pointed at, without weakening
    /// the in-transit rule for any real endpoint.
    /// </summary>
    [Fact]
    public void Allows_a_loopback_emulator_endpoint()
    {
        var options = Valid();
        options.BlobServiceUri = "http://127.0.0.1:10000/devstoreaccount1";

        var resolved = BackupStorageConfiguration.Resolve(options);

        Assert.Equal(new Uri("http://127.0.0.1:10000/devstoreaccount1"), resolved.BlobServiceUri);
    }
}
