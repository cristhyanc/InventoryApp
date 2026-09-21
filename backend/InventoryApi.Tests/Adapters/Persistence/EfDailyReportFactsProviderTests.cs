using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational SQLite tests: daily's per-day grouping, card/cash split, COGS completeness, and
/// date-boundary behavior depend on SQL translation and ordering, not just in-memory LINQ semantics.
/// </summary>
public class EfDailyReportFactsProviderTests
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
    public async Task Groups_sales_into_one_row_per_calendar_day_ordered_ascending()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 10m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 2), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 2, MachineID = 10, SettlementValue = 5m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 3, MachineID = 10, SettlementValue = 7m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed });
        await db.SaveChangesAsync();
        var provider = new EfDailyReportFactsProvider(db, new NayaxProcessingFeeService(db));

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 2), null, CancellationToken.None);

        Assert.Equal(2, facts.Days.Count);
        Assert.Equal(new DateTime(2025, 8, 1), facts.Days[0].Date);
        Assert.Equal(12m, facts.Days[0].GrossSales);
        Assert.Equal(new DateTime(2025, 8, 2), facts.Days[1].Date);
        Assert.Equal(10m, facts.Days[1].GrossSales);
    }

    [Fact]
    public async Task Splits_card_and_cash_sales_per_day_and_reports_cogs_completeness()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 10m, PaymentMethod = "Credit Card", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 4m },
            new NayaxSales { TransactionID = 2, MachineID = 10, SettlementValue = 5m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = null });
        await db.SaveChangesAsync();
        var provider = new EfDailyReportFactsProvider(db, new NayaxProcessingFeeService(db));

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        var day = Assert.Single(facts.Days);
        Assert.Equal(15m, day.GrossSales);
        Assert.Equal(10m, day.CardSales);
        Assert.Equal(5m, day.CashSales);
        Assert.False(day.IsCogsComplete);
        Assert.Equal(1, day.UncostedTransactionCount);
        Assert.Equal(5m, day.UncostedSalesAmount);
        Assert.Equal(4m, day.PartialCostOfGoods);
        Assert.False(facts.Totals.IsCogsComplete);
        Assert.Equal(4m, facts.Totals.PartialCostOfGoods);
    }

    [Fact]
    public async Task Date_range_is_inclusive_of_both_boundary_days_and_excludes_the_day_after()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 1m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 7, 31, 23, 59, 0), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 2, MachineID = 10, SettlementValue = 2m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1, 0, 0, 0), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 3, MachineID = 10, SettlementValue = 4m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 2, 0, 0, 0), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 4, MachineID = 10, SettlementValue = 8m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 3, 0, 0, 0), TransactionStatusId = NayaxTransactionStatusIds.Completed });
        await db.SaveChangesAsync();
        var provider = new EfDailyReportFactsProvider(db, new NayaxProcessingFeeService(db));

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 2), null, CancellationToken.None);

        Assert.Equal(2, facts.Days.Count);
        Assert.Equal(6m, facts.Days.Sum(x => x.GrossSales));
    }

    [Fact]
    public async Task Machine_filter_scopes_sales_to_the_selected_machine_only()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 10m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 2, MachineID = 20, SettlementValue = 30m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed });
        await db.SaveChangesAsync();
        var provider = new EfDailyReportFactsProvider(db, new NayaxProcessingFeeService(db));

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), 10, CancellationToken.None);

        var day = Assert.Single(facts.Days);
        Assert.Equal(10m, day.GrossSales);
    }

    [Fact]
    public async Task Non_completed_sales_are_excluded_from_gross_sales_but_counted_by_status()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 12m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 2, MachineID = 10, SettlementValue = 5m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.PendingSettlementNotFinal },
            new NayaxSales { TransactionID = 3, MachineID = 10, SettlementValue = 4m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Refunded },
            new NayaxSales { TransactionID = 4, MachineID = 10, SettlementValue = 3m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = 21 },
            new NayaxSales { TransactionID = 5, MachineID = 10, SettlementValue = 7m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = null });
        await db.SaveChangesAsync();
        var provider = new EfDailyReportFactsProvider(db, new NayaxProcessingFeeService(db));

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        var day = Assert.Single(facts.Days);
        Assert.Equal(12m, day.GrossSales);
        Assert.Equal(1, day.PendingTransactionCount);
        Assert.Equal(1, day.RefundedTransactionCount);
        Assert.Equal(2, day.UnknownStatusTransactionCount);
        Assert.True(day.HasDataQualityWarning);
        Assert.Equal(5, facts.Totals.CompletedTransactionCount + facts.Totals.PendingTransactionCount +
            facts.Totals.RefundedTransactionCount + facts.Totals.UnknownStatusTransactionCount);
        Assert.Equal(1, facts.Totals.NullStatusTransactionCount);
    }

    [Fact]
    public async Task Single_day_reimbursement_is_matched_to_its_calendar_day_and_reconciles_within_tolerance()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 1, MachineID = 10, SettlementValue = 10m, PaymentMethod = "Credit Card",
            MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 4m
        });
        var file = new ImportedFile { FileName = "aug.xml", FileHash = "aug", ImportedAt = DateTime.UtcNow };
        file.Reimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2025, 8, 1),
            ReimbursementEndDate = new DateTime(2025, 8, 1),
            Total = 10m
        });
        db.ImportedFiles.Add(file);
        await db.SaveChangesAsync();
        var provider = new EfDailyReportFactsProvider(db, new NayaxProcessingFeeService(db));

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        var day = Assert.Single(facts.Days);
        Assert.True(day.HasImportedReimbursement);
        Assert.Equal(10m, day.ImportedReimbursement);
        Assert.False(day.HasPeriodOnlyImportedData);
    }

    [Fact]
    public async Task Multi_day_reimbursement_is_marked_period_only_and_not_allocated_to_a_single_day()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 1, MachineID = 10, SettlementValue = 10m, PaymentMethod = "Credit Card",
            MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 4m
        });
        var file = new ImportedFile { FileName = "aug.xml", FileHash = "aug2", ImportedAt = DateTime.UtcNow };
        file.Reimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2025, 8, 1),
            ReimbursementEndDate = new DateTime(2025, 8, 2),
            Total = 20m
        });
        db.ImportedFiles.Add(file);
        await db.SaveChangesAsync();
        var provider = new EfDailyReportFactsProvider(db, new NayaxProcessingFeeService(db));

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        var day = Assert.Single(facts.Days);
        Assert.False(day.HasImportedReimbursement);
        Assert.True(day.HasPeriodOnlyImportedData);
        Assert.True(facts.HasAnyPeriodOnlyImportedData);
    }

    [Fact]
    public async Task No_sales_in_range_returns_no_days_and_incomplete_cogs_flag_stays_true()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var provider = new EfDailyReportFactsProvider(db, new NayaxProcessingFeeService(db));

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        Assert.Empty(facts.Days);
        Assert.True(facts.Totals.IsCogsComplete);
        Assert.Equal(0m, facts.Totals.PartialCostOfGoods);
    }
}
