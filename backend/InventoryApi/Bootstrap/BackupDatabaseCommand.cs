using System.Diagnostics;
using System.Security.Cryptography;
using Inventory.Infrastructure.Backups;
using Microsoft.Data.Sqlite;

namespace InventoryApi.Bootstrap;

/// <summary>
/// What the <c>backup-database</c> command was asked to do with the snapshot it takes.
/// </summary>
public enum BackupDatabaseMode
{
    /// <summary>Write a verified snapshot to the operator-chosen path and keep it (issue #331).</summary>
    RetainedSnapshot,

    /// <summary>
    /// Stage a verified snapshot, upload it to the configured private backup container, and remove
    /// the local copy (issue #332).
    /// </summary>
    Upload,
}

/// <param name="Mode">Which of the two mutually exclusive modes was requested.</param>
/// <param name="OutputPath">The retained snapshot's destination; empty in upload mode.</param>
public sealed record BackupDatabaseRequest(BackupDatabaseMode Mode, string OutputPath);

/// <summary>
/// Parses the <c>backup-database</c> command line. Unlike <see cref="DatabaseMigrationArguments"/>
/// and <see cref="BusinessBootstrapArguments"/>, there is no implicit/default mode: exactly one of
/// <c>--output &lt;path&gt;</c> (a retained snapshot at an operator-chosen destination) or
/// <c>--upload</c> (a staged snapshot uploaded to the configured backup container) is required.
///
/// They are mutually exclusive because they dispose of the snapshot in opposite ways: one keeps a
/// file the operator named, the other stages its own copy and deletes it afterwards. Accepting
/// both would mean either silently ignoring the path or deleting a file the operator asked to
/// keep, and neither is something to guess at for a backup.
/// </summary>
public static class BackupDatabaseArguments
{
    public const string CommandName = "backup-database";
    public const string OutputFlag = "--output";
    public const string UploadFlag = "--upload";

    private const string Usage = $"Usage: {CommandName} ({OutputFlag} <path> | {UploadFlag})";

    public static bool Matches(string[] args) =>
        args.Length > 0 && string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase);

    /// <param name="args">The full argument list, including the command name at position 0.</param>
    /// <param name="request">The parsed mode and, for a retained snapshot, its destination.</param>
    /// <param name="error">Operator-facing reason the arguments were rejected, or empty.</param>
    public static bool TryParse(string[] args, out BackupDatabaseRequest request, out string error)
    {
        request = new BackupDatabaseRequest(BackupDatabaseMode.RetainedSnapshot, string.Empty);
        error = string.Empty;

        var remaining = args.Skip(1).ToArray();

        if (!TryParseFlags(remaining, out var seenOutput, out var sawUpload, out error))
        {
            return false;
        }

        if (sawUpload && seenOutput is not null)
        {
            error = $"{OutputFlag} and {UploadFlag} are mutually exclusive. {UploadFlag} stages its "
                + "own snapshot and removes the local copy after uploading it, so there is no "
                + $"retained destination to name; use {OutputFlag} <path> when a local snapshot is "
                + "what you want. Pass exactly one.";
            return false;
        }

        if (sawUpload)
        {
            request = new BackupDatabaseRequest(BackupDatabaseMode.Upload, string.Empty);
            return true;
        }

        if (string.IsNullOrWhiteSpace(seenOutput))
        {
            error = $"Pass exactly one of {OutputFlag} <path> or {UploadFlag}. {Usage}";
            return false;
        }

        request = new BackupDatabaseRequest(BackupDatabaseMode.RetainedSnapshot, seenOutput);
        return true;
    }

    /// <summary>
    /// Walks the arguments after the command name, recognising <see cref="UploadFlag"/> and
    /// <see cref="OutputFlag"/> and rejecting anything else. Split out of <see cref="TryParse"/>
    /// purely to keep the per-flag validation (each flag used at most once, <c>--output</c> needs a
    /// value) readable as its own unit; the mutual-exclusion/required-flag rules that depend on the
    /// combined result stay in <see cref="TryParse"/>.
    /// </summary>
    private static bool TryParseFlags(
        string[] remaining, out string? seenOutput, out bool sawUpload, out string error)
    {
        seenOutput = null;
        sawUpload = false;
        error = string.Empty;

        for (var i = 0; i < remaining.Length; i++)
        {
            if (string.Equals(remaining[i], UploadFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (sawUpload)
                {
                    error = $"{UploadFlag} was specified more than once.";
                    return false;
                }

                sawUpload = true;
                continue;
            }

            if (!string.Equals(remaining[i], OutputFlag, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Unrecognised argument '{remaining[i]}'. {Usage}";
                return false;
            }

            if (i + 1 >= remaining.Length)
            {
                error = $"{OutputFlag} requires a path argument. {Usage}";
                return false;
            }

            if (seenOutput is not null)
            {
                error = $"{OutputFlag} was specified more than once.";
                return false;
            }

            seenOutput = remaining[++i];
        }

        return true;
    }
}

/// <summary>
/// Why a <see cref="DatabaseBackupOutcome"/> did not succeed. <see cref="None"/> only appears
/// alongside a successful outcome.
/// </summary>
public enum DatabaseBackupFailureReason
{
    None,
    SourceMissing,
    SourceCannotBeOpened,
    OutputAlreadyExists,
    OutputSameAsSource,
    OutputInsideContentRoot,
    IntegrityCheckFailed,
}

/// <param name="Succeeded">True only when the snapshot was taken and passed integrity_check.</param>
/// <param name="Failure">Why it failed; <see cref="DatabaseBackupFailureReason.None"/> on success.</param>
/// <param name="Message">Operator-facing summary, safe to print (never a connection string).</param>
/// <param name="Sha256">The snapshot's SHA-256, lower-case hex, only when it succeeded.</param>
/// <param name="Duration">Wall-clock time for the backup plus verification, only when it succeeded.</param>
public sealed record DatabaseBackupOutcome(
    bool Succeeded,
    DatabaseBackupFailureReason Failure,
    string Message,
    string? Sha256,
    TimeSpan? Duration)
{
    /// <summary>Non-zero only on failure, so the process exit code reflects the result directly.</summary>
    public int ExitCode => Succeeded ? 0 : 1;
}

/// <summary>
/// The validation/backup/verification behaviour behind the <c>backup-database</c> command, against
/// plain file paths rather than a parsed connection string or a built <c>WebApplication</c>, so
/// tests can drive it directly against real on-disk SQLite files (issue #331).
///
/// Builds on the Online Backup API mechanism <c>SqliteBackupRestoreTests</c> already proves: this
/// type adds the operator-facing guardrails - the configured source must exist, the destination
/// must be new, must differ from the source, and must sit outside the API's web root/published
/// content - plus the post-backup <c>PRAGMA integrity_check</c>, SHA-256, and duration reporting
/// that make the resulting file a trustworthy retained snapshot.
/// </summary>
public static class DatabaseBackupRunner
{
    public static async Task<DatabaseBackupOutcome> RunAsync(
        string sourcePath,
        string outputPath,
        string contentRootPath,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        output.WriteLine();
        output.WriteLine("Database backup");
        output.WriteLine(new string('-', 78));

        // Resolved against the current directory, exactly how Microsoft.Data.Sqlite/EF Core
        // resolve a relative "Data Source=..." value - the relative default must behave the same
        // here as it does for the running API, not be rejected merely for being relative.
        var resolvedSource = Path.GetFullPath(sourcePath);
        var resolvedOutput = Path.GetFullPath(outputPath);
        var resolvedContentRoot = Path.GetFullPath(contentRootPath);

        if (!File.Exists(resolvedSource))
        {
            return Fail(
                output,
                DatabaseBackupFailureReason.SourceMissing,
                $"The configured database was not found at '{resolvedSource}'.");
        }

        if (string.Equals(resolvedOutput, resolvedSource, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                output,
                DatabaseBackupFailureReason.OutputSameAsSource,
                $"{BackupDatabaseArguments.OutputFlag} must name a path different from the source database.");
        }

        if (IsWithin(resolvedContentRoot, resolvedOutput))
        {
            return Fail(
                output,
                DatabaseBackupFailureReason.OutputInsideContentRoot,
                $"{BackupDatabaseArguments.OutputFlag} must be outside the API's web root/published "
                    + $"content ('{resolvedContentRoot}'); it never deploys with the application.");
        }

        if (File.Exists(resolvedOutput))
        {
            return Fail(
                output,
                DatabaseBackupFailureReason.OutputAlreadyExists,
                $"'{resolvedOutput}' already exists; choose a new, unused destination so an earlier "
                    + "snapshot is never overwritten.");
        }

        var outputDirectory = Path.GetDirectoryName(resolvedOutput);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var source = new SqliteConnection($"Data Source={resolvedSource}");
            await source.OpenAsync(cancellationToken);

            // Forces SQLite to actually read the database header/schema now, rather than lazily
            // on first real use, so a file that exists but is not a valid SQLite database (or is
            // unreadable) is reported clearly here instead of surfacing as a confusing failure
            // from the backup call below.
            using (var probe = source.CreateCommand())
            {
                probe.CommandText = "PRAGMA quick_check;";
                await probe.ExecuteScalarAsync(cancellationToken);
            }

            using var destination = new SqliteConnection($"Data Source={resolvedOutput}");
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination);
        }
        catch (SqliteException ex)
        {
            TryDeletePartialOutput(resolvedOutput);
            return Fail(
                output,
                DatabaseBackupFailureReason.SourceCannotBeOpened,
                $"The configured database at '{resolvedSource}' could not be opened or backed up: {ex.Message}");
        }

        string integrityResult;
        using (var verify = new SqliteConnection($"Data Source={resolvedOutput}"))
        {
            await verify.OpenAsync(cancellationToken);
            using var command = verify.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            integrityResult = (string)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        if (!string.Equals(integrityResult, "ok", StringComparison.Ordinal))
        {
            return Fail(
                output,
                DatabaseBackupFailureReason.IntegrityCheckFailed,
                $"PRAGMA integrity_check reported '{integrityResult}', not 'ok'; the snapshot at "
                    + $"'{resolvedOutput}' is not trustworthy and was left in place for inspection.");
        }

        stopwatch.Stop();
        var sha256 = await ComputeSha256Async(resolvedOutput, cancellationToken);

        output.WriteLine($"Snapshot        : {resolvedOutput}");
        output.WriteLine("Integrity check : ok");
        output.WriteLine($"SHA-256         : {sha256}");
        output.WriteLine($"Duration        : {stopwatch.Elapsed.TotalSeconds:0.###}s");
        output.WriteLine();
        output.WriteLine("Backup verified successfully.");
        output.WriteLine();

        return new DatabaseBackupOutcome(
            Succeeded: true,
            DatabaseBackupFailureReason.None,
            "Backup verified successfully.",
            sha256,
            stopwatch.Elapsed);
    }

    private static DatabaseBackupOutcome Fail(TextWriter output, DatabaseBackupFailureReason reason, string message)
    {
        output.WriteLine($"FAILED ({reason}): {message}");
        output.WriteLine();
        return new DatabaseBackupOutcome(Succeeded: false, reason, message, Sha256: null, Duration: null);
    }

    private static void TryDeletePartialOutput(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only; the failure above is already reported.
        }
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="root"/> itself or nested under it.
    /// Both paths are already fully resolved (<see cref="Path.GetFullPath(string)"/>) by the caller.
    /// </summary>
    private static bool IsWithin(string root, string candidate)
    {
        if (string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }
}

/// <summary>
/// Creates an operator-invokable, verified SQLite snapshot (issue #331).
///
/// Like <see cref="BusinessBootstrapCommand"/> and <see cref="DatabaseMigrationCommand"/>, this is
/// an early, human-invoked CLI mode that the API host never reaches on its own: starting the web
/// host and taking a backup are mutually exclusive paths through <c>Program.cs</c>.
///
/// It is callable manually for one-off verification, and by the scheduled backup job (issue #333)
/// using the same supported command - this is intentionally the only way either caller takes a
/// retained snapshot, so there is exactly one place that decides where backups may and may not go.
///
/// From a source tree, with the SDK installed:
/// <code>
///   dotnet run --project backend/InventoryApi -- backup-database --output /path/to/backups/inventory-20260101T000000.db
/// </code>
///
/// From the deployed application, which is `dotnet publish` output and has no SDK or sources:
/// <code>
///   dotnet InventoryApi.dll backup-database --output /path/to/backups/inventory-20260101T000000.db
/// </code>
///
/// It opens the same configured database the API would (<c>ConnectionStrings:DefaultConnection</c>,
/// falling back to the relative <c>Data Source=inventory.db</c> default), uses SQLite's Online
/// Backup API rather than a filesystem copy, and refuses a destination that already exists,
/// matches the source, or sits inside the API's content root/web root - so a snapshot never lands
/// somewhere a redeploy or publish step would silently overwrite or discard it.
///
/// <c>--upload</c> is the second mode (issue #332): it creates and verifies a snapshot exactly as
/// above, into a staging directory under the OS temporary path rather than a retained destination,
/// uploads it to the configured private backup container, and removes the local copy. It is one
/// workflow on purpose - there is no way to upload a file this command did not just take and
/// verify, and no way to leave a verified snapshot sitting on the instance the database is on.
/// Authentication is the App Service's managed identity through <c>DefaultAzureCredential</c>;
/// <c>BackupStorage:BlobServiceUri</c> and <c>BackupStorage:ContainerName</c> are the only settings
/// it reads, and neither is or may become a secret. An unconfigured or unusable destination is
/// refused before any snapshot is taken.
/// </summary>
public static class BackupDatabaseCommand
{
    public const string CommandName = BackupDatabaseArguments.CommandName;

    /// <summary>
    /// Where <c>--upload</c> stages the snapshot it is about to upload: the OS temporary path,
    /// which is outside the API's content root and web root on every host the application runs on,
    /// so the staged copy is never served, published or captured by a redeploy. The snapshot
    /// runner's own content-root guard still checks this rather than trusting it.
    /// </summary>
    private static string StagingDirectory => Path.Combine(Path.GetTempPath(), "inventoryapp-backup-staging");

    public static bool Matches(string[] args) => BackupDatabaseArguments.Matches(args);

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!BackupDatabaseArguments.TryParse(args, out var request, out var argumentError))
        {
            return Refuse(argumentError);
        }

        var builder = WebApplication.CreateBuilder(args);
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Data Source=inventory.db";
        var sourcePath = new SqliteConnectionStringBuilder(connectionString).DataSource;

        if (request.Mode == BackupDatabaseMode.RetainedSnapshot)
        {
            var outcome = await DatabaseBackupRunner.RunAsync(
                sourcePath,
                request.OutputPath,
                builder.Environment.ContentRootPath,
                Console.Out,
                cancellationToken);

            return outcome.ExitCode;
        }

        var storageOptions = builder.Configuration
            .GetSection(BackupStorageOptions.SectionName)
            .Get<BackupStorageOptions>() ?? new BackupStorageOptions();

        IBackupSnapshotUploader uploader;
        try
        {
            // Resolved before the snapshot is taken: an upload that cannot reach its destination
            // must not first spend time copying the database and then report that the verified
            // snapshot it just deleted had nowhere to go.
            uploader = BackupSnapshotUploaderFactory.CreateAzureBlob(
                BackupStorageConfiguration.Resolve(storageOptions));
        }
        catch (InvalidOperationException configurationError)
        {
            return Refuse(configurationError.Message);
        }

        var uploadOutcome = await DatabaseBackupUploadRunner.RunAsync(
            sourcePath,
            StagingDirectory,
            builder.Environment.ContentRootPath,
            uploader,
            DateTimeOffset.UtcNow,
            Console.Out,
            cancellationToken);

        return uploadOutcome.ExitCode;
    }

    private static int Refuse(string reason)
    {
        Console.WriteLine();
        Console.WriteLine($"Database backup - REFUSED: {reason}");
        Console.WriteLine();
        return 1;
    }
}
