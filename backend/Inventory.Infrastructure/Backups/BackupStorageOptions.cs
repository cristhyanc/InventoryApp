namespace Inventory.Infrastructure.Backups;

/// <summary>
/// The non-secret <c>BackupStorage</c> configuration section, bound from application settings or
/// environment variables (<c>BackupStorage__BlobServiceUri</c>,
/// <c>BackupStorage__ContainerName</c>) and read only by the <c>backup-database --upload</c>
/// command (issue #332).
///
/// It is deliberately separate from <see cref="Documents.DocumentStorageOptions"/> rather than a
/// container name added to it. Database snapshots and business documents have different
/// lifecycles, different retention, and different blast radius: a snapshot is a complete copy of
/// every business's data, so it belongs in its own private container whose data-role assignment
/// can be granted and audited on its own. There is also no provider choice here - uploading is
/// requested explicitly by the operator or the scheduled job, so there is no default to fall back
/// to and no local-filesystem destination to select by accident.
///
/// Nothing here is a secret and nothing here may become one: the upload authenticates with a
/// managed identity through <c>DefaultAzureCredential</c>, so there is no account key, SAS token,
/// client secret or storage connection string to configure.
/// <see cref="BackupStorageConfiguration.Resolve"/> rejects a service URI that carries a query
/// string or credentials, which is what a SAS token or an embedded key would look like.
/// </summary>
public sealed class BackupStorageOptions
{
    /// <summary>The configuration section these settings are bound from.</summary>
    public const string SectionName = "BackupStorage";

    /// <summary>
    /// The Blob service endpoint, for example
    /// <c>https://&lt;storage-account&gt;.blob.core.windows.net</c>. Required by the upload mode.
    /// </summary>
    public string? BlobServiceUri { get; set; }

    /// <summary>
    /// The private container holding database snapshots - for example <c>database-backups</c> in
    /// production and <c>database-backups-dev</c> for development. Required by the upload mode,
    /// and never the container business documents live in.
    /// </summary>
    public string? ContainerName { get; set; }
}
