using Inventory.Infrastructure.Backups;
using InventoryApi.Bootstrap;
using InventoryApi.Tests.Infrastructure.Backups;
using Microsoft.Data.Sqlite;
using Xunit;

namespace InventoryApi.Tests.Operations;

/// <summary>
/// The <c>backup-database --upload</c> workflow (issue #332): take the snapshot, verify it, upload
/// it, and leave no local copy behind.
///
/// These tests drive the orchestration against real on-disk SQLite files and an in-memory stand-in
/// for the container, so they prove the one thing the pieces cannot prove separately - that a
/// snapshot only reaches storage after it passed integrity verification, and that the staged file
/// is removed whether the upload succeeded, failed or threw.
/// </summary>
public sealed class DatabaseBackupUploadRunnerTests : IDisposable
{
    private static readonly DateTimeOffset FirstOfMarch = new(2026, 3, 1, 2, 30, 15, TimeSpan.Zero);

    private readonly string _root;
    private readonly string _sourcePath;
    private readonly string _contentRootPath;
    private readonly string _stagingDirectory;

    public DatabaseBackupUploadRunnerTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "InventoryApp.DatabaseBackupUploadRunnerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _sourcePath = Path.Combine(_root, "source.db");
        _contentRootPath = Path.Combine(_root, "published-content");
        Directory.CreateDirectory(_contentRootPath);
        _stagingDirectory = Path.Combine(_root, "staging");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void CreateSourceDatabase()
    {
        using var source = new SqliteConnection($"Data Source={_sourcePath}");
        source.Open();
        using var command = source.CreateCommand();
        command.CommandText =
            "CREATE TABLE SalesSnapshot (Id INTEGER PRIMARY KEY, Amount REAL NOT NULL);"
            + "INSERT INTO SalesSnapshot (Amount) VALUES (12.50), (7.25);";
        command.ExecuteNonQuery();
    }

    private Task<DatabaseBackupUploadOutcome> RunAsync(
        IBackupSnapshotUploader uploader, TextWriter? output = null, string? stagingDirectory = null) =>
        DatabaseBackupUploadRunner.RunAsync(
            _sourcePath,
            stagingDirectory ?? _stagingDirectory,
            _contentRootPath,
            uploader,
            FirstOfMarch,
            output ?? TextWriter.Null,
            CancellationToken.None);

    private static IReadOnlyCollection<string> StagedFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.GetFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            : [];

    #region The whole workflow

    /// <summary>
    /// The command-to-upload flow end to end: a live database becomes a verified snapshot, the
    /// snapshot becomes a daily object and the month's recovery point, and what landed in storage
    /// is a restorable SQLite database rather than merely a file of the right length.
    /// </summary>
    [Fact]
    public async Task Creates_verifies_and_uploads_the_snapshot_then_removes_the_staged_copy()
    {
        CreateSourceDatabase();
        var container = new InMemoryBackupBlobContainer();

        var outcome = await RunAsync(new AzureBlobBackupUploader(container));

        Assert.True(outcome.Succeeded);
        Assert.Equal(0, outcome.ExitCode);
        Assert.True(outcome.Snapshot.Succeeded);
        Assert.NotNull(outcome.Upload);
        Assert.Equal("daily/inventory-20260301T023015Z.db", outcome.Upload!.DailyObjectName);
        Assert.Equal("monthly/2026-03/inventory-2026-03.db", outcome.Upload.MonthlyObjectName);
        Assert.True(outcome.Upload.MonthlyObjectCreated);
        Assert.True(outcome.StagedCopyRemoved);
        Assert.Empty(StagedFiles(_stagingDirectory));

        var restoredPath = Path.Combine(_root, "restored.db");
        File.WriteAllBytes(restoredPath, container["daily/inventory-20260301T023015Z.db"].Content);
        using var restored = new SqliteConnection($"Data Source={restoredPath}");
        restored.Open();
        using var integrity = restored.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", (string)integrity.ExecuteScalar()!);
        using var count = restored.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM SalesSnapshot;";
        Assert.Equal(2L, (long)count.ExecuteScalar()!);
    }

    /// <summary>
    /// The staged snapshot has to exist while the upload runs and not afterwards - the whole point
    /// of staging it rather than uploading from the live database file.
    /// </summary>
    [Fact]
    public async Task Stages_the_snapshot_for_the_upload_and_leaves_nothing_on_local_disk()
    {
        CreateSourceDatabase();
        var uploader = new RecordingBackupSnapshotUploader();

        var outcome = await RunAsync(uploader);

        var staged = Assert.Single(uploader.Uploads);
        Assert.True(uploader.StagedFileExistedDuringUpload);
        Assert.StartsWith(_stagingDirectory, staged.LocalPath, StringComparison.Ordinal);
        Assert.False(File.Exists(staged.LocalPath));
        Assert.True(outcome.StagedCopyRemoved);
        Assert.Empty(StagedFiles(_stagingDirectory));
    }

    /// <summary>
    /// The uploader is handed the checksum the snapshot's own integrity verification produced, so
    /// "only a verified snapshot is uploaded" is a property of what it receives rather than a
    /// check it is trusted to repeat.
    /// </summary>
    [Fact]
    public async Task Hands_the_uploader_the_verified_checksum_and_creation_time()
    {
        CreateSourceDatabase();
        var uploader = new RecordingBackupSnapshotUploader();

        var outcome = await RunAsync(uploader);

        var staged = Assert.Single(uploader.Uploads);
        Assert.Equal(outcome.Snapshot.Sha256, staged.Sha256);
        Assert.Equal(FirstOfMarch, staged.CreatedUtc);
    }

    #endregion

    #region Nothing unverified is uploaded

    [Fact]
    public async Task Uploads_nothing_when_the_snapshot_could_not_be_taken()
    {
        // No source database exists, so verification never happens.
        var uploader = new RecordingBackupSnapshotUploader();

        var outcome = await RunAsync(uploader);

        Assert.False(outcome.Succeeded);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal(DatabaseBackupFailureReason.SourceMissing, outcome.Snapshot.Failure);
        Assert.Null(outcome.Upload);
        Assert.Empty(uploader.Uploads);
    }

    [Fact]
    public async Task Uploads_nothing_when_the_source_is_not_a_valid_sqlite_database()
    {
        File.WriteAllText(_sourcePath, "not a sqlite database");
        var uploader = new RecordingBackupSnapshotUploader();

        var outcome = await RunAsync(uploader);

        Assert.False(outcome.Succeeded);
        Assert.Equal(DatabaseBackupFailureReason.SourceCannotBeOpened, outcome.Snapshot.Failure);
        Assert.Empty(uploader.Uploads);
        Assert.Empty(StagedFiles(_stagingDirectory));
    }

    /// <summary>
    /// A staging location inside the API's content root (which contains <c>wwwroot</c>) is refused
    /// by the same guard the retained-snapshot mode uses, so the upload workflow cannot put a
    /// database snapshot anywhere the application serves files from, even temporarily.
    /// </summary>
    [Fact]
    public async Task Refuses_to_stage_inside_the_web_root_or_published_content()
    {
        CreateSourceDatabase();
        var uploader = new RecordingBackupSnapshotUploader();

        var outcome = await RunAsync(
            uploader, stagingDirectory: Path.Combine(_contentRootPath, "wwwroot", "backup-staging"));

        Assert.False(outcome.Succeeded);
        Assert.Equal(DatabaseBackupFailureReason.OutputInsideContentRoot, outcome.Snapshot.Failure);
        Assert.Empty(uploader.Uploads);
    }

    #endregion

    #region Cleanup

    [Fact]
    public async Task Removes_the_staged_copy_after_a_failed_upload()
    {
        CreateSourceDatabase();
        var uploader = new RecordingBackupSnapshotUploader
        {
            Result = new BackupUploadOutcome(
                Succeeded: false,
                BackupUploadFailureReason.DailyObjectAlreadyExists,
                DailyObjectName: "daily/inventory-20260301T023015Z.db",
                MonthlyObjectName: null,
                MonthlyObjectCreated: false,
                Message: "The daily object already exists."),
        };

        var outcome = await RunAsync(uploader);

        Assert.False(outcome.Succeeded);
        Assert.Equal(1, outcome.ExitCode);
        Assert.True(outcome.StagedCopyRemoved);
        Assert.Empty(StagedFiles(_stagingDirectory));
    }

    /// <summary>
    /// The cleanup is a <c>finally</c> path, not a step on the success route: an uploader that
    /// throws - an unreachable storage account, a refused role assignment - must not leave a copy
    /// of the business's database on the App Service's local disk.
    /// </summary>
    [Fact]
    public async Task Removes_the_staged_copy_when_the_upload_throws()
    {
        CreateSourceDatabase();
        var uploader = new RecordingBackupSnapshotUploader
        {
            ThrowWith = new InvalidOperationException("storage unreachable"),
        };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(uploader));

        Assert.Equal("storage unreachable", failure.Message);
        Assert.Empty(StagedFiles(_stagingDirectory));
    }

    /// <summary>
    /// Two runs can overlap: the scheduled job and an operator running the command by hand, or a
    /// slow upload still in flight when the next run starts. The staged path is derived from a UTC
    /// instant precise only to the second, so both can name the same second - and the cleanup is
    /// unconditional. Each invocation therefore stages into its own directory: the second run must
    /// take and upload its own snapshot rather than being refused by the "destination must not
    /// already exist" guard, and must not delete the first run's staged file (or its SQLite
    /// sidecars) while the first run is still uploading it.
    ///
    /// The overlap is created deterministically by starting the second run from inside the first
    /// run's upload, which is exactly the window in which the staged file must survive.
    /// </summary>
    [Fact]
    public async Task An_overlapping_run_at_the_same_instant_neither_collides_with_nor_deletes_the_first_runs_staged_copy()
    {
        CreateSourceDatabase();
        var overlappingUploader = new RecordingBackupSnapshotUploader();
        DatabaseBackupUploadOutcome? overlapping = null;
        var firstUploader = new RecordingBackupSnapshotUploader
        {
            DuringUpload = async () => overlapping = await RunAsync(overlappingUploader),
        };

        var first = await RunAsync(firstUploader);

        // The first run's staged snapshot was still there after the overlapping run had finished
        // - taken its own snapshot, uploaded it, and run its own cleanup.
        Assert.True(firstUploader.StagedFileExistedDuringUpload);
        Assert.True(firstUploader.StagedFileExistedAfterOverlap);

        Assert.NotNull(overlapping);
        Assert.Equal(DatabaseBackupFailureReason.None, overlapping!.Snapshot.Failure);
        Assert.True(overlapping.Succeeded);
        Assert.True(first.Succeeded);

        var firstStaged = Assert.Single(firstUploader.Uploads);
        var overlappingStaged = Assert.Single(overlappingUploader.Uploads);
        Assert.NotEqual(firstStaged.LocalPath, overlappingStaged.LocalPath);
        Assert.NotEqual(
            Path.GetDirectoryName(firstStaged.LocalPath), Path.GetDirectoryName(overlappingStaged.LocalPath));

        // Both runs cleaned up after themselves, and neither left its own directory behind.
        Assert.True(first.StagedCopyRemoved);
        Assert.True(overlapping.StagedCopyRemoved);
        Assert.Empty(StagedFiles(_stagingDirectory));
    }

    /// <summary>
    /// The cleanup deletes only what this invocation created. A file already sitting in the staging
    /// root - another run's snapshot, or something an operator put there - survives untouched, even
    /// when it carries exactly the name this run's snapshot would once have had, and even when it
    /// has SQLite sidecar files beside it.
    /// </summary>
    [Fact]
    public async Task Leaves_a_pre_existing_file_in_the_staging_directory_untouched()
    {
        CreateSourceDatabase();
        Directory.CreateDirectory(_stagingDirectory);
        var preExisting = Path.Combine(_stagingDirectory, "inventory-20260301T023015Z.db");
        File.WriteAllText(preExisting, "another run's snapshot");
        File.WriteAllText(preExisting + "-wal", "another run's write-ahead log");
        File.WriteAllText(preExisting + "-shm", "another run's shared memory file");

        var outcome = await RunAsync(new AzureBlobBackupUploader(new InMemoryBackupBlobContainer()));

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.StagedCopyRemoved);
        Assert.Equal("another run's snapshot", File.ReadAllText(preExisting));
        Assert.Equal("another run's write-ahead log", File.ReadAllText(preExisting + "-wal"));
        Assert.Equal("another run's shared memory file", File.ReadAllText(preExisting + "-shm"));

        // Nothing but those three pre-existing files is left: this run removed its own staged copy
        // and its own directory.
        Assert.Equal(3, StagedFiles(_stagingDirectory).Count);
    }

    #endregion

    #region Reporting

    [Fact]
    public async Task Reports_the_objects_it_wrote_without_printing_the_snapshot_contents()
    {
        CreateSourceDatabase();
        var container = new InMemoryBackupBlobContainer();
        var writer = new StringWriter();

        await RunAsync(new AzureBlobBackupUploader(container), writer);

        var report = writer.ToString();
        Assert.Contains("daily/inventory-20260301T023015Z.db", report, StringComparison.Ordinal);
        Assert.Contains("monthly/2026-03/inventory-2026-03.db", report, StringComparison.Ordinal);
        Assert.DoesNotContain("SalesSnapshot", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sig=", report, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    /// <summary>
    /// A stand-in uploader that records what the workflow handed it, and can fail or throw on
    /// demand. It checks the staged file's existence while the "upload" is in progress, which is
    /// the only moment that can be observed from outside. <see cref="DuringUpload"/> runs in that
    /// same window, so a test can make a second run overlap this one deterministically and then
    /// see whether this run's staged file survived it.
    /// </summary>
    private sealed class RecordingBackupSnapshotUploader : IBackupSnapshotUploader
    {
        public List<VerifiedBackupSnapshot> Uploads { get; } = [];

        public bool StagedFileExistedDuringUpload { get; private set; }

        /// <summary>Null when <see cref="DuringUpload"/> was not set.</summary>
        public bool? StagedFileExistedAfterOverlap { get; private set; }

        public Func<Task>? DuringUpload { get; set; }

        public Exception? ThrowWith { get; set; }

        public BackupUploadOutcome? Result { get; set; }

        public async Task<BackupUploadOutcome> UploadAsync(
            VerifiedBackupSnapshot snapshot, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            Uploads.Add(snapshot);
            StagedFileExistedDuringUpload = File.Exists(snapshot.LocalPath);

            if (DuringUpload is not null)
            {
                await DuringUpload();
                StagedFileExistedAfterOverlap = File.Exists(snapshot.LocalPath);
            }

            if (ThrowWith is not null) throw ThrowWith;

            return Result ?? new BackupUploadOutcome(
                Succeeded: true,
                BackupUploadFailureReason.None,
                DailyObjectName: "daily/inventory-20260301T023015Z.db",
                MonthlyObjectName: "monthly/2026-03/inventory-2026-03.db",
                MonthlyObjectCreated: true,
                Message: "Uploaded.");
        }
    }
}
