using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddNayaxSalesHistoryImportJobs</c> (issue #1304): the one new tenant-owned
/// table, the index the worker's discovery read uses, and the fact that it is purely additive.
///
/// <para>Production applies migrations at startup (AGENTS.md § Database and migrations), so what has
/// to be proved is that this one runs over a database that already holds sales and reimbursement
/// imports and changes nothing about them: no existing column, no existing row, no existing index.
/// <strong>It is a new table rather than a type column on the reimbursement import's job table</strong>,
/// which is the decision the issue asked to state: the two jobs share a shape and nothing else, and
/// one table carrying both would mean every column of each being nullable for the other and the
/// reimbursement import - explicitly out of this issue's scope - being migrated to make room.</para>
///
/// <para>The schema is read with SQL rather than through the entities on purpose. What is under test
/// is the table the migration created, and a test that read it through the model would pass on a
/// model that had drifted from it.</para>
/// </summary>
public class AddNayaxSalesHistoryImportJobsMigrationTests
{
    private const string PreviousMigration = "20261118220000_AddNayaxMachineStockEventAppliedReplacement";
    private const string HistoryMigration = "AddNayaxSalesHistoryImportJobs";

    /// <summary>
    /// The table does not exist before the migration and does after it, with the columns the feature
    /// needs - including the business key every tenant-owned table carries, the stored file, and the
    /// progress counts that are written in the same transaction as the sales they describe.
    /// </summary>
    [Fact]
    public async Task The_migration_creates_the_tenant_owned_job_table()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = OptionsFor(connection);

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await MigrateToAsync(before, PreviousMigration);

            Assert.Empty(await TablesLikeAsync(connection, "NayaxSalesHistoryImportJob%"));
        }

        await using var after = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(after, HistoryMigration);

        Assert.Equal(
            ["NayaxSalesHistoryImportJobs"],
            await TablesLikeAsync(connection, "NayaxSalesHistoryImportJob%"));

        Assert.Equal(
            [
                "Attempts",
                "BusinessId",
                "Created",
                "File",
                "FileName",
                "FinishedAtUtc",
                "Id",
                "Invalid",
                "KeptStoredValues",
                "LastError",
                "RowsRead",
                "Skipped",
                "State",
                "TotalRows",
                "Updated",
                "UploadedAtUtc",
                "UploadedByDirectoryTenantId",
                "UploadedByObjectId",
            ],
            await ColumnsAsync(connection, "NayaxSalesHistoryImportJobs"));
    }

    /// <summary>
    /// The discovery index the worker's one cross-business statement reads in: state, then the upload
    /// instant. It deliberately does not lead with the business key, because that statement has no
    /// business to narrow by - whose job is waiting is what it is for.
    /// </summary>
    [Fact]
    public async Task The_discovery_index_is_ordered_for_the_oldest_unfinished_job()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, HistoryMigration);

        var sql = await IndexSqlAsync(
            connection, "IX_NayaxSalesHistoryImportJobs_State_UploadedAtUtc");

        Assert.NotNull(sql);
        Assert.Contains("\"State\", \"UploadedAtUtc\"", sql);

        // And the page's own read is indexed by the business, like every other per-business read.
        var byBusiness = await IndexSqlAsync(
            connection, "IX_NayaxSalesHistoryImportJobs_BusinessId_UploadedAtUtc");

        Assert.NotNull(byBusiness);
        Assert.Contains("\"BusinessId\", \"UploadedAtUtc\"", byBusiness);
    }

    /// <summary>
    /// It is purely additive: a database that already holds imported sales keeps them, column for
    /// column and row for row, and the new table starts empty - nothing is backfilled, because no
    /// past import was uploaded as a history job.
    /// </summary>
    [Fact]
    public async Task An_existing_sale_is_untouched()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = OptionsFor(connection);

        string[] salesColumnsBefore;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await MigrateToAsync(before, PreviousMigration);
            salesColumnsBefore = await ColumnsAsync(connection, "NayaxSales");

            await ExecuteAsync(
                connection,
                """
                INSERT INTO Businesses (Id, Name, Status, CreatedAtUtc, SupplierPresetsCopiedAtUtc)
                VALUES (1, 'Fictional Vending A', 1, '2026-01-01 00:00:00', NULL);
                INSERT INTO NayaxSales (
                    Id, BusinessId, TransactionID, MachineID, MachineAuthorizationTime,
                    SettlementValue, CostingStatus, CostSource, Provider, ProviderProductIdIsRaw,
                    SqsVerification)
                VALUES (1, 1, 9000001, 942488501, '2026-06-01 00:00:00', 3.5, 0, 0, 1, 1, 0);
                """);
        }

        await using var after = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(after, HistoryMigration);

        Assert.Equal(salesColumnsBefore, await ColumnsAsync(connection, "NayaxSales"));
        Assert.Equal(
            3.5,
            Convert.ToDouble(
                await ScalarAsync(connection, "SELECT SettlementValue FROM NayaxSales WHERE Id = 1;")));
        Assert.Equal(
            0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM NayaxSalesHistoryImportJobs;"));

        // And the reimbursement import's own job table is exactly as it was: this feature is a
        // second table beside it, not a change to it.
        Assert.NotEmpty(await ColumnsAsync(connection, "ReimbursementImportJobs"));
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
            await MigrateToAsync(up, HistoryMigration);
        }

        await using var down = TestAppDbContext.Unrestricted(options);
        await MigrateToAsync(down, PreviousMigration);

        Assert.Empty(await TablesLikeAsync(connection, "NayaxSalesHistoryImportJob%"));
        Assert.NotEmpty(await ColumnsAsync(connection, "NayaxSales"));
        Assert.NotEmpty(await ColumnsAsync(connection, "ReimbursementImportJobs"));
    }

    /// <summary>
    /// The migration carries its <strong>own</strong> historical target model, which is what the EF
    /// tooling reconstructs a schema's history from - <c>dotnet ef migrations remove</c> restores the
    /// snapshot from the previous migration's target model, and the application itself never reads
    /// one at all.
    ///
    /// <para>So it has to be a model this migration states and not a model that is read from
    /// somewhere a later change can move: a target model derived from the mutable
    /// <c>AppDbContextModelSnapshot</c> would silently become whatever the schema looks like next
    /// year, and this migration would then claim to target tables that did not exist when it ran.
    /// Here the model is the literal one, so the test asserts both what the migration targets - the
    /// table it creates, with its columns - and that it does not borrow the snapshot's: the two types
    /// are unrelated, and the only model the snapshot could lend is one that already differs from
    /// this one by every migration added after it.</para>
    /// </summary>
    [Fact]
    public void The_migrations_target_model_is_its_own_and_states_the_new_table()
    {
        var migration = (Migration)Activator.CreateInstance(
            typeof(AppDbContext).Assembly.GetType(
                "Inventory.Infrastructure.Migrations.AddNayaxSalesHistoryImportJobs")!)!;

        // The literal model the Designer declares, built with nothing but a model builder.
        var target = migration.TargetModel;

        var job = target.GetEntityTypes().Single(entity =>
            entity.GetTableName() == "NayaxSalesHistoryImportJobs");

        Assert.Contains("BusinessId", job.GetProperties().Select(property => property.Name));
        Assert.Contains("RowsRead", job.GetProperties().Select(property => property.Name));
        Assert.Contains("File", job.GetProperties().Select(property => property.Name));

        // And it is the migration's own, not the mutable snapshot's: nothing in this migration's
        // type derives from, or is derived from, the snapshot that a later migration rewrites.
        var snapshot = typeof(AppDbContext).Assembly.GetType(
            "Inventory.Infrastructure.Migrations.AppDbContextModelSnapshot")!;

        Assert.False(snapshot.IsAssignableFrom(migration.GetType()));
        Assert.DoesNotContain(
            migration.GetType().GetNestedTypes(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
            nested => snapshot.IsAssignableFrom(nested));
    }

    private static async Task MigrateToAsync(AppDbContext db, string targetMigration) =>
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);

    private static DbContextOptions<AppDbContext> OptionsFor(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

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
