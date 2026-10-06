using System.Security.Cryptography;
using System.Text;
using Inventory.Infrastructure.Backups;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Backups;

/// <summary>
/// The verified-snapshot uploader (issue #332).
///
/// These tests run against <see cref="InMemoryBackupBlobContainer"/>, so the suite needs no
/// Azure account, credentials or network. What they exercise is everything the uploader decides:
/// which object a snapshot goes to, whether this upload is the month's recovery point, what it
/// records about the snapshot, and - the reason it exists in this shape - that only a snapshot
/// whose bytes still match its verified checksum is ever sent, and that an existing object is
/// never overwritten.
/// </summary>
public sealed class AzureBlobBackupUploaderTests : IDisposable
{
    private static readonly DateTimeOffset FirstOfMarch =
        new(2026, 3, 1, 2, 30, 15, TimeSpan.Zero);

    private readonly string _root;

    public AzureBlobBackupUploaderTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "InventoryApp.AzureBlobBackupUploaderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Writes a staged snapshot file and returns it paired with its real checksum.</summary>
    private VerifiedBackupSnapshot StageSnapshot(string content, DateTimeOffset createdUtc)
    {
        var path = Path.Combine(_root, $"snapshot-{Guid.NewGuid():N}.db");
        var bytes = Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(path, bytes);

        return new VerifiedBackupSnapshot(path, Sha256Of(bytes), createdUtc);
    }

    private static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    #region Daily object

    [Fact]
    public async Task Writes_the_verified_snapshot_under_the_daily_prefix_with_a_utc_timestamped_name()
    {
        var container = new InMemoryBackupBlobContainer();
        var snapshot = StageSnapshot("verified snapshot", FirstOfMarch);

        var outcome = await new AzureBlobBackupUploader(container).UploadAsync(snapshot, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal("daily/inventory-20260301T023015Z.db", outcome.DailyObjectName);
        Assert.True(container.Contains("daily/inventory-20260301T023015Z.db"));
        Assert.Equal(
            File.ReadAllBytes(snapshot.LocalPath),
            container["daily/inventory-20260301T023015Z.db"].Content);
    }

    /// <summary>
    /// The timestamp is the snapshot's UTC instant regardless of the machine's local offset, so
    /// two hosts in different zones cannot produce the same name for different snapshots (or
    /// different names for the same one).
    /// </summary>
    [Fact]
    public async Task The_daily_name_uses_utc_even_when_the_snapshot_time_carries_an_offset()
    {
        var container = new InMemoryBackupBlobContainer();
        var sydneyMorning = new DateTimeOffset(2026, 3, 1, 13, 30, 15, TimeSpan.FromHours(11));
        var snapshot = StageSnapshot("verified snapshot", sydneyMorning);

        var outcome = await new AzureBlobBackupUploader(container).UploadAsync(snapshot, CancellationToken.None);

        Assert.Equal("daily/inventory-20260301T023015Z.db", outcome.DailyObjectName);
    }

    /// <summary>
    /// A daily name that is already taken is a conflict, not something to resolve by overwriting:
    /// the object already there is a recovery point. The month's object is not attempted either -
    /// an upload that could not place its own daily copy has nothing verified to promote.
    /// </summary>
    [Fact]
    public async Task Refuses_to_overwrite_an_existing_daily_object_and_does_not_attempt_the_monthly_one()
    {
        var container = new InMemoryBackupBlobContainer();
        container.Put("daily/inventory-20260301T023015Z.db", Encoding.UTF8.GetBytes("an earlier snapshot"));
        var snapshot = StageSnapshot("verified snapshot", FirstOfMarch);

        var outcome = await new AzureBlobBackupUploader(container).UploadAsync(snapshot, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(BackupUploadFailureReason.DailyObjectAlreadyExists, outcome.Failure);
        Assert.Equal(
            "an earlier snapshot",
            Encoding.UTF8.GetString(container["daily/inventory-20260301T023015Z.db"].Content));
        Assert.DoesNotContain(
            container.CreateAttempts,
            name => name.StartsWith(BackupObjectNames.MonthlyPrefix, StringComparison.Ordinal));
    }

    #endregion

    #region Monthly recovery point

    [Fact]
    public async Task Writes_the_months_recovery_point_on_the_first_successful_upload_of_the_month()
    {
        var container = new InMemoryBackupBlobContainer();
        var snapshot = StageSnapshot("verified snapshot", FirstOfMarch);

        var outcome = await new AzureBlobBackupUploader(container).UploadAsync(snapshot, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.MonthlyObjectCreated);
        Assert.Equal("monthly/2026-03/inventory-2026-03.db", outcome.MonthlyObjectName);
        Assert.Equal(
            File.ReadAllBytes(snapshot.LocalPath),
            container["monthly/2026-03/inventory-2026-03.db"].Content);
    }

    /// <summary>
    /// The missed-first-day case: nothing ran on 1 March, so the first upload of the month is on
    /// the 17th. The month must still end up with a recovery point - "first upload of the month",
    /// not "upload on the first of the month", is what creates it.
    /// </summary>
    [Fact]
    public async Task A_first_upload_later_in_the_month_still_creates_that_months_recovery_point()
    {
        var container = new InMemoryBackupBlobContainer();
        var seventeenthOfMarch = new DateTimeOffset(2026, 3, 17, 18, 5, 0, TimeSpan.Zero);
        var snapshot = StageSnapshot("verified snapshot", seventeenthOfMarch);

        var outcome = await new AzureBlobBackupUploader(container).UploadAsync(snapshot, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.MonthlyObjectCreated);
        Assert.Equal("monthly/2026-03/inventory-2026-03.db", outcome.MonthlyObjectName);
        Assert.Equal("daily/inventory-20260317T180500Z.db", outcome.DailyObjectName);
    }

    /// <summary>
    /// The idempotence rule. A later upload in the same month still takes its own daily object,
    /// but the month's recovery point is whatever was created first and is left exactly as it is -
    /// decided by the conditional create on a deterministic name, not by a read-then-write the
    /// second uploader could lose.
    /// </summary>
    [Fact]
    public async Task A_second_upload_in_the_same_month_leaves_the_existing_monthly_object_unchanged()
    {
        var container = new InMemoryBackupBlobContainer();
        var uploader = new AzureBlobBackupUploader(container);
        var first = StageSnapshot("first snapshot of March", FirstOfMarch);
        var second = StageSnapshot(
            "second snapshot of March", new DateTimeOffset(2026, 3, 2, 2, 30, 15, TimeSpan.Zero));

        var firstOutcome = await uploader.UploadAsync(first, CancellationToken.None);
        var secondOutcome = await uploader.UploadAsync(second, CancellationToken.None);

        Assert.True(firstOutcome.Succeeded);
        Assert.True(firstOutcome.MonthlyObjectCreated);

        Assert.True(secondOutcome.Succeeded);
        Assert.False(secondOutcome.MonthlyObjectCreated);
        Assert.Equal("monthly/2026-03/inventory-2026-03.db", secondOutcome.MonthlyObjectName);
        Assert.Equal("daily/inventory-20260302T023015Z.db", secondOutcome.DailyObjectName);

        // Both daily objects are kept, and the month still holds the first snapshot's bytes.
        Assert.True(container.Contains("daily/inventory-20260301T023015Z.db"));
        Assert.True(container.Contains("daily/inventory-20260302T023015Z.db"));
        Assert.Equal(
            "first snapshot of March",
            Encoding.UTF8.GetString(container["monthly/2026-03/inventory-2026-03.db"].Content));
    }

    [Fact]
    public async Task A_new_month_gets_its_own_recovery_point()
    {
        var container = new InMemoryBackupBlobContainer();
        var uploader = new AzureBlobBackupUploader(container);
        var march = StageSnapshot("March snapshot", FirstOfMarch);
        var april = StageSnapshot("April snapshot", new DateTimeOffset(2026, 4, 1, 2, 30, 15, TimeSpan.Zero));

        await uploader.UploadAsync(march, CancellationToken.None);
        var aprilOutcome = await uploader.UploadAsync(april, CancellationToken.None);

        Assert.True(aprilOutcome.MonthlyObjectCreated);
        Assert.Equal("monthly/2026-04/inventory-2026-04.db", aprilOutcome.MonthlyObjectName);
        Assert.Equal(
            "March snapshot",
            Encoding.UTF8.GetString(container["monthly/2026-03/inventory-2026-03.db"].Content));
    }

    #endregion

    #region Only a verified snapshot

    [Fact]
    public async Task Refuses_a_snapshot_whose_file_is_not_there_and_uploads_nothing()
    {
        var container = new InMemoryBackupBlobContainer();
        var missing = new VerifiedBackupSnapshot(
            Path.Combine(_root, "does-not-exist.db"), new string('a', 64), FirstOfMarch);

        var outcome = await new AzureBlobBackupUploader(container).UploadAsync(missing, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(BackupUploadFailureReason.SnapshotMissing, outcome.Failure);
        Assert.Empty(container.CreateAttempts);
    }

    /// <summary>
    /// No checksum means the snapshot never completed integrity verification - the producer only
    /// reports one for a snapshot that passed <c>PRAGMA integrity_check</c>. An unverified file
    /// must not become a recovery point.
    /// </summary>
    [Fact]
    public async Task Refuses_a_snapshot_with_no_verified_checksum_and_uploads_nothing()
    {
        var container = new InMemoryBackupBlobContainer();
        var staged = StageSnapshot("verified snapshot", FirstOfMarch);
        var unverified = staged with { Sha256 = "   " };

        var outcome = await new AzureBlobBackupUploader(container).UploadAsync(unverified, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(BackupUploadFailureReason.SnapshotNotVerified, outcome.Failure);
        Assert.Empty(container.CreateAttempts);
    }

    /// <summary>
    /// The file on disk is re-hashed against the checksum verification produced, so a staged copy
    /// that was replaced, truncated or corrupted between verification and upload is refused
    /// rather than stored as a trustworthy recovery point.
    /// </summary>
    [Fact]
    public async Task Refuses_a_snapshot_whose_bytes_no_longer_match_the_verified_checksum()
    {
        var container = new InMemoryBackupBlobContainer();
        var snapshot = StageSnapshot("verified snapshot", FirstOfMarch);
        File.WriteAllText(snapshot.LocalPath, "something else entirely");

        var outcome = await new AzureBlobBackupUploader(container).UploadAsync(snapshot, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(BackupUploadFailureReason.SnapshotChecksumMismatch, outcome.Failure);
        Assert.Empty(container.CreateAttempts);
    }

    #endregion

    #region Metadata and upload verification

    [Fact]
    public async Task Records_the_creation_time_checksum_and_kind_as_object_metadata()
    {
        var container = new InMemoryBackupBlobContainer();
        var snapshot = StageSnapshot("verified snapshot", FirstOfMarch);

        await new AzureBlobBackupUploader(container).UploadAsync(snapshot, CancellationToken.None);

        var daily = container["daily/inventory-20260301T023015Z.db"].Metadata;
        Assert.Equal("2026-03-01T02:30:15Z", daily[BackupObjectMetadata.CreatedUtc]);
        Assert.Equal(snapshot.Sha256, daily[BackupObjectMetadata.Sha256]);
        Assert.Equal("daily", daily[BackupObjectMetadata.Kind]);

        var monthly = container["monthly/2026-03/inventory-2026-03.db"].Metadata;
        Assert.Equal("2026-03-01T02:30:15Z", monthly[BackupObjectMetadata.CreatedUtc]);
        Assert.Equal(snapshot.Sha256, monthly[BackupObjectMetadata.Sha256]);
        Assert.Equal("monthly", monthly[BackupObjectMetadata.Kind]);
    }

    /// <summary>
    /// An accepted write is not a completed upload. The uploader reads each object back and
    /// compares its length and recorded checksum, so an upload that stored something other than
    /// the snapshot is reported as a failure instead of as a recovery point nobody can restore.
    /// </summary>
    [Fact]
    public async Task Fails_when_the_stored_object_does_not_match_the_snapshot_that_was_sent()
    {
        var container = new InMemoryBackupBlobContainer
        {
            CorruptCreatedBlobWith = blob => blob with { Content = [.. blob.Content, (byte)'!'] },
        };
        var snapshot = StageSnapshot("verified snapshot", FirstOfMarch);

        var outcome = await new AzureBlobBackupUploader(container).UploadAsync(snapshot, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(BackupUploadFailureReason.UploadNotVerified, outcome.Failure);
        Assert.Contains("daily/inventory-20260301T023015Z.db", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The operator-facing message is the only thing this reports, and a snapshot's contents are
    /// business data: the message names objects, sizes and the checksum, never bytes.
    /// </summary>
    [Fact]
    public async Task Reports_the_upload_without_echoing_the_snapshot_contents()
    {
        var container = new InMemoryBackupBlobContainer();
        var snapshot = StageSnapshot("TOP SECRET SALES ROW", FirstOfMarch);

        var outcome = await new AzureBlobBackupUploader(container).UploadAsync(snapshot, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.DoesNotContain("TOP SECRET", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
