using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace InventoryApi.Bootstrap;

/// <summary>
/// Parses the <c>backup-database</c> command line. Unlike <see cref="DatabaseMigrationArguments"/>
/// and <see cref="BusinessBootstrapArguments"/>, there is no implicit/default mode: a retained
/// manual snapshot always needs an explicit, operator-chosen destination, so <c>--output &lt;path&gt;</c>
/// is required rather than optional.
/// </summary>
public static class BackupDatabaseArguments
{
    public const string CommandName = "backup-database";
    public const string OutputFlag = "--output";

    public static bool Matches(string[] args) =>
        args.Length > 0 && string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase);

    /// <param name="args">The full argument list, including the command name at position 0.</param>
    /// <param name="outputPath">The requested snapshot destination.</param>
    /// <param name="error">Operator-facing reason the arguments were rejected, or empty.</param>
    public static bool TryParse(string[] args, out string outputPath, out string error)
    {
        outputPath = string.Empty;
        error = string.Empty;

        string? seenOutput = null;
        var remaining = args.Skip(1).ToArray();

        for (var i = 0; i < remaining.Length; i++)
        {
            if (!string.Equals(remaining[i], OutputFlag, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Unrecognised argument '{remaining[i]}'. Usage: {CommandName} {OutputFlag} <path>";
                return false;
            }

            if (i + 1 >= remaining.Length)
            {
                error = $"{OutputFlag} requires a path argument. Usage: {CommandName} {OutputFlag} <path>";
                return false;
            }

            if (seenOutput is not null)
            {
                error = $"{OutputFlag} was specified more than once.";
                return false;
            }

            seenOutput = remaining[++i];
        }

        if (string.IsNullOrWhiteSpace(seenOutput))
        {
            error = $"{OutputFlag} <path> is required. Usage: {CommandName} {OutputFlag} <path>";
            return false;
        }

        outputPath = seenOutput;
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
/// </summary>
public static class BackupDatabaseCommand
{
    public const string CommandName = BackupDatabaseArguments.CommandName;

    public static bool Matches(string[] args) => BackupDatabaseArguments.Matches(args);

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!BackupDatabaseArguments.TryParse(args, out var outputPath, out var argumentError))
        {
            Console.WriteLine();
            Console.WriteLine($"Database backup - REFUSED: {argumentError}");
            Console.WriteLine();
            return 1;
        }

        var builder = WebApplication.CreateBuilder(args);
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Data Source=inventory.db";
        var sourcePath = new SqliteConnectionStringBuilder(connectionString).DataSource;

        var outcome = await DatabaseBackupRunner.RunAsync(
            sourcePath,
            outputPath,
            builder.Environment.ContentRootPath,
            Console.Out,
            cancellationToken);

        return outcome.ExitCode;
    }
}
