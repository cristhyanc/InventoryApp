using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddNayaxSaleStatusChangeAudits</c> (issue #1107): one additive, tenant-owned,
/// append-only table recording stored sale statuses a Nayax SQS message changed.
///
/// <para>Production applies migrations at startup (AGENTS.md § Database and migrations), so this one
/// runs over a database holding real sales - including the status-<c>62</c> rows whose <em>meaning</em>
/// this release changes. The claim that has to be checked rather than trusted is that
/// <strong>no stored row changes at all</strong>: the rule change needs no schema and no backfill, a
/// refunded sale keeps every value it had, and the new table starts empty. A migration that had
/// rewritten a status, a cost or a quantity would be exactly the mass update the issue says not to
/// make - each historical row is picked up by its product's next cost replay instead.</para>
///
/// <para>The rows are read with SQL rather than through the entity, as the other migration tests here
/// do: what is under test is the schema and the data the migration wrote, and a test reading them
/// through the model would pass on a model that had drifted from the columns the migration created.</para>
/// </summary>
public class AddNayaxSaleStatusChangeAuditsMigrationTests
{
    private const string PreviousMigration = "20261107100349_AddNayaxSaleOwnerDecisions";
    private const string TargetMigration = "20261108020626_AddNayaxSaleStatusChangeAudits";

    private const string AuditTable = "NayaxSaleStatusChangeAudits";

    /// <summary>A sale Nayax had already refunded before this release, as a deployment holds it.</summary>
    private const long RefundedTransactionId = 7100000062;

    /// <summary>An ordinary completed sale beside it.</summary>
    private const long CompletedTransactionId = 7100000012;

    [Fact]
    public async Task The_migration_adds_one_table_with_its_tenant_and_per_sale_indexes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);

        Assert.Empty(await MigrationSchemaProbe.ColumnNamesAsync(connection, AuditTable));

        await MigrateToAsync(db, TargetMigration);

        Assert.Equal(
            new[]
            {
                "BusinessId",
                "Id",
                "NayaxSaleId",
                "NewTransactionStatusId",
                "OccurredAtUtc",
                "PreviousTransactionStatusId",
                "SourceMessageId",
                "TransactionId",
            },
            (await MigrationSchemaProbe.ColumnNamesAsync(connection, AuditTable))
                .Order(StringComparer.Ordinal));

        var indexes = await MigrationSchemaProbe.IndexNamesAsync(connection, AuditTable);

        // The tenant index every IBusinessOwned table gets, and the one read this table has: a sale's
        // status changes in order.
        Assert.Contains($"IX_{AuditTable}_BusinessId", indexes);
        Assert.Contains($"IX_{AuditTable}_BusinessId_NayaxSaleId_Id", indexes);

        // And it starts empty: there is no backfill, because no queue message has said anything yet.
        Assert.Equal(0, await ScalarAsync(connection, $"SELECT COUNT(*) FROM {AuditTable};"));
    }

    /// <summary>
    /// Nothing on the sales table moves - not a column, not an index, and not a value on the
    /// status-<c>62</c> row this release changes the meaning of. The row's status stays <c>62</c>, its
    /// recorded cost stays what it was, and its instant is untouched: what changes is which rule reads
    /// it, and that is code rather than data.
    /// </summary>
    [Fact]
    public async Task An_existing_refunded_sale_keeps_every_value_and_the_sales_table_is_untouched()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);

        await InsertBusinessAsync(connection);
        await InsertSaleAsync(connection, RefundedTransactionId, statusId: 62);
        await InsertSaleAsync(connection, CompletedTransactionId, statusId: 12);

        var columnsBefore = await MigrationSchemaProbe.ColumnNamesAsync(connection, "NayaxSales");
        var indexesBefore = await MigrationSchemaProbe.IndexNamesAsync(connection, "NayaxSales");
        var salesBefore = await ReadSalesAsync(connection);

        await MigrateToAsync(db, TargetMigration);

        Assert.Equal(columnsBefore, await MigrationSchemaProbe.ColumnNamesAsync(connection, "NayaxSales"));
        Assert.Equal(indexesBefore, await MigrationSchemaProbe.IndexNamesAsync(connection, "NayaxSales"));
        Assert.Equal(salesBefore, await ReadSalesAsync(connection));
        Assert.Equal(0, await ScalarAsync(connection, $"SELECT COUNT(*) FROM {AuditTable};"));
    }

    /// <summary>
    /// <c>Down</c> drops the table and leaves every sale exactly as it is. What a rollback loses is
    /// the audit trail, never a sale or a figure - a status already moved to <c>62</c> stays in its
    /// column, and an older build simply reads it as a refund that consumes no stock, which is the
    /// behaviour it had before this release.
    /// </summary>
    [Fact]
    public async Task Rolling_back_drops_only_the_audit_table()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);

        await InsertBusinessAsync(connection);
        await InsertSaleAsync(connection, RefundedTransactionId, statusId: 62);

        await MigrateToAsync(db, TargetMigration);
        var salesAfterUpgrade = await ReadSalesAsync(connection);

        await MigrateToAsync(db, PreviousMigration);

        Assert.Empty(await MigrationSchemaProbe.ColumnNamesAsync(connection, AuditTable));
        Assert.Equal(salesAfterUpgrade, await ReadSalesAsync(connection));
    }

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

    private static Task<List<SaleRow>> ReadSalesAsync(SqliteConnection connection) =>
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
    /// A sale a deployment already holds, written with SQL on the <em>previous</em> schema - including
    /// the recorded cost, because the claim is that this migration does not touch one.
    /// </summary>
    private static Task InsertSaleAsync(SqliteConnection connection, long transactionId, int statusId) =>
        ExecuteAsync(
            connection,
            "INSERT INTO NayaxSales "
                + "(BusinessId, TransactionID, TransactionStatusId, MachineID, SettlementValue, "
                + " MachineAuthorizationTime, Provider, ProviderProductIdIsRaw, UnitCostAtSale, "
                + " CostOfGoodsSold, CostingStatus, CostSource) "
                + $"VALUES (1, {transactionId}, {statusId}, 4200001, 3.5, "
                + "'2026-11-05 02:42:17', 1, 1, 2.0, 2.0, 1, 1);");

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

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
