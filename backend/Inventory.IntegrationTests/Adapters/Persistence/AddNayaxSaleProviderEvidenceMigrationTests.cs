using Inventory.Domain.NayaxSqs;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddNayaxSaleProviderEvidence</c> (issue #1104): the tenant-owned, append-only
/// <c>NayaxSaleProviderEvidences</c> table, and the nullable
/// <c>BusinessNayaxSqsSenders.LastReconciliationSucceededAtUtc</c> column.
///
/// <para>Production applies migrations at startup (AGENTS.md § Database and migrations), so this one
/// runs over a database holding real sales and a real sender mapping. The claims that have to be
/// checked rather than trusted are that <strong>nothing stored changes</strong> - no sale, status,
/// cost, quantity or instant - and that the <c>SqsVerification</c> values this issue adds need
/// <em>no</em> migration at all: the column is a nullable integer with no check constraint, so a sale
/// keeps exactly the value it has and the new states are simply values nothing has written yet.</para>
///
/// <para>The rows are read with SQL rather than through the entity, exactly as the other migration
/// tests here do: what is under test is the schema and the data the migration wrote, and a test that
/// read them through the model would pass on a model that had drifted from the columns the migration
/// created.</para>
/// </summary>
public class AddNayaxSaleProviderEvidenceMigrationTests
{
    private const string PreviousMigration = "20261106093540_AddNayaxSqsSaleProvenanceAndPendingRebuilds";
    private const string TargetMigration = "20261107004752_AddNayaxSaleProviderEvidence";

    private const long StoredTransactionId = 7100000001;

    #region The evidence table

    /// <summary>
    /// The table arrives tenant-owned, with the one index every read of it uses and no uniqueness at
    /// all - several rows about one sale is the normal state, which is what "append-only" means here.
    /// It carries a <c>BusinessId</c> because what a business's own <c>lastSales</c> said about its own
    /// sale is a fact about that one business; it is not a platform table (AGENTS.md § Tenant
    /// ownership and data isolation).
    /// </summary>
    [Fact]
    public async Task The_migration_creates_the_tenant_owned_evidence_table()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);

        Assert.DoesNotContain(
            "NayaxSaleProviderEvidences", await MigrationSchemaProbe.TableNamesAsync(connection));

        await MigrateToAsync(db, TargetMigration);

        Assert.Contains(
            "NayaxSaleProviderEvidences", await MigrationSchemaProbe.TableNamesAsync(connection));

        Assert.Equal(
            [
                "AmountComparison",
                "Bracketed",
                "BusinessId",
                "CurrencyComparison",
                "Id",
                "InstantComparison",
                "Kind",
                "MachineComparison",
                "NayaxSaleId",
                "ObservedAtUtc",
                "OutsideReceiptGrace",
                "ProductComparison",
                "ProviderAuthorizationInstantUtc",
                "ProviderCurrencyCode",
                "ProviderMachineId",
                "ProviderNayaxProductId",
                "ProviderProductName",
                "ProviderSettlementValue",
                "ReadMachineId",
                "ResultingState",
                "ReturnedEarliestInstantUtc",
                "ReturnedItemCount",
                "ReturnedLatestInstantUtc",
                "RunId",
                "SaleAuthorizationInstantUtc",
                "SaleReceivedViaSqsAtUtc",
            ],
            (await MigrationSchemaProbe.ColumnNamesAsync(connection, "NayaxSaleProviderEvidences"))
                .Order(StringComparer.Ordinal));

        var indexes = await MigrationSchemaProbe.IndexNamesAsync(
            connection, "NayaxSaleProviderEvidences");
        Assert.Contains("IX_NayaxSaleProviderEvidences_BusinessId", indexes);
        Assert.Contains("IX_NayaxSaleProviderEvidences_BusinessId_NayaxSaleId_Kind_Id", indexes);
    }

    /// <summary>
    /// Nothing about the table is unique: several pieces of evidence about one sale is exactly what it
    /// is for. A unique index here would have refused a genuinely new observation - the second run
    /// that found a contradiction - and the deduplication is the write-locked comparison against the
    /// latest row instead.
    /// </summary>
    [Fact]
    public async Task Several_pieces_of_evidence_about_one_sale_are_allowed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, TargetMigration);

        await InsertEvidenceAsync(connection, businessId: 1, saleId: 55, resultingState: 20);
        await InsertEvidenceAsync(connection, businessId: 1, saleId: 55, resultingState: 30);

        Assert.Equal(
            2, await MigrationSchemaProbe.RowCountAsync(connection, "NayaxSaleProviderEvidences"));

        Assert.DoesNotContain(
            "UNIQUE",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "NayaxSaleProviderEvidences"),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The table deliberately carries no foreign key to <c>NayaxSales</c>: a restricted key would make
    /// deleting a sale depend on its evidence, and a cascading one would delete the evidence a
    /// financial decision was recorded against. The sale is this business's own by the ownership key
    /// the row is read under.
    /// </summary>
    [Fact]
    public async Task The_evidence_table_has_no_foreign_key_to_the_sales()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, TargetMigration);

        Assert.DoesNotContain(
            "FOREIGN KEY",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "NayaxSaleProviderEvidences"),
            StringComparison.Ordinal);
    }

    #endregion

    #region The sender column

    /// <summary>
    /// One nullable column, and nothing else about the sender table changes: not the sender id, not
    /// the enabled flag, not the revision, not either index and not the check constraint that stops
    /// an enabled row having nothing to match on.
    /// </summary>
    [Fact]
    public async Task The_migration_adds_one_nullable_column_to_the_sender_table()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);

        var before = await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessNayaxSqsSenders");
        var indexesBefore = await MigrationSchemaProbe.IndexNamesAsync(
            connection, "BusinessNayaxSqsSenders");

        await MigrateToAsync(db, TargetMigration);

        Assert.Equal(
            before.Append("LastReconciliationSucceededAtUtc").Order(StringComparer.Ordinal),
            (await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessNayaxSqsSenders"))
                .Order(StringComparer.Ordinal));

        Assert.Equal(
            indexesBefore,
            await MigrationSchemaProbe.IndexNamesAsync(connection, "BusinessNayaxSqsSenders"));

        Assert.Contains(
            "CK_BusinessNayaxSqsSenders_EnabledNeedsSenderId",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "BusinessNayaxSqsSenders"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A sender mapping a deployment already holds keeps every value, and the new column is
    /// <c>NULL</c>. Nothing is backfilled: a business whose reconciliation has never run has not
    /// reconciled, and an instant invented here would be read by issue #1106 as evidence that it had.
    /// </summary>
    [Fact]
    public async Task An_existing_sender_keeps_every_value_and_gets_no_reconciliation_instant()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);
        await InsertBusinessAsync(connection);
        await InsertSenderAsync(connection);

        var before = await ReadSenderAsync(connection);

        await MigrateToAsync(db, TargetMigration);

        Assert.Equal(before, await ReadSenderAsync(connection));

        var reconciled = await QueryAsync(
            connection,
            "SELECT LastReconciliationSucceededAtUtc FROM BusinessNayaxSqsSenders;",
            reader => reader.IsDBNull(0) ? null : reader.GetString(0));

        Assert.Null(Assert.Single(reconciled));
    }

    #endregion

    #region The verification states

    /// <summary>
    /// <strong>The new <c>SqsVerification</c> values need no migration, and this is what says so.</strong>
    /// The column is a nullable integer with no check constraint and no lookup table, so an existing
    /// sale keeps exactly the value it has - <c>NULL</c> for one the queue never delivered and
    /// <c>Unverified</c> for one it did - and the three new states are values nothing has written yet.
    /// </summary>
    [Fact]
    public async Task An_existing_sales_verification_value_is_untouched_and_the_new_states_need_no_schema()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);
        await InsertBusinessAsync(connection);

        // Two sales as a deployment already holds them: one the lastSales sync imported, carrying no
        // verification value at all, and one the queue delivered, carrying Unverified.
        await InsertSaleAsync(connection, StoredTransactionId, verification: null);
        await InsertSaleAsync(
            connection, StoredTransactionId + 1, verification: (int)NayaxSqsVerification.Unverified);

        var before = await ReadVerificationsAsync(connection);

        await MigrateToAsync(db, TargetMigration);

        Assert.Equal(before, await ReadVerificationsAsync(connection));
        Assert.Equal([null, (int)NayaxSqsVerification.Unverified], await ReadVerificationsAsync(connection));

        // No constraint was added that the new values would have to be listed in, and none that would
        // have refused them either.
        var sql = await MigrationSchemaProbe.CreateTableSqlAsync(connection, "NayaxSales");
        Assert.DoesNotContain("CHECK", sql, StringComparison.OrdinalIgnoreCase);

        // And each new value stores and reads back as itself.
        foreach (var state in new[]
                 {
                     NayaxSqsVerification.Verified,
                     NayaxSqsVerification.Conflict,
                     NayaxSqsVerification.NeedsInvestigation,
                 })
        {
            await ExecuteAsync(
                connection,
                $"UPDATE NayaxSales SET SqsVerification = {(int)state} "
                    + $"WHERE TransactionID = {StoredTransactionId + 1};");

            Assert.Equal(
                [null, (int)state],
                await ReadVerificationsAsync(connection));
        }
    }

    #endregion

    #region Reversing it

    /// <summary>
    /// <c>Down</c> drops the table and the column and leaves every sale and every sender mapping
    /// exactly as it was. What is lost is the <em>evidence</em> and the reconciliation's own success
    /// instant - never a sale, a cost or a figure - and the verification states already written stay
    /// as they are, because they are values in a column that predates this migration.
    /// </summary>
    [Fact]
    public async Task Reversing_the_migration_drops_the_table_and_the_column()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);
        await InsertBusinessAsync(connection);
        await InsertSenderAsync(connection);
        await InsertSaleAsync(
            connection, StoredTransactionId, verification: (int)NayaxSqsVerification.Unverified);

        var senderColumnsBefore = await MigrationSchemaProbe.ColumnNamesAsync(
            connection, "BusinessNayaxSqsSenders");
        var senderIndexesBefore = await MigrationSchemaProbe.IndexNamesAsync(
            connection, "BusinessNayaxSqsSenders");
        var senderBefore = await ReadSenderAsync(connection);
        var verificationsBefore = await ReadVerificationsAsync(connection);

        await MigrateToAsync(db, TargetMigration);

        // A run has happened and recorded evidence and its success, which is the state a rollback
        // really has to cope with.
        await InsertEvidenceAsync(connection, businessId: 1, saleId: 1, resultingState: 20);
        await ExecuteAsync(
            connection,
            "UPDATE BusinessNayaxSqsSenders "
                + "SET LastReconciliationSucceededAtUtc = '2026-11-07 03:00:00';");

        await MigrateToAsync(db, PreviousMigration);

        Assert.DoesNotContain(
            "NayaxSaleProviderEvidences", await MigrationSchemaProbe.TableNamesAsync(connection));
        // Ordered, because reversing an added column rebuilds the table in SQLite and the physical
        // column order afterwards is EF's rather than the original one. What matters is the set.
        Assert.Equal(
            senderColumnsBefore.Order(StringComparer.Ordinal),
            (await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessNayaxSqsSenders"))
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            senderIndexesBefore,
            await MigrationSchemaProbe.IndexNamesAsync(connection, "BusinessNayaxSqsSenders"));
        Assert.Equal(senderBefore, await ReadSenderAsync(connection));
        Assert.Equal(verificationsBefore, await ReadVerificationsAsync(connection));
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
        await InsertSenderAsync(connection);
        await InsertSaleAsync(
            connection, StoredTransactionId, verification: (int)NayaxSqsVerification.Unverified);

        await MigrateToAsync(db, TargetMigration);
        await MigrateToAsync(db, PreviousMigration);
        await MigrateToAsync(db, TargetMigration);

        Assert.Contains(
            "NayaxSaleProviderEvidences", await MigrationSchemaProbe.TableNamesAsync(connection));
        Assert.Contains(
            "LastReconciliationSucceededAtUtc",
            await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessNayaxSqsSenders"));
        Assert.Equal(1, await MigrationSchemaProbe.RowCountAsync(connection, "NayaxSales"));
        Assert.Equal(1, await MigrationSchemaProbe.RowCountAsync(connection, "BusinessNayaxSqsSenders"));
    }

    #endregion

    /// <summary>
    /// One sender mapping as the columns that existed <em>before</em> this migration hold it, so the
    /// same projection can be read on either side of the upgrade and compared.
    /// </summary>
    private sealed record SenderRow(
        int BusinessId,
        string? AwsSenderId,
        int IsEnabled,
        string? LastMessageReceivedAtUtc,
        int Revision,
        string CreatedAtUtc,
        string UpdatedAtUtc);

    private static Task<List<SenderRow>> ReadSenderAsync(SqliteConnection connection) =>
        QueryAsync(
            connection,
            "SELECT BusinessId, AwsSenderId, IsEnabled, LastMessageReceivedAtUtc, Revision, "
                + "CreatedAtUtc, UpdatedAtUtc FROM BusinessNayaxSqsSenders ORDER BY BusinessId;",
            reader => new SenderRow(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6)));

    private static Task<List<int?>> ReadVerificationsAsync(SqliteConnection connection) =>
        QueryAsync(
            connection,
            "SELECT SqsVerification FROM NayaxSales ORDER BY TransactionID;",
            reader => reader.IsDBNull(0) ? (int?)null : reader.GetInt32(0));

    private static Task InsertBusinessAsync(SqliteConnection connection) =>
        ExecuteAsync(
            connection,
            "INSERT INTO Businesses (Id, Name, Status, CreatedAtUtc, TimeZoneId) "
                + "VALUES (1, 'Fictional Vending', 20, '2026-01-01 00:00:00', 'Australia/Sydney');");

    private static Task InsertSenderAsync(SqliteConnection connection) =>
        ExecuteAsync(
            connection,
            "INSERT INTO BusinessNayaxSqsSenders "
                + "(BusinessId, AwsSenderId, IsEnabled, Revision, CreatedAtUtc, UpdatedAtUtc) "
                + "VALUES (1, 'AIDAFICTIONALSENDERAA', 1, 1, '2026-11-06 00:00:00', "
                + "'2026-11-06 00:00:00');");

    /// <summary>
    /// A completed sale a deployment already holds, written with SQL on the <em>previous</em> schema.
    /// </summary>
    private static Task InsertSaleAsync(
        SqliteConnection connection, long transactionId, int? verification) =>
        ExecuteAsync(
            connection,
            "INSERT INTO NayaxSales "
                + "(BusinessId, TransactionID, TransactionStatusId, MachineID, SettlementValue, "
                + " MachineAuthorizationTime, Provider, ProviderProductIdIsRaw, CostingStatus, "
                + " CostSource, SqsVerification) "
                + $"VALUES (1, {transactionId}, 12, 4200001, 3.5, '2026-11-05 02:42:17', 1, 1, 0, 0, "
                + $"{verification?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL"});");

    private static Task InsertEvidenceAsync(
        SqliteConnection connection, int businessId, long saleId, int resultingState) =>
        ExecuteAsync(
            connection,
            "INSERT INTO NayaxSaleProviderEvidences "
                + "(BusinessId, NayaxSaleId, RunId, ObservedAtUtc, Kind, ResultingState) "
                + $"VALUES ({businessId}, {saleId}, '11111111-1111-1111-1111-111111111111', "
                + $"'2026-11-07 03:00:00', 10, {resultingState});");

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
