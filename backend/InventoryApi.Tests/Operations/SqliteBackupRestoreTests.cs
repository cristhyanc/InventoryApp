using InventoryApi.Bootstrap;
using Microsoft.Data.Sqlite;
using Xunit;

namespace InventoryApi.Tests.Operations;

/// <summary>
/// Issue #53. Proves the backup mechanism the documented production procedure
/// (docs/architecture.md, README.md) relies on - SQLite's Online Backup API, the same engine
/// feature the <c>sqlite3</c> CLI's <c>.backup</c> command and
/// <see cref="SqliteConnection.BackupDatabase(SqliteConnection)"/> both call - produces a usable,
/// integrity-checked copy without requiring the source connection (standing in for a running API
/// process) to close first. Every fixture here is a throwaway file under the OS temp directory;
/// no test opens or modifies a developer's or production <c>inventory.db</c>.
/// </summary>
public sealed class SqliteBackupRestoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _sourcePath;

    public SqliteBackupRestoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "InventoryApp.BackupRestoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _sourcePath = Path.Combine(_root, "source.db");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long CountRows(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM SalesSnapshot;";
        return (long)command.ExecuteScalar()!;
    }

    private static void AssertIntegrityOk(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", (string)command.ExecuteScalar()!);
    }

    [Fact]
    public void Backup_produces_a_verified_restorable_copy_of_committed_data()
    {
        using var source = new SqliteConnection($"Data Source={_sourcePath}");
        source.Open();
        Execute(source, "CREATE TABLE SalesSnapshot (Id INTEGER PRIMARY KEY, Amount REAL NOT NULL);");
        Execute(source, "INSERT INTO SalesSnapshot (Amount) VALUES (12.50), (7.25);");

        var backupPath = Path.Combine(_root, "backup.db");
        using (var destination = new SqliteConnection($"Data Source={backupPath}"))
        {
            destination.Open();

            // The source connection above stays open throughout, exactly as a running API
            // process would hold it - proving the backup does not require stopping the app.
            source.BackupDatabase(destination);
        }

        using var restored = new SqliteConnection($"Data Source={backupPath}");
        restored.Open();
        AssertIntegrityOk(restored);
        Assert.Equal(2, CountRows(restored));
    }

    [Fact]
    public void A_later_backup_reflects_writes_committed_after_the_first_backup()
    {
        using var source = new SqliteConnection($"Data Source={_sourcePath}");
        source.Open();
        Execute(source, "CREATE TABLE SalesSnapshot (Id INTEGER PRIMARY KEY, Amount REAL NOT NULL);");
        Execute(source, "INSERT INTO SalesSnapshot (Amount) VALUES (12.50);");

        var firstBackupPath = Path.Combine(_root, "backup-1.db");
        using (var destination = new SqliteConnection($"Data Source={firstBackupPath}"))
        {
            destination.Open();
            source.BackupDatabase(destination);
        }

        // The app keeps writing through the same, still-open source connection between backups -
        // nothing about taking a backup required disconnecting or restarting it.
        Execute(source, "INSERT INTO SalesSnapshot (Amount) VALUES (99.00);");

        var secondBackupPath = Path.Combine(_root, "backup-2.db");
        using (var destination = new SqliteConnection($"Data Source={secondBackupPath}"))
        {
            destination.Open();
            source.BackupDatabase(destination);
        }

        using var firstRestored = new SqliteConnection($"Data Source={firstBackupPath}");
        firstRestored.Open();
        Assert.Equal(1, CountRows(firstRestored));

        using var secondRestored = new SqliteConnection($"Data Source={secondBackupPath}");
        secondRestored.Open();
        AssertIntegrityOk(secondRestored);
        Assert.Equal(2, CountRows(secondRestored));
    }
}

/// <summary>
/// Issue #331. Builds on the mechanism <see cref="SqliteBackupRestoreTests"/> already proves
/// (SQLite's Online Backup API plus <c>PRAGMA integrity_check</c>) and exercises the
/// <c>backup-database</c> command's orchestration on top of it - path validation, verification,
/// hashing, and failure reporting - against real on-disk SQLite files rather than re-proving the
/// low-level backup/restore behaviour itself.
/// </summary>
public sealed class DatabaseBackupRunnerTests : IDisposable
{
    private readonly string _root;
    private readonly string _sourcePath;
    private readonly string _contentRootPath;

    public DatabaseBackupRunnerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "InventoryApp.DatabaseBackupRunnerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _sourcePath = Path.Combine(_root, "source.db");
        _contentRootPath = Path.Combine(_root, "published-content");
        Directory.CreateDirectory(_contentRootPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long CountRows(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM SalesSnapshot;";
        return (long)command.ExecuteScalar()!;
    }

    private static void AssertIntegrityOk(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", (string)command.ExecuteScalar()!);
    }

    private void CreateSourceDatabase()
    {
        using var source = new SqliteConnection($"Data Source={_sourcePath}");
        source.Open();
        Execute(source, "CREATE TABLE SalesSnapshot (Id INTEGER PRIMARY KEY, Amount REAL NOT NULL);");
        Execute(source, "INSERT INTO SalesSnapshot (Amount) VALUES (12.50), (7.25);");
    }

    [Fact]
    public async Task Succeeds_and_reports_a_verified_hashed_snapshot_of_an_on_disk_database()
    {
        CreateSourceDatabase();
        var outputPath = Path.Combine(_root, "backups", "inventory-snapshot.db");
        var writer = new StringWriter();

        var outcome = await DatabaseBackupRunner.RunAsync(
            _sourcePath, outputPath, _contentRootPath, writer, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(0, outcome.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Sha256));
        Assert.NotNull(outcome.Duration);
        Assert.True(File.Exists(outputPath));

        using var restored = new SqliteConnection($"Data Source={outputPath}");
        restored.Open();
        AssertIntegrityOk(restored);
        Assert.Equal(2, CountRows(restored));

        // Never prints the connection string/path twice as anything resembling a secret, and
        // never logs raw file bytes - only the measured outcome.
        Assert.DoesNotContain("Password", writer.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fails_clearly_when_the_configured_source_does_not_exist()
    {
        var missingSourcePath = Path.Combine(_root, "does-not-exist.db");
        var outputPath = Path.Combine(_root, "backups", "inventory-snapshot.db");

        var outcome = await DatabaseBackupRunner.RunAsync(
            missingSourcePath, outputPath, _contentRootPath, TextWriter.Null, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal(DatabaseBackupFailureReason.SourceMissing, outcome.Failure);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task A_relative_source_path_is_not_rejected_merely_for_being_relative()
    {
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_root);
        try
        {
            CreateSourceDatabase();
            var outputPath = Path.Combine(_root, "backups", "inventory-snapshot.db");

            var outcome = await DatabaseBackupRunner.RunAsync(
                "source.db", outputPath, _contentRootPath, TextWriter.Null, CancellationToken.None);

            Assert.True(outcome.Succeeded);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    [Fact]
    public async Task Fails_clearly_when_the_source_file_is_not_a_valid_sqlite_database()
    {
        File.WriteAllText(_sourcePath, "not a sqlite database");
        var outputPath = Path.Combine(_root, "backups", "inventory-snapshot.db");

        var outcome = await DatabaseBackupRunner.RunAsync(
            _sourcePath, outputPath, _contentRootPath, TextWriter.Null, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal(DatabaseBackupFailureReason.SourceCannotBeOpened, outcome.Failure);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task Refuses_an_output_path_that_already_exists()
    {
        CreateSourceDatabase();
        var outputPath = Path.Combine(_root, "already-there.db");
        File.WriteAllText(outputPath, "pre-existing");

        var outcome = await DatabaseBackupRunner.RunAsync(
            _sourcePath, outputPath, _contentRootPath, TextWriter.Null, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(DatabaseBackupFailureReason.OutputAlreadyExists, outcome.Failure);
        Assert.Equal("pre-existing", File.ReadAllText(outputPath));
    }

    [Fact]
    public async Task Refuses_an_output_path_equal_to_the_source()
    {
        CreateSourceDatabase();

        var outcome = await DatabaseBackupRunner.RunAsync(
            _sourcePath, _sourcePath, _contentRootPath, TextWriter.Null, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(DatabaseBackupFailureReason.OutputSameAsSource, outcome.Failure);
    }

    [Fact]
    public async Task Refuses_an_output_path_inside_the_web_root_or_published_content()
    {
        CreateSourceDatabase();
        var outputPath = Path.Combine(_contentRootPath, "inventory-snapshot.db");

        var outcome = await DatabaseBackupRunner.RunAsync(
            _sourcePath, outputPath, _contentRootPath, TextWriter.Null, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(DatabaseBackupFailureReason.OutputInsideContentRoot, outcome.Failure);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task Never_copies_the_live_database_with_a_raw_filesystem_copy()
    {
        // The source stays open throughout, exactly as a running API process would hold it -
        // proving the command uses SQLite's Online Backup API (which tolerates a concurrently
        // open writer) rather than a filesystem `cp`, which would require the writer to be idle
        // and could capture a mid-write file.
        using var source = new SqliteConnection($"Data Source={_sourcePath}");
        source.Open();
        Execute(source, "CREATE TABLE SalesSnapshot (Id INTEGER PRIMARY KEY, Amount REAL NOT NULL);");
        Execute(source, "INSERT INTO SalesSnapshot (Amount) VALUES (12.50);");

        var outputPath = Path.Combine(_root, "backups", "inventory-snapshot.db");

        var outcome = await DatabaseBackupRunner.RunAsync(
            _sourcePath, outputPath, _contentRootPath, TextWriter.Null, CancellationToken.None);

        Assert.True(outcome.Succeeded);

        Execute(source, "INSERT INTO SalesSnapshot (Amount) VALUES (99.00);");

        using var restored = new SqliteConnection($"Data Source={outputPath}");
        restored.Open();
        Assert.Equal(1, CountRows(restored));
    }
}

/// <summary>
/// Issues #331 and #332. Proves the <c>backup-database</c> argument parsing: exactly one explicit,
/// unambiguous mode is required - a retained snapshot at <c>--output &lt;path&gt;</c> or an
/// <c>--upload</c> to the configured backup container - matching the same strictness as the other
/// early CLI commands in <c>Program.cs</c>.
/// </summary>
public sealed class BackupDatabaseArgumentsTests
{
    [Fact]
    public void Matches_only_the_backup_database_command_name()
    {
        Assert.True(BackupDatabaseArguments.Matches(new[] { "backup-database", "--output", "x.db" }));
        Assert.False(BackupDatabaseArguments.Matches(new[] { "migrate-database" }));
        Assert.False(BackupDatabaseArguments.Matches(Array.Empty<string>()));
    }

    [Fact]
    public void Requires_an_explicit_mode()
    {
        var ok = BackupDatabaseArguments.TryParse(
            new[] { "backup-database" }, out _, out var error);

        Assert.False(ok);
        Assert.Contains("--output", error, StringComparison.Ordinal);
        Assert.Contains("--upload", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_output_with_no_value()
    {
        var ok = BackupDatabaseArguments.TryParse(
            new[] { "backup-database", "--output" }, out _, out var error);

        Assert.False(ok);
        Assert.Contains("--output", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_an_unrecognised_argument()
    {
        var ok = BackupDatabaseArguments.TryParse(
            new[] { "backup-database", "--output", "x.db", "--extra" }, out _, out var error);

        Assert.False(ok);
        Assert.Contains("Unrecognised", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parses_a_valid_output_path()
    {
        var ok = BackupDatabaseArguments.TryParse(
            new[] { "backup-database", "--output", "x.db" }, out var request, out var error);

        Assert.True(ok);
        Assert.Equal(BackupDatabaseMode.RetainedSnapshot, request.Mode);
        Assert.Equal("x.db", request.OutputPath);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void Parses_the_upload_mode()
    {
        var ok = BackupDatabaseArguments.TryParse(
            new[] { "backup-database", "--upload" }, out var request, out var error);

        Assert.True(ok);
        Assert.Equal(BackupDatabaseMode.Upload, request.Mode);
        Assert.Equal(string.Empty, request.OutputPath);
        Assert.Equal(string.Empty, error);
    }

    /// <summary>
    /// The two modes dispose of the snapshot in opposite ways: <c>--output</c> retains a file the
    /// operator named, while <c>--upload</c> stages its own copy and deletes it. Accepting both
    /// would mean either silently ignoring the path or deleting a file the operator asked to keep.
    /// </summary>
    [Fact]
    public void Rejects_output_combined_with_upload()
    {
        var ok = BackupDatabaseArguments.TryParse(
            new[] { "backup-database", "--output", "x.db", "--upload" }, out _, out var error);

        Assert.False(ok);
        Assert.Contains("mutually exclusive", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_a_repeated_upload_flag()
    {
        var ok = BackupDatabaseArguments.TryParse(
            new[] { "backup-database", "--upload", "--upload" }, out _, out var error);

        Assert.False(ok);
        Assert.Contains("--upload", error, StringComparison.Ordinal);
    }
}
