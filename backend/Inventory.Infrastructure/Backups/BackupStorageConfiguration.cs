using System.Globalization;

namespace Inventory.Infrastructure.Backups;

/// <summary>
/// The validated result of reading <see cref="BackupStorageOptions"/>. Producing one of these is
/// the only way to build an uploader, so a half-configured destination cannot reach a snapshot.
/// </summary>
/// <param name="BlobServiceUri">The Blob endpoint the snapshot is uploaded to.</param>
/// <param name="ContainerName">The private container holding database snapshots.</param>
public sealed record ResolvedBackupStorageConfiguration(Uri BlobServiceUri, string ContainerName);

/// <summary>
/// Turns the raw <c>BackupStorage</c> settings into a validated destination, or throws
/// (issue #332).
///
/// The failure mode matters more than the success one. An upload that quietly accepted an
/// incomplete configuration would be worse than one that refused: the operator - or the scheduled
/// job - would be told a verified snapshot had been stored off the instance when nothing had left
/// it. Every rejection below therefore names the setting to fix, and the command exits non-zero
/// without taking a snapshot at all.
/// </summary>
public static class BackupStorageConfiguration
{
    /// <summary>Validates <paramref name="options"/> and resolves the upload destination.</summary>
    /// <exception cref="InvalidOperationException">The configuration is incomplete or unusable.</exception>
    public static ResolvedBackupStorageConfiguration Resolve(BackupStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new ResolvedBackupStorageConfiguration(
            ParseBlobServiceUri(options.BlobServiceUri),
            ParseContainerName(options.ContainerName));
    }

    private static Uri ParseBlobServiceUri(string? blobServiceUri)
    {
        if (string.IsNullOrWhiteSpace(blobServiceUri))
        {
            throw new InvalidOperationException(Invalid(
                nameof(BackupStorageOptions.BlobServiceUri),
                "it is required by the upload mode, for example 'https://<storage-account>.blob.core.windows.net'."));
        }

        if (!Uri.TryCreate(blobServiceUri.Trim(), UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(Invalid(
                nameof(BackupStorageOptions.BlobServiceUri),
                "it is not an absolute URI, for example 'https://<storage-account>.blob.core.windows.net'."));
        }

        // A query string on a storage endpoint is what a SAS token looks like, and user info is
        // what an embedded credential looks like. Both are refused outright: the upload
        // authenticates with a managed identity and must not be able to acquire a standing,
        // copyable credential to the backup container by configuration alone.
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(Invalid(
                nameof(BackupStorageOptions.BlobServiceUri),
                "it must be a plain service endpoint. A query string or embedded credentials are not accepted: the backup upload authenticates with a managed identity, never a SAS token, account key or connection string."));
        }

        // https is the in-transit requirement for any real endpoint. Loopback is allowed so a
        // local storage emulator can be pointed at without weakening the rule.
        if (!uri.IsLoopback && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(Invalid(
                nameof(BackupStorageOptions.BlobServiceUri),
                "it must use https, so the snapshot is encrypted in transit."));
        }

        return uri;
    }

    private static string ParseContainerName(string? containerName)
    {
        if (string.IsNullOrWhiteSpace(containerName))
        {
            throw new InvalidOperationException(Invalid(
                nameof(BackupStorageOptions.ContainerName),
                "it is required by the upload mode, for example 'database-backups' in production or 'database-backups-dev' for development. It must be a private container of its own, never the one business documents are stored in."));
        }

        return containerName.Trim();
    }

    private static string Invalid(string setting, string problem) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{BackupStorageOptions.SectionName}:{setting} is invalid: {problem}");
}
