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
