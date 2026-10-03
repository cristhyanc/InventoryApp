using System.Globalization;
using Inventory.Infrastructure.Backups;

namespace InventoryApi.Bootstrap;

/// <param name="Succeeded">True only when the snapshot was verified, both uploads are accounted for, and no local copy was left behind.</param>
/// <param name="Snapshot">The snapshot step's own outcome; always present, because it always runs.</param>
/// <param name="Upload">The upload step's outcome, or <c>null</c> when the snapshot never qualified for one.</param>
/// <param name="StagedCopyRemoved">Whether this run's staged snapshot (and any SQLite sidecar files) are gone.</param>
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
/// <b>The snapshot is staged outside the served content, in a directory this run owns.</b> The
/// staging root is supplied by the caller and validated by the snapshot runner's existing
/// content-root guard, so the temporary copy can never sit under <c>wwwroot</c> or anywhere else a
/// redeploy would capture or the application would serve. Within it, each invocation stages into
/// its own freshly named subdirectory, so two runs that overlap - a scheduled one and an operator's
/// manual one, say - never contend for a path, and neither can delete the other's snapshot.
/// </description></item>
/// <item><description>
/// <b>The staged copy is removed either way, and only ever this run's.</b> Cleanup is a
/// <c>finally</c> path, so an upload that failed, or that threw because the storage account was
/// unreachable or the role assignment was refused, still does not leave a complete copy of every
/// business's data on the instance's local disk. A cleanup that could not finish is reported and
/// fails the command rather than being swallowed. It deletes only the paths this invocation could
/// have created inside its own directory, so nothing another run or an operator put in the staging
/// root is ever touched.
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

        // Each invocation stages inside its own subdirectory of the staging root. The UTC instant
        // alone is precise only to the second, so two runs that start in the same second - a
        // scheduled backup and an operator's manual one - would otherwise pick the same path: the
        // second would be refused by the snapshot runner's "destination must not already exist"
        // guard, and would then delete the first run's snapshot from under it on the way out. The
        // random suffix makes the directory this run's alone; the instant is kept in the name so an
        // interrupted run still leaves something an operator can recognise, and the file inside
        // keeps the plain timestamped name the uploaded objects use.
        var runDirectory = Path.Combine(
            Path.GetFullPath(stagingDirectory),
            string.Create(
                CultureInfo.InvariantCulture,
                $"run-{createdUtc.ToUniversalTime():yyyyMMdd'T'HHmmss}Z-{Guid.NewGuid():N}"));

        var stagedPath = Path.Combine(
            runDirectory,
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
            TryRemoveRunDirectory(runDirectory);
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

            string monthlyStatusSuffix;
            if (upload.MonthlyObjectName is null)
            {
                monthlyStatusSuffix = string.Empty;
            }
            else if (upload.MonthlyObjectCreated)
            {
                monthlyStatusSuffix = " (created as this month's recovery point)";
            }
            else
            {
                monthlyStatusSuffix = " (already existed; left unchanged)";
            }

            output.WriteLine(
                $"Monthly object  : {upload.MonthlyObjectName ?? "(none)"}" + monthlyStatusSuffix);
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
    /// Removes this run's staged snapshot and any SQLite sidecar files, reporting whether they are
    /// actually gone afterwards. Nothing else is touched, by name and by location: these are the
    /// only three paths this invocation could have created, and they are inside a directory no
    /// other run uses, so a concurrent run's snapshot and anything an operator keeps in the staging
    /// root both survive.
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

    /// <summary>
    /// Removes this run's now-empty staging subdirectory so repeated runs do not accumulate
    /// directories under the staging root. The delete is deliberately non-recursive: if anything
    /// unexpected is in there it fails rather than taking that content with it. Failure is not
    /// reported as an unremoved staged copy - an empty directory is not a copy of the database,
    /// and <see cref="TryRemoveStagedCopy"/> has already answered the question the operator needs.
    /// </summary>
    private static void TryRemoveRunDirectory(string runDirectory)
    {
        try
        {
            if (Directory.Exists(runDirectory))
            {
                Directory.Delete(runDirectory, recursive: false);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Best effort: the staged files themselves are reported separately and are what matters.
        }
    }
}
