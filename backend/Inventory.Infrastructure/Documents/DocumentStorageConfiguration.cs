using System.Globalization;

namespace Inventory.Infrastructure.Documents;

/// <summary>
/// The validated result of reading <see cref="DocumentStorageOptions"/>. Producing one of these
/// is the only way to select a provider, so a half-configured provider cannot reach a running
/// adapter.
/// </summary>
/// <param name="Provider">The selected implementation.</param>
/// <param name="BlobServiceUri">The Blob endpoint; non-null exactly when the provider is AzureBlob.</param>
/// <param name="ContainerName">The Blob container; non-null exactly when the provider is AzureBlob.</param>
public sealed record ResolvedDocumentStorageConfiguration(
    DocumentStorageProvider Provider,
    Uri? BlobServiceUri,
    string? ContainerName);

/// <summary>
/// Turns the raw <c>DocumentStorage</c> settings into a validated selection, or throws.
///
/// The failure mode matters more than the success one: an invalid or incomplete Azure
/// configuration must never quietly fall back to the filesystem. That would put a business's
/// documents somewhere nobody expected them, on a machine that may be recycled, while the
/// application looked healthy. Every rejection below therefore names the setting to fix.
/// </summary>
public static class DocumentStorageConfiguration
{
    /// <summary>Validates <paramref name="options"/> and selects a provider.</summary>
    /// <exception cref="InvalidOperationException">The configuration is unsupported or incomplete.</exception>
    public static ResolvedDocumentStorageConfiguration Resolve(DocumentStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var provider = ParseProvider(options.Provider);

        return provider == DocumentStorageProvider.FileSystem
            ? new ResolvedDocumentStorageConfiguration(provider, BlobServiceUri: null, ContainerName: null)
            : new ResolvedDocumentStorageConfiguration(
                provider,
                ParseBlobServiceUri(options.BlobServiceUri),
                ParseContainerName(options.ContainerName));
    }

    private static DocumentStorageProvider ParseProvider(string? provider)
    {
        // Absent means FileSystem: that is what every environment ran before this setting
        // existed, so adding the setting does not silently move anyone's documents.
        if (string.IsNullOrWhiteSpace(provider)) return DocumentStorageProvider.FileSystem;

        // Enum.TryParse would also accept "2" and silently mean AzureBlob. Configuration names a
        // provider; a number in this setting is a mistake, not a selection.
        var name = provider.Trim();
        if (!int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) &&
            Enum.TryParse<DocumentStorageProvider>(name, ignoreCase: true, out var parsed) &&
            Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(Invalid(
            nameof(DocumentStorageOptions.Provider),
            string.Create(
                CultureInfo.InvariantCulture,
                $"'{provider}' is not a supported document-storage provider. Use '{nameof(DocumentStorageProvider.FileSystem)}' or '{nameof(DocumentStorageProvider.AzureBlob)}'.")));
    }

    private static Uri ParseBlobServiceUri(string? blobServiceUri)
    {
        if (string.IsNullOrWhiteSpace(blobServiceUri))
        {
            throw new InvalidOperationException(Invalid(
                nameof(DocumentStorageOptions.BlobServiceUri),
                "it is required by the AzureBlob provider, for example 'https://<storage-account>.blob.core.windows.net'."));
        }

        if (!Uri.TryCreate(blobServiceUri.Trim(), UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(Invalid(
                nameof(DocumentStorageOptions.BlobServiceUri),
                "it is not an absolute URI, for example 'https://<storage-account>.blob.core.windows.net'."));
        }

        // A query string on a storage endpoint is what a SAS token looks like, and user info is
        // what an embedded credential looks like. Both are refused outright: this deployment
        // authenticates with a managed identity and must not be able to acquire a standing,
        // copyable credential by configuration alone.
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(Invalid(
                nameof(DocumentStorageOptions.BlobServiceUri),
                "it must be a plain service endpoint. A query string or embedded credentials are not accepted: document storage authenticates with a managed identity, never a SAS token, account key or connection string."));
        }

        // Loopback is allowed so a local storage emulator can be pointed at without weakening
        // the rule for any real endpoint.
        if (!uri.IsLoopback && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(Invalid(
                nameof(DocumentStorageOptions.BlobServiceUri),
                "it must use https."));
        }

        return uri;
    }

    private static string ParseContainerName(string? containerName)
    {
        if (string.IsNullOrWhiteSpace(containerName))
        {
            throw new InvalidOperationException(Invalid(
                nameof(DocumentStorageOptions.ContainerName),
                "it is required by the AzureBlob provider, for example 'business-documents' in production or 'business-documents-dev' for development."));
        }

        return containerName.Trim();
    }

    private static string Invalid(string setting, string problem) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{DocumentStorageOptions.SectionName}:{setting} is invalid: {problem}");
}
