namespace Inventory.Infrastructure.Backups;

/// <summary>
/// A database snapshot that has been taken and has passed integrity verification (issue #332).
///
/// The type is the contract: an uploader only ever receives one of these, and the only caller that
/// builds one is the backup workflow, from a snapshot whose <c>PRAGMA integrity_check</c> returned
/// <c>ok</c> - which is also where <paramref name="Sha256" /> comes from. A file that failed
/// verification, or that was never verified, has no checksum to put here.
/// </summary>
/// <param name="LocalPath">The staged snapshot file, outside the API's content root and web root.</param>
/// <param name="Sha256">The checksum verification computed, lower-case hex.</param>
/// <param name="CreatedUtc">When the snapshot was taken.</param>
public sealed record VerifiedBackupSnapshot(string LocalPath, string Sha256, DateTimeOffset CreatedUtc);

/// <summary>
/// Why a <see cref="BackupUploadOutcome"/> did not succeed. <see cref="None"/> only appears
/// alongside a successful outcome.
/// </summary>
public enum BackupUploadFailureReason
{
    None,

    /// <summary>The staged snapshot file is not there; nothing was uploaded.</summary>
    SnapshotMissing,

    /// <summary>No checksum, so the snapshot never completed integrity verification.</summary>
    SnapshotNotVerified,

    /// <summary>The staged file no longer hashes to the checksum verification produced.</summary>
    SnapshotChecksumMismatch,

    /// <summary>
    /// An object already exists under this snapshot's daily name. It is a recovery point and is
    /// never overwritten, so the upload stops instead.
    /// </summary>
    DailyObjectAlreadyExists,

    /// <summary>
    /// The service accepted a write but the object read back afterwards does not match the
    /// snapshot that was sent, so the upload cannot be reported as completed.
    /// </summary>
    UploadNotVerified,
}

/// <param name="Succeeded">True only when the daily object was stored and verified, and the month has a recovery point.</param>
/// <param name="Failure">Why it failed; <see cref="BackupUploadFailureReason.None"/> on success.</param>
/// <param name="DailyObjectName">The per-snapshot object name, once one was chosen.</param>
/// <param name="MonthlyObjectName">The month's recovery-point object name, once one was chosen.</param>
/// <param name="MonthlyObjectCreated">
/// True when this upload created the month's recovery point; false when the month already had one,
/// which is the ordinary case for every upload after the first in a month and is not a failure.
/// </param>
/// <param name="Message">Operator-facing summary, safe to print: object names, sizes and the checksum only.</param>
public sealed record BackupUploadOutcome(
    bool Succeeded,
    BackupUploadFailureReason Failure,
    string? DailyObjectName,
    string? MonthlyObjectName,
    bool MonthlyObjectCreated,
    string Message);

/// <summary>
/// Stores a verified snapshot outside the App Service instance (issue #332).
///
/// The port exists so the backup workflow - which lives in <c>InventoryApi</c>, runs before the
/// web host is built, and therefore has no service container to resolve anything from - can take a
/// snapshot and upload it without naming a <c>BlobServiceClient</c> or a credential type, which
/// an architecture test enforces. <see cref="BackupSnapshotUploaderFactory"/> is the only place
/// that builds the Azure implementation.
/// </summary>
public interface IBackupSnapshotUploader
{
    /// <summary>Uploads the verified snapshot and reports what was stored.</summary>
    Task<BackupUploadOutcome> UploadAsync(VerifiedBackupSnapshot snapshot, CancellationToken cancellationToken);
}
