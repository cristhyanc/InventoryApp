using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddNayaxSqsSaleProvenanceAndPendingRebuilds</c> (issue #1103): three nullable
/// columns on <c>NayaxSales</c> that say a sale arrived from the shared Nayax SQS queue, and the
/// tenant-owned <c>PendingInventoryCostRebuilds</c> table that records which products still have to
/// have their inventory cost replayed.
///
/// <para>Production applies migrations at startup (AGENTS.md § Database and migrations), so this one
/// runs over a database holding real sales. The claim that has to be checked rather than trusted is
/// that <strong>nothing stored changes</strong>: no status, no cost, no quantity and no instant, and
/// the three new columns are <c>NULL</c> on every sale that already existed - which is exactly what
/// keeps "did not arrive from the queue" and "arrived from the queue" apart without a fourth column
/// to say which.</para>
///
/// <para>The rows are read with SQL rather than through the entity, exactly as the other migration
/// tests here do: what is under test is the schema and the data the migration wrote, and a test that
/// read them through the model would pass on a model that had drifted from the columns the migration
/// created - and could not read the table at all at the point where the columns do not exist yet.</para>
/// </summary>
public class AddNayaxSqsSaleProvenanceAndPendingRebuildsMigrationTests
{
    private const string PreviousMigration = "20261106032310_AddBusinessNayaxSqsSenders";
    private const string TargetMigration = "20261106093540_AddNayaxSqsSaleProvenanceAndPendingRebuilds";

    private static readonly string[] NewSaleColumns =
        ["ReceivedViaSqsAtUtc", "SqsFirstMessageId", "SqsVerification"];

    /// <summary>A sale a deployment already holds, written on the previous schema.</summary>
    private const long StoredTransactionId = 7100000001;

    #region The sale columns

    [Fact]
    public async Task The_migration_adds_three_nullable_columns_and_changes_nothing_else_about_the_sales_table()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);

        var before = await MigrationSchemaProbe.ColumnNamesAsync(connection, "NayaxSales");
        var indexesBefore = await MigrationSchemaProbe.IndexNamesAsync(connection, "NayaxSales");
        Assert.DoesNotContain(NewSaleColumns[0], before);

        await MigrateToAsync(db, TargetMigration);

        var after = await MigrationSchemaProbe.ColumnNamesAsync(connection, "NayaxSales");
        Assert.Equal(
            before.Concat(NewSaleColumns).Order(StringComparer.Ordinal),
            after.Order(StringComparer.Ordinal));

        // No index is added or dropped either: the per-business transaction index that makes the
        // deduplication a database rule is exactly as it was.
        Assert.Equal(indexesBefore, await MigrationSchemaProbe.IndexNamesAsync(connection, "NayaxSales"));
        Assert.Contains("IX_NayaxSales_BusinessId_TransactionID", indexesBefore);
    }

    /// <summary>
    /// The three columns are <c>NULL</c> on a sale that already existed, and every other column of
    /// that sale is untouched. Nothing is backfilled: a sale the <c>lastSales</c> sync imported did
    /// not arrive from the queue, and saying it did - even as a default instant - would be a false
    /// statement about where a financial row came from.
    /// </summary>
    [Fact]
    public async Task An_existing_sale_keeps_every_value_and_gets_no_sqs_provenance()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);
        await InsertBusinessAsync(connection);
        await InsertExistingSaleAsync(connection);

        var before = await ReadSaleAsync(connection);

        await MigrateToAsync(db, TargetMigration);

        Assert.Equal(before, await ReadSaleAsync(connection));

        var provenance = await QueryAsync(
            connection,
            "SELECT ReceivedViaSqsAtUtc, SqsFirstMessageId, SqsVerification FROM NayaxSales;",
            reader => (
                Received: reader.IsDBNull(0) ? null : reader.GetString(0),
                MessageId: reader.IsDBNull(1) ? null : reader.GetString(1),
                Verification: reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2)));

        var row = Assert.Single(provenance);
        Assert.Null(row.Received);
        Assert.Null(row.MessageId);
        Assert.Null(row.Verification);
    }

    #endregion

    #region The pending-rebuild table

    /// <summary>
    /// The table arrives tenant-owned, with the per-business uniqueness that makes "one row per
    /// product" a database rule rather than a convention, and the oldest-first index the drain reads
    /// by. It carries a <c>BusinessId</c> because a pending replay is a fact about one business's own
    /// product - it is not a platform table (AGENTS.md § Tenant ownership and data isolation).
    /// </summary>
    [Fact]
    public async Task The_migration_creates_the_tenant_owned_pending_rebuild_table()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);

        Assert.DoesNotContain(
            "PendingInventoryCostRebuilds", await MigrationSchemaProbe.TableNamesAsync(connection));

        await MigrateToAsync(db, TargetMigration);

        Assert.Contains(
            "PendingInventoryCostRebuilds", await MigrationSchemaProbe.TableNamesAsync(connection));

        Assert.Equal(
            [
                "AttemptCount",
                "BusinessId",
                "CreatedAtUtc",
                "FromInstantUtc",
                "Id",
                "LastAttemptAtUtc",
                "LastFailureCode",
                "ProductId",
            ],
            (await MigrationSchemaProbe.ColumnNamesAsync(connection, "PendingInventoryCostRebuilds"))
                .Order(StringComparer.Ordinal));

        var indexes = await MigrationSchemaProbe.IndexNamesAsync(
            connection, "PendingInventoryCostRebuilds");
        Assert.Contains("IX_PendingInventoryCostRebuilds_BusinessId", indexes);
        Assert.Contains("IX_PendingInventoryCostRebuilds_BusinessId_ProductId", indexes);
        Assert.Contains("IX_PendingInventoryCostRebuilds_BusinessId_CreatedAtUtc", indexes);
    }

    /// <summary>
    /// The unique index is the backstop under the store's read-then-insert: two rows for one product
    /// of one business are refused by the database, so a second concurrent writer cannot turn one
    /// pending replay into a growing list.
    /// </summary>
    [Fact]
    public async Task One_product_of_one_business_can_have_only_one_pending_rebuild()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, TargetMigration);

        await ExecuteAsync(
            connection,
            "INSERT INTO PendingInventoryCostRebuilds "
                + "(BusinessId, ProductId, FromInstantUtc, CreatedAtUtc, AttemptCount) "
                + "VALUES (1, 55, '2026-11-06 00:00:00', '2026-11-06 00:00:00', 0);");

        var duplicate = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "INSERT INTO PendingInventoryCostRebuilds "
                + "(BusinessId, ProductId, FromInstantUtc, CreatedAtUtc, AttemptCount) "
                + "VALUES (1, 55, '2026-11-05 00:00:00', '2026-11-06 00:00:00', 0);"));

        Assert.Contains("UNIQUE", duplicate.Message, StringComparison.OrdinalIgnoreCase);

        // And the constraint is per business, because two businesses may legitimately be replaying
        // products that happen to share a local key.
        await ExecuteAsync(
            connection,
            "INSERT INTO PendingInventoryCostRebuilds "
                + "(BusinessId, ProductId, FromInstantUtc, CreatedAtUtc, AttemptCount) "
                + "VALUES (2, 55, '2026-11-06 00:00:00', '2026-11-06 00:00:00', 0);");

        Assert.Equal(
            2, await MigrationSchemaProbe.RowCountAsync(connection, "PendingInventoryCostRebuilds"));
    }

    /// <summary>
    /// The table deliberately carries no foreign key to <c>Products</c>. A pending replay is transient
    /// work, and a restricted key would make deleting a product depend on whether a background replay
    /// had caught up - a rule nobody asked for. The product is this business's own by the ownership
    /// key the row is read under.
    /// </summary>
    [Fact]
    public async Task The_pending_rebuild_table_has_no_foreign_key_to_products()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, TargetMigration);

        Assert.DoesNotContain(
            "FOREIGN KEY",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "PendingInventoryCostRebuilds"),
            StringComparison.Ordinal);
    }

    #endregion

    #region Reversing it

    /// <summary>
    /// <c>Down</c> drops the table and the three columns and leaves every sale exactly as it was. It
    /// is safe precisely because nothing read those columns before this release and nothing else
    /// refers to the table: what is lost is provenance and transient replay work, never a sale, a
    /// status or a cost.
    /// </summary>
    [Fact]
    public async Task Reversing_the_migration_drops_the_table_and_the_three_columns()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);
        await InsertBusinessAsync(connection);
        await InsertExistingSaleAsync(connection);

        var columnsBefore = await MigrationSchemaProbe.ColumnNamesAsync(connection, "NayaxSales");
        var indexesBefore = await MigrationSchemaProbe.IndexNamesAsync(connection, "NayaxSales");
        var saleBefore = await ReadSaleAsync(connection);

        await MigrateToAsync(db, TargetMigration);
        await MigrateToAsync(db, PreviousMigration);

        Assert.Equal(columnsBefore, await MigrationSchemaProbe.ColumnNamesAsync(connection, "NayaxSales"));
        Assert.Equal(indexesBefore, await MigrationSchemaProbe.IndexNamesAsync(connection, "NayaxSales"));
        Assert.DoesNotContain(
            "PendingInventoryCostRebuilds", await MigrationSchemaProbe.TableNamesAsync(connection));
        Assert.Equal(saleBefore, await ReadSaleAsync(connection));
    }

    /// <summary>
    /// And the upgrade applies again afterwards, which is what makes the rollback a rollback rather
    /// than a one-way door.
    /// </summary>
    [Fact]
    public async Task The_upgrade_can_be_applied_again_after_being_reversed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);
        await InsertBusinessAsync(connection);
        await InsertExistingSaleAsync(connection);

        await MigrateToAsync(db, TargetMigration);
        await MigrateToAsync(db, PreviousMigration);
        await MigrateToAsync(db, TargetMigration);

        Assert.Contains(
            "PendingInventoryCostRebuilds", await MigrationSchemaProbe.TableNamesAsync(connection));
        Assert.Contains(
            "SqsVerification", await MigrationSchemaProbe.ColumnNamesAsync(connection, "NayaxSales"));
        Assert.Equal(1, await MigrationSchemaProbe.RowCountAsync(connection, "NayaxSales"));
    }

    #endregion

    /// <summary>
    /// One sale as the columns that existed <em>before</em> this migration hold it, so the same
    /// projection can be read on either side of the upgrade and compared.
    /// </summary>
    private sealed record SaleRow(
        long TransactionId,
        int BusinessId,
        int? TransactionStatusId,
        long MachineId,
        long? ProductId,
        string SettlementValue,
        string MachineAuthorizationTime,
        string? UnitCostAtSale,
        string? CostOfGoodsSold,
        int CostingStatus,
        int CostSource);

    private static Task<List<SaleRow>> ReadSaleAsync(SqliteConnection connection) =>
        QueryAsync(
            connection,
            "SELECT TransactionID, BusinessId, TransactionStatusId, MachineID, ProductId, "
                + "CAST(SettlementValue AS TEXT), MachineAuthorizationTime, "
                + "CAST(UnitCostAtSale AS TEXT), CAST(CostOfGoodsSold AS TEXT), CostingStatus, "
                + "CostSource FROM NayaxSales ORDER BY TransactionID;",
            reader => new SaleRow(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetInt32(9),
                reader.GetInt32(10)));

    private static Task InsertBusinessAsync(SqliteConnection connection) =>
        ExecuteAsync(
            connection,
            "INSERT INTO Businesses (Id, Name, Status, CreatedAtUtc, TimeZoneId) "
                + "VALUES (1, 'Fictional Vending', 20, '2026-01-01 00:00:00', 'Australia/Sydney');");

    /// <summary>
    /// A completed sale a deployment already holds, written with SQL on the <em>previous</em> schema -
    /// which is the point: this is a database the provenance columns have not reached yet.
    /// </summary>
    private static Task InsertExistingSaleAsync(SqliteConnection connection) =>
        ExecuteAsync(
            connection,
            "INSERT INTO NayaxSales "
                + "(BusinessId, TransactionID, TransactionStatusId, MachineID, SettlementValue, "
                + " MachineAuthorizationTime, Provider, ProviderProductIdIsRaw, CostingStatus, "
                + " CostSource) "
                + $"VALUES (1, {StoredTransactionId}, 12, 4200001, 3.5, "
                + "'2026-11-05 02:42:17', 1, 1, 0, 0);");

    private static Task MigrateToAsync(AppDbContext db, string targetMigration) =>
        db.GetService<IMigrator>().MigrateAsync(targetMigration);

    private static DbContextOptions<AppDbContext> OptionsFor(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

    private static async Task<List<T>> QueryAsync<T>(
        SqliteConnection connection,
        string sql,
        Func<SqliteDataReader, T> read)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<T>();
        while (await reader.ReadAsync())
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
