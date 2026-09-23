using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational SQLite tests: the imported-summary query this provider reuses depends on SQL
/// translation and FK-backed navigation collections, not just in-memory LINQ semantics.
/// </summary>
public class EfGstReportFactsProviderTests
{
    private static async Task<SqliteConnection> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await setup.Database.EnsureCreatedAsync();
        return connection;
    }

    [Fact]
    public async Task No_imported_reimbursements_in_range_reports_no_rows_and_no_gst_classification()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var provider = new EfGstReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), null, CancellationToken.None);

        Assert.False(facts.ImportedContainsRows);
        Assert.False(facts.ImportedContainsGstClassification);
    }

    [Fact]
    public async Task Imported_reimbursement_with_a_vat_classified_fee_reports_rows_and_gst_classification()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var file = new ImportedFile { FileName = "aug.xml", FileHash = "aug", ImportedAt = DateTime.UtcNow };
        file.Reimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2025, 8, 1),
            ReimbursementEndDate = new DateTime(2025, 8, 31),
            Total = 110m,
            Fees = { new ImportedFee { FeeTypeDescription = "Processing fee", TotalSum = 10m, TotalSumWithVat = 11m, VatPercentage = 10m } }
        });
        db.ImportedFiles.Add(file);
        await db.SaveChangesAsync();
        var provider = new EfGstReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), null, CancellationToken.None);

        Assert.True(facts.ImportedContainsRows);
        Assert.True(facts.ImportedContainsGstClassification);
    }

    [Fact]
    public async Task Imported_reimbursement_without_a_vat_percentage_reports_rows_but_no_gst_classification()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var file = new ImportedFile { FileName = "aug.xml", FileHash = "aug", ImportedAt = DateTime.UtcNow };
        file.Reimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2025, 8, 1),
            ReimbursementEndDate = new DateTime(2025, 8, 31),
            Total = 110m,
            Fees = { new ImportedFee { FeeTypeDescription = "Processing fee", TotalSum = 10m, TotalSumWithVat = null, VatPercentage = null } }
        });
        db.ImportedFiles.Add(file);
        await db.SaveChangesAsync();
        var provider = new EfGstReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), null, CancellationToken.None);

        Assert.True(facts.ImportedContainsRows);
        Assert.False(facts.ImportedContainsGstClassification);
    }

    [Fact]
    public async Task A_reimbursement_period_outside_the_requested_range_is_not_counted()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var file = new ImportedFile { FileName = "jul.xml", FileHash = "jul", ImportedAt = DateTime.UtcNow };
        file.Reimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2025, 7, 1),
            ReimbursementEndDate = new DateTime(2025, 7, 31),
            Total = 50m,
            Fees = { new ImportedFee { FeeTypeDescription = "Processing fee", TotalSum = 5m, TotalSumWithVat = 5.5m, VatPercentage = 10m } }
        });
        db.ImportedFiles.Add(file);
        await db.SaveChangesAsync();
        var provider = new EfGstReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), null, CancellationToken.None);

        Assert.False(facts.ImportedContainsRows);
        Assert.False(facts.ImportedContainsGstClassification);
    }
}
