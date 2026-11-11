using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddReimbursementImportJobs</c> (issue #1149): the two new tenant-owned tables,
/// the uniqueness rule that makes a job's counts right, and the fact that it is purely additive.
///
/// <para>Production applies migrations at startup (AGENTS.md § Database and migrations), so what has
/// to be proved is that this one runs over a database that already holds reimbursement imports and
/// changes nothing about them: no existing column, no existing row, no existing index.</para>
///
/// <para>The schema is read with SQL rather than through the entities on purpose. What is under test
/// is the tables the migration created, and a test that read them through the model would pass on a
/// model that had drifted from them.</para>
/// </summary>
public class AddReimbursementImportJobsMigrationTests
{
    private const string PreviousMigration = "20261108070703_AddMasterProductSupplierSearchTerms";
    private const string JobsMigration = "AddReimbursementImportJobs";

    private static async Task MigrateToAsync(AppDbContext db, string targetMigration) =>
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);

    private static DbContextOptions<AppDbContext> OptionsFor(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

    /// <summary>
    /// The two tables do not exist before the migration and do after it, with the columns the
    /// feature needs - including the business key every tenant-owned table carries.
    /// </summary>
    [Fact]
    public async Task The_migration_creates_the_two_tenant_owned_tables()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = OptionsFor(connection);

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await MigrateToAsync(before, PreviousMigration);

            Assert.Empty(await TablesLikeAsync(connection, "ReimbursementImportJob%"));
        }

        await using var after = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(after, JobsMigration);

        Assert.Equal(
            ["ReimbursementImportJobFiles", "ReimbursementImportJobs"],
            await TablesLikeAsync(connection, "ReimbursementImportJob%"));

        var jobColumns = await ColumnsAsync(connection, "ReimbursementImportJobs");
        Assert.Equal(
            [
                "Archive",
                "Attempts",
                "BusinessId",
                "FileName",
                "FinishedAtUtc",
                "Id",
                "LastError",
                "NextAttemptAtUtc",
                "State",
                "UploadedAtUtc",
                "UploadedByDirectoryTenantId",
                "UploadedByObjectId",
            ],
            jobColumns);

        var fileColumns = await ColumnsAsync(connection, "ReimbursementImportJobFiles");
        Assert.Equal(
            [
                "BusinessId",
                "EntryIndex",
                "EntryPath",
                "Id",
                "Outcome",
                "Reason",
                "RecordedAtUtc",
                "ReimbursementImportJobId",
            ],
            fileColumns);
    }

    /// <summary>
    /// A job's counts are computed from its file outcomes, so one outcome per file per job is a
    /// database rule rather than a hope - and it is led by the business key, like every other
    /// per-business uniqueness rule in this schema.
    /// </summary>
    [Fact]
    public async Task One_outcome_per_file_per_job_is_a_unique_index_led_by_the_business()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, JobsMigration);

        var sql = await IndexSqlAsync(
            connection, "IX_ReimbursementImportJobFiles_BusinessId_ReimbursementImportJobId_EntryIndex");

        Assert.NotNull(sql);
        Assert.Contains("UNIQUE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"BusinessId\", \"ReimbursementImportJobId\", \"EntryIndex\"", sql);
    }

    /// <summary>
    /// The discovery index the worker's one cross-business statement reads in: state, then the
    /// not-before instant, then the upload instant. It deliberately does not lead with the business
    /// key, because that statement has no business to narrow by.
    /// </summary>
    [Fact]
    public async Task The_discovery_index_is_ordered_for_the_oldest_due_job()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, JobsMigration);

        var sql = await IndexSqlAsync(
            connection, "IX_ReimbursementImportJobs_State_NextAttemptAtUtc_UploadedAtUtc");

        Assert.NotNull(sql);
        Assert.Contains("\"State\", \"NextAttemptAtUtc\", \"UploadedAtUtc\"", sql);
    }

    /// <summary>
    /// It is purely additive: a database that already holds a reimbursement import keeps it, column
    /// for column and row for row.
    /// </summary>
    [Fact]
    public async Task An_existing_reimbursement_import_is_untouched()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = OptionsFor(connection);

        string[] importedFileColumnsBefore;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await MigrateToAsync(before, PreviousMigration);
            importedFileColumnsBefore = await ColumnsAsync(connection, "ImportedFiles");

            await ExecuteAsync(
                connection,
                """
                INSERT INTO Businesses (Id, Name, Status, CreatedAtUtc, SupplierPresetsCopiedAtUtc)
                VALUES (1, 'Fictional Vending A', 1, '2026-01-01 00:00:00', NULL);
                INSERT INTO ImportedFiles (Id, BusinessId, FileName, FileHash, ImportedAt)
                VALUES (1, 1, 'report_9900001_20261005.xml', 'HASH-A', '2026-10-06 00:00:00');
                """);
        }

        await using var after = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(after, JobsMigration);

        Assert.Equal(importedFileColumnsBefore, await ColumnsAsync(connection, "ImportedFiles"));
        Assert.Equal(
            "HASH-A",
            await ScalarAsync(connection, "SELECT FileHash FROM ImportedFiles WHERE Id = 1;"));

        // And the new tables start empty: nothing is backfilled, because there is nothing a past
        // import could be said to have been uploaded as.
        Assert.Equal(
            0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM ReimbursementImportJobs;"));
    }

    /// <summary>The migration reverses cleanly, dropping only what it created.</summary>
    [Fact]
    public async Task The_migration_can_be_rolled_back()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = OptionsFor(connection);

        await using (var up = TestAppDbContext.Unrestricted(options))
        {
            await MigrateToAsync(up, JobsMigration);
        }

        await using var down = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(down, PreviousMigration);

        Assert.Empty(await TablesLikeAsync(connection, "ReimbursementImportJob%"));
        Assert.NotEmpty(await ColumnsAsync(connection, "ImportedFiles"));
    }

    private static async Task<string[]> TablesLikeAsync(SqliteConnection connection, string pattern)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE $pattern ORDER BY name;";
        command.Parameters.AddWithValue("$pattern", pattern);

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));

        return [.. names];
    }

    private static async Task<string[]> ColumnsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}') ORDER BY name;";

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));

        return [.. names];
    }

    private static async Task<string?> IndexSqlAsync(SqliteConnection connection, string indexName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name;";
        command.Parameters.AddWithValue("$name", indexName);

        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }
}
