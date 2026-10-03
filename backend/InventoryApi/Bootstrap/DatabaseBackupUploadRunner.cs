using System.Globalization;
using Inventory.Infrastructure.Backups;

namespace InventoryApi.Bootstrap;

/// <param name="Succeeded">True only when the snapshot was verified, both uploads are accounted for, and no local copy was left behind.</param>
/// <param name="Snapshot">The snapshot step's own outcome; always present, because it always runs.</param>
/// <param name="Upload">The upload step's outcome, or <c>null</c> when the snapshot never qualified for one.</param>
/// <param name="StagedCopyRemoved">Whether the staged snapshot (and any SQLite sidecar files) are gone.</param>
/// <param name="Message">Operator-facing summary, safe to print.</param>
public sealed record DatabaseBackupUploadOutcome(
    bool Succeeded,
    DatabaseBackupOutcome Snapshot,
    BackupUploadOutcome? Upload,
    bool StagedCopyRemoved,
    string Message)
{
    /// <summary>Non-zero on any failure, so the process exit code reflects the result directly.</summary>
    public int ExitCode => Succeeded ? 0 : 1;
}

/// <summary>
/// The create-verify-upload workflow behind <c>backup-database --upload</c> (issue #332), against
/// plain paths and the <see cref="IBackupSnapshotUploader"/> port rather than a built
/// <c>WebApplication</c> or an Azure client, so it can be driven end to end in tests against real
/// on-disk SQLite files and an in-memory container.
///
/// The three properties it exists to guarantee, in order:
///
/// <list type="number">
/// <item><description>
/// <b>Nothing unverified is uploaded.</b> The snapshot is taken by the same
/// <see cref="DatabaseBackupRunner"/> the retained-snapshot mode uses - including its
/// <c>PRAGMA integrity_check</c> and its refusal to write inside the API's content root or web
/// root - and the uploader is only called when that succeeded. The checksum handed to the uploader
/// is the one verification computed; there is no path through this method that sends a file
/// verification did not approve.
/// </description></item>
/// <item><description>
/// <b>The snapshot is staged outside the served content.</b> The staging directory is supplied by
/// the caller and validated by the snapshot runner's existing content-root guard, so the temporary
/// copy can never sit under <c>wwwroot</c> or anywhere else a redeploy would capture or the
/// application would serve.
/// </description></item>
/// <item><description>
/// <b>The staged copy is removed either way.</b> Cleanup is a <c>finally</c> path, so an upload
/// that failed, or that threw because the storage account was unreachable or the role assignment
/// was refused, still does not leave a complete copy of every business's data on the instance's
/// local disk. A cleanup that could not finish is reported and fails the command rather than being
/// swallowed.
/// </description></item>
/// </list>
/// </summary>
public static class DatabaseBackupUploadRunner
{
    public static async Task<DatabaseBackupUploadOutcome> RunAsync(
        string sourcePath,
        string stagingDirectory,
        string contentRootPath,
        IBackupSnapshotUploader uploader,
        DateTimeOffset createdUtc,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentNullException.ThrowIfNull(uploader);
        ArgumentNullException.ThrowIfNull(output);

        // Named from the same UTC instant the uploaded objects are, so an interrupted run leaves a
        // file an operator can recognise - and so the snapshot runner's "destination must not
        // already exist" guard is meaningful rather than accidentally reusing one name forever.
        var stagedPath = Path.Combine(
            Path.GetFullPath(stagingDirectory),
            string.Create(
                CultureInfo.InvariantCulture,
                $"inventory-{createdUtc.ToUniversalTime():yyyyMMdd'T'HHmmss}Z.db"));

        DatabaseBackupOutcome snapshot;
        BackupUploadOutcome? upload = null;
        bool stagedCopyRemoved;

        try
        {
            snapshot = await DatabaseBackupRunner.RunAsync(
                sourcePath, stagedPath, contentRootPath, output, cancellationToken);

            if (snapshot.Succeeded)
            {
                upload = await uploader.UploadAsync(
                    new VerifiedBackupSnapshot(stagedPath, snapshot.Sha256!, createdUtc),
                    cancellationToken);
            }
        }
        finally
        {
            stagedCopyRemoved = TryRemoveStagedCopy(stagedPath);
        }

        var succeeded = snapshot.Succeeded && upload is { Succeeded: true } && stagedCopyRemoved;
        var message = Summarise(snapshot, upload, stagedCopyRemoved);

        Write(output, stagedPath, upload, stagedCopyRemoved, message);

        return new DatabaseBackupUploadOutcome(succeeded, snapshot, upload, stagedCopyRemoved, message);
    }

    private static string Summarise(
        DatabaseBackupOutcome snapshot, BackupUploadOutcome? upload, bool stagedCopyRemoved)
    {
        if (!snapshot.Succeeded)
        {
            return "No snapshot was uploaded: " + snapshot.Message;
        }

        if (upload is null || !upload.Succeeded)
        {
            return upload?.Message ?? "The upload did not run.";
        }

        return stagedCopyRemoved
            ? upload.Message
            : upload.Message + " The staged local copy could not be removed and must be deleted by hand.";
    }

    private static void Write(
        TextWriter output,
        string stagedPath,
        BackupUploadOutcome? upload,
        bool stagedCopyRemoved,
        string message)
    {
        output.WriteLine("Backup upload");
        output.WriteLine(new string('-', 78));

        if (upload is null)
        {
            output.WriteLine("Upload          : not attempted (no verified snapshot)");
        }
        else
        {
            output.WriteLine($"Daily object    : {upload.DailyObjectName ?? "(none)"}");
            output.WriteLine(
                $"Monthly object  : {upload.MonthlyObjectName ?? "(none)"}"
                    + (upload.MonthlyObjectName is null
                        ? string.Empty
                        : upload.MonthlyObjectCreated
                            ? " (created as this month's recovery point)"
                            : " (already existed; left unchanged)"));
            output.WriteLine(
                upload.Succeeded
                    ? "Upload          : verified"
                    : $"Upload          : FAILED ({upload.Failure})");
        }

        output.WriteLine(
            stagedCopyRemoved
                ? "Staged copy     : removed"
                : $"Staged copy     : NOT REMOVED - delete '{stagedPath}' by hand");

        output.WriteLine();
        output.WriteLine(message);
        output.WriteLine();
    }

    /// <summary>
    /// Removes the staged snapshot and any SQLite sidecar files, reporting whether the staging
    /// location is actually clean afterwards. Nothing else in the staging directory is touched:
    /// this run only ever created these three paths, and deleting anything it did not create would
    /// make the command destructive on a directory an operator may have chosen for other reasons.
    /// </summary>
    private static bool TryRemoveStagedCopy(string stagedPath)
    {
        var removed = true;

        foreach (var path in new[] { stagedPath, stagedPath + "-wal", stagedPath + "-shm" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                removed = false;
            }

            removed &= !File.Exists(path);
        }

        return removed;
    }
}
