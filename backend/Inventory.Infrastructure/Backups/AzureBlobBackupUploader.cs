using System.Globalization;
using System.Security.Cryptography;

namespace Inventory.Infrastructure.Backups;

/// <summary>
/// Uploads a verified database snapshot to the private backup container (issue #332).
///
/// Everything this type decides is deliberately independent of the Azure SDK, which lives behind
/// <see cref="IBackupBlobContainer"/>: which objects a snapshot is written to, whether this upload
/// is the month's recovery point, and whether an accepted write counts as a completed upload. The
/// rules it enforces are:
///
/// <list type="bullet">
/// <item><description>
/// <b>Only a verified snapshot is uploaded.</b> The type it accepts can only describe a snapshot
/// that passed <c>PRAGMA integrity_check</c>, and the file is re-hashed against that checksum here
/// before a single byte is sent - so a staged copy that was replaced, truncated or corrupted after
/// verification is refused rather than stored as a recovery point nobody can restore from.
/// </description></item>
/// <item><description>
/// <b>Nothing is ever overwritten.</b> Both writes are conditional creates (<c>If-None-Match: *</c>
/// at the service), so the decision belongs to the service rather than to a check-then-write race
/// here. An occupied daily name stops the upload; an occupied monthly name means the month already
/// has its recovery point and is left exactly as it is.
/// </description></item>
/// <item><description>
/// <b>An accepted write is not a completed upload.</b> Each object is read back - its length and
/// recorded checksum, never its content - so a write the service accepted but did not store as
/// sent is reported as a failure.
/// </description></item>
/// </list>
///
/// The monthly recovery point is created by this flow rather than selected afterwards by a storage
/// lifecycle rule: a rule can expire objects, but nothing in the account knows which daily
/// snapshot a month should keep. Because "first upload of the month" is decided by whether the
/// month's deterministic object already exists, a month whose first scheduled run was missed still
/// gets a recovery point from whichever verified snapshot arrives first - and a retry, or any later
/// upload in the same month, leaves that object untouched.
/// </summary>
public sealed class AzureBlobBackupUploader : IBackupSnapshotUploader
{
    private const string DailyKind = "daily";
    private const string MonthlyKind = "monthly";

    private readonly IBackupBlobContainer _container;

    /// <summary>Creates the uploader over a backup container.</summary>
    public AzureBlobBackupUploader(IBackupBlobContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        _container = container;
    }

    /// <inheritdoc />
    public async Task<BackupUploadOutcome> UploadAsync(
        VerifiedBackupSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (string.IsNullOrWhiteSpace(snapshot.Sha256))
        {
            return Refuse(
                BackupUploadFailureReason.SnapshotNotVerified,
                "The snapshot carries no integrity checksum, so it did not complete verification. "
                    + "Only a verified snapshot is uploaded; nothing was sent.");
        }

        if (!File.Exists(snapshot.LocalPath))
        {
            return Refuse(
                BackupUploadFailureReason.SnapshotMissing,
                "The staged snapshot file is not there, so there is nothing verified to upload.");
        }

        var byteLength = new FileInfo(snapshot.LocalPath).Length;
        var actualSha256 = await ComputeSha256Async(snapshot.LocalPath, cancellationToken);

        if (!string.Equals(actualSha256, snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse(
                BackupUploadFailureReason.SnapshotChecksumMismatch,
                "The staged snapshot no longer matches the checksum its integrity verification "
                    + "produced, so it is no longer the verified snapshot. Nothing was uploaded.");
        }

        var dailyName = BackupObjectNames.Daily(snapshot.CreatedUtc);
        var monthlyName = BackupObjectNames.Monthly(snapshot.CreatedUtc);

        var dailyCreated = await CreateAsync(dailyName, snapshot, DailyKind, cancellationToken);
        if (!dailyCreated)
        {
            return new BackupUploadOutcome(
                Succeeded: false,
                BackupUploadFailureReason.DailyObjectAlreadyExists,
                dailyName,
                MonthlyObjectName: null,
                MonthlyObjectCreated: false,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{dailyName}' already exists. An existing recovery point is never overwritten, "
                        + $"so this snapshot was not uploaded and the {MonthOf(snapshot)} recovery point was not touched."));
        }

        if (!await IsStoredAsSentAsync(dailyName, byteLength, snapshot.Sha256, cancellationToken))
        {
            return NotVerified(dailyName, dailyName, monthlyObjectName: null, monthlyObjectCreated: false);
        }

        // The month's recovery point. The conditional create answers "is this the first upload of
        // this month?" in the same request that would write it, so two uploads racing in the same
        // month cannot both believe they are the first.
        var monthlyCreated = await CreateAsync(monthlyName, snapshot, MonthlyKind, cancellationToken);

        if (monthlyCreated &&
            !await IsStoredAsSentAsync(monthlyName, byteLength, snapshot.Sha256, cancellationToken))
        {
            return NotVerified(monthlyName, dailyName, monthlyName, monthlyObjectCreated: true);
        }

        var monthlyNote = monthlyCreated
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"'{monthlyName}' was created as the {MonthOf(snapshot)} recovery point.")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"The {MonthOf(snapshot)} recovery point '{monthlyName}' already exists and was left unchanged.");

        return new BackupUploadOutcome(
            Succeeded: true,
            BackupUploadFailureReason.None,
            dailyName,
            monthlyName,
            monthlyCreated,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Uploaded and verified {byteLength} byte(s) as '{dailyName}'. {monthlyNote}"));
    }

    private async Task<bool> CreateAsync(
        string blobName, VerifiedBackupSnapshot snapshot, string kind, CancellationToken cancellationToken)
    {
        // Opened per write, because an upload consumes the stream and the two objects are separate
        // writes of the same verified bytes.
        await using var content = File.OpenRead(snapshot.LocalPath);

        return await _container.CreateIfAbsentAsync(
            blobName, content, MetadataFor(snapshot, kind), cancellationToken);
    }

    /// <summary>
    /// Whether the stored object is the snapshot that was sent, from its length and the checksum
    /// recorded on it. The content is never downloaded: the checksum is the verified snapshot's
    /// own, so an object carrying it and matching in length is the snapshot, and an object that
    /// does not is not something to report as a recovery point.
    /// </summary>
    private async Task<bool> IsStoredAsSentAsync(
        string blobName, long expectedByteLength, string expectedSha256, CancellationToken cancellationToken)
    {
        var properties = await _container.GetPropertiesAsync(blobName, cancellationToken);
        if (properties is null) return false;

        return properties.ByteLength == expectedByteLength
            && properties.Metadata.TryGetValue(BackupObjectMetadata.Sha256, out var storedSha256)
            && string.Equals(storedSha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string> MetadataFor(VerifiedBackupSnapshot snapshot, string kind) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BackupObjectMetadata.CreatedUtc] = snapshot.CreatedUtc
                .ToUniversalTime()
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            [BackupObjectMetadata.Sha256] = snapshot.Sha256,
            [BackupObjectMetadata.Kind] = kind,
        };

    private static string MonthOf(VerifiedBackupSnapshot snapshot) =>
        snapshot.CreatedUtc.ToUniversalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static BackupUploadOutcome Refuse(BackupUploadFailureReason reason, string message) =>
        new(
            Succeeded: false,
            reason,
            DailyObjectName: null,
            MonthlyObjectName: null,
            MonthlyObjectCreated: false,
            message);

    private static BackupUploadOutcome NotVerified(
        string failedObjectName,
        string dailyObjectName,
        string? monthlyObjectName,
        bool monthlyObjectCreated) =>
        new(
            Succeeded: false,
            BackupUploadFailureReason.UploadNotVerified,
            dailyObjectName,
            monthlyObjectName,
            monthlyObjectCreated,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The write of '{failedObjectName}' was accepted, but the object read back afterwards is not "
                    + $"the snapshot that was sent. The upload is not complete and must not be treated "
                    + $"as a recovery point."));

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);

        return Convert.ToHexStringLower(hash);
    }
}
