namespace Inventory.Infrastructure.Documents;

/// <summary>
/// Which document-storage implementation the application runs on.
/// </summary>
public enum DocumentStorageProvider
{
    /// <summary>
    /// <see cref="FileSystemDocumentStorage"/>: documents under the content root, with the
    /// legacy web-root fallback. This is the default, and the provider every environment used
    /// before issue #39.
    /// </summary>
    FileSystem = 1,

    /// <summary>
    /// <see cref="AzureBlobDocumentStorage"/>: documents in a private Azure Blob container,
    /// keyed by the trusted current business.
    /// </summary>
    AzureBlob = 2,
}

/// <summary>
/// The non-secret <c>DocumentStorage</c> configuration section, bound from application settings
/// or environment variables (<c>DocumentStorage__Provider</c>, <c>DocumentStorage__BlobServiceUri</c>,
/// <c>DocumentStorage__ContainerName</c>).
///
/// Nothing here is a secret and nothing here may become one: the Azure adapter authenticates
/// with a managed identity through <c>DefaultAzureCredential</c>, so there is no account key,
/// SAS token, client secret or storage connection string to configure.
/// <see cref="DocumentStorageConfiguration.Resolve"/> rejects a service URI that carries a query
/// string or credentials, which is what a SAS token or an embedded key would look like.
/// </summary>
public sealed class DocumentStorageOptions
{
    /// <summary>The configuration section these settings are bound from.</summary>
    public const string SectionName = "DocumentStorage";

    /// <summary>
    /// <c>FileSystem</c> or <c>AzureBlob</c>. Absent or empty means <c>FileSystem</c>, which is
    /// what every environment ran before this setting existed; an unrecognised value is an
    /// error rather than a silent default.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// The Blob service endpoint, for example
    /// <c>https://&lt;storage-account&gt;.blob.core.windows.net</c>. Required by
    /// <see cref="DocumentStorageProvider.AzureBlob"/>.
    /// </summary>
    public string? BlobServiceUri { get; set; }

    /// <summary>
    /// The private container holding business documents - <c>business-documents</c> in
    /// production, <c>business-documents-dev</c> for development. Required by
    /// <see cref="DocumentStorageProvider.AzureBlob"/>.
    /// </summary>
    public string? ContainerName { get; set; }
}
