using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Issue #1105's migration: one additive, tenant-owned, append-only audit table, no backfill, and
/// nothing existing changed. The three new verification states need no schema.
/// </summary>
public class AddNayaxSaleOwnerDecisionsMigrationTests
{
    private const string PreviousMigration = "20261107004752_AddNayaxSaleProviderEvidence";
    private const string TargetMigration = "20261107100349_AddNayaxSaleOwnerDecisions";

    [Fact]
    public async Task The_migration_creates_the_audit_table_with_its_business_and_sale_indexes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, PreviousMigration);
        Assert.DoesNotContain("NayaxSaleOwnerDecisionAudits", await MigrationSchemaProbe.TableNamesAsync(connection));
        var salesColumnsBefore = await MigrationSchemaProbe.ColumnNamesAsync(connection, "NayaxSales");

        await MigrateToAsync(db, TargetMigration);

        Assert.Contains("NayaxSaleOwnerDecisionAudits", await MigrationSchemaProbe.TableNamesAsync(connection));
        var columns = await MigrationSchemaProbe.ColumnNamesAsync(connection, "NayaxSaleOwnerDecisionAudits");
        foreach (var column in new[]
                 {
                     "BusinessId", "NayaxSaleId", "TransactionId", "Kind", "Decision", "EvidenceId", "Reason",
                     "ActorDirectoryTenantId", "ActorObjectId", "OccurredAtUtc", "PreviousState", "ResultingState",
                     "PreviousProductId", "ResultingProductId", "PreviousNayaxProductId", "ResultingNayaxProductId",
                     "PreviousCountedAmount", "ResultingCountedAmount", "ReconciledPeriodFrom", "ReconciledPeriodTo",
                     "ReconciledPeriodDifference",
                 })
        {
            Assert.Contains(column, columns);
        }

        var indexes = await MigrationSchemaProbe.IndexNamesAsync(connection, "NayaxSaleOwnerDecisionAudits");
        Assert.Contains("IX_NayaxSaleOwnerDecisionAudits_BusinessId", indexes);
        Assert.Contains("IX_NayaxSaleOwnerDecisionAudits_BusinessId_NayaxSaleId_Id", indexes);

        // No backfill and no change to the sales table: every existing sale keeps its values.
        Assert.Equal(salesColumnsBefore, await MigrationSchemaProbe.ColumnNamesAsync(connection, "NayaxSales"));
    }

    [Fact]
    public async Task Down_drops_only_the_audit_table()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));
        await MigrateToAsync(db, TargetMigration);
        await MigrateToAsync(db, PreviousMigration);

        var tables = await MigrationSchemaProbe.TableNamesAsync(connection);
        Assert.DoesNotContain("NayaxSaleOwnerDecisionAudits", tables);
        Assert.Contains("NayaxSaleProviderEvidences", tables);
    }

    private static Task MigrateToAsync(AppDbContext db, string targetMigration) =>
        db.GetService<IMigrator>().MigrateAsync(targetMigration);

    private static DbContextOptions<AppDbContext> OptionsFor(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
}
