using Inventory.Infrastructure.Reporting.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

using Inventory.Domain.FinancialConfiguration;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational SQLite tests: reconciliation's period-based reimbursement matching, the card-gross
/// fallback cascade across devices/payments, and the fallback period depend on SQL translation,
/// EF Include graphs, and in-memory LINQ over navigation collections, not just simple queries.
/// </summary>
public class EfReconciliationReportFactsProviderTests
{
    private static async Task<SqliteConnection> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await setup.Database.EnsureCreatedAsync();
        return connection;
    }

    [Fact]
    public async Task No_reimbursements_returns_a_single_fallback_period_covering_the_requested_range()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 1,
            MachineID = 10,
            SettlementValue = 50m,
            PaymentMethod = "Credit Card",
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineAuthorizationTime = new DateTime(2025, 8, 10)
        });
        await db.SaveChangesAsync();
        var provider = new EfReconciliationReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), null, CancellationToken.None);

        Assert.False(facts.HasMatchedReimbursement);
        var period = Assert.Single(facts.Periods);
        Assert.False(period.HasImported);
        Assert.Equal(new DateTime(2025, 8, 1), period.From);
        Assert.Equal(new DateTime(2025, 8, 31), period.To);
        Assert.Equal(50m, period.CardSales);
    }

    [Fact]
    public async Task Reimbursement_period_fully_within_the_requested_range_is_matched()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 1,
            MachineID = 10,
            SettlementValue = 100m,
            PaymentMethod = "Credit Card",
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineAuthorizationTime = new DateTime(2025, 8, 10)
        });
        var file = new ImportedFile { FileName = "aug.xml", FileHash = "aug", ImportedAt = DateTime.UtcNow };
        file.Reimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2025, 8, 1),
            ReimbursementEndDate = new DateTime(2025, 8, 31),
            Total = 100.005m
        });
        db.ImportedFiles.Add(file);
        await db.SaveChangesAsync();
        var provider = new EfReconciliationReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), null, CancellationToken.None);

        Assert.True(facts.HasMatchedReimbursement);
        var period = Assert.Single(facts.Periods);
        Assert.True(period.HasImported);
        Assert.Equal(100.005m, period.ReportedGross);
        Assert.Equal(100.005m, period.ActualNetReimbursement);
    }

    [Fact]
    public async Task Reimbursement_period_outside_the_requested_range_is_not_matched_and_falls_back()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 2,
            MachineID = 10,
            SettlementValue = 50m,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineAuthorizationTime = new DateTime(2025, 8, 10)
        });
        var file = new ImportedFile { FileName = "jul.xml", FileHash = "jul", ImportedAt = DateTime.UtcNow };
        file.Reimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2025, 7, 1),
            ReimbursementEndDate = new DateTime(2025, 7, 31),
            Total = 50m
        });
        db.ImportedFiles.Add(file);
        await db.SaveChangesAsync();
        var provider = new EfReconciliationReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), null, CancellationToken.None);

        Assert.False(facts.HasMatchedReimbursement);
        var period = Assert.Single(facts.Periods);
        Assert.False(period.HasImported);
        Assert.Equal(0m, period.ReportedGross);
    }

    [Fact]
    public async Task Machine_filter_matches_the_reimbursement_device_for_the_selected_machine_only()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales
            {
                TransactionID = 10,
                MachineID = 1216029552,
                SettlementValue = 90.30m,
                PaymentMethod = "Credit Card",
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                MachineAuthorizationTime = new DateTime(2025, 8, 12)
            },
            new NayaxSales
            {
                TransactionID = 11,
                MachineID = 1216029562,
                SettlementValue = 42.90m,
                PaymentMethod = "Credit Card",
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                MachineAuthorizationTime = new DateTime(2025, 8, 12)
            });
        var file = new ImportedFile { FileName = "machine.xml", FileHash = "machine", ImportedAt = DateTime.UtcNow };
        file.Reimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2025, 8, 12),
            ReimbursementEndDate = new DateTime(2025, 8, 12),
            Total = 133.20m,
            Devices =
            {
                new ImportedReimbursementDevice { MachineNumber = "1216029552", TotalBillableTransactionAmount = 90.30m, NetAmount = 86.22m },
                new ImportedReimbursementDevice { MachineNumber = "1216029562", TotalBillableTransactionAmount = 42.90m, NetAmount = 41.03m }
            }
        });
        db.ImportedFiles.Add(file);
        await db.SaveChangesAsync();
        var provider = new EfReconciliationReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 12), new DateTime(2025, 8, 12), 1216029552, CancellationToken.None);

        var period = Assert.Single(facts.Periods);
        Assert.Equal(90.30m, period.CardSales);
        Assert.Equal(90.30m, period.ReportedGross);
    }

    [Fact]
    public async Task Fee_rows_split_into_processing_and_other_fees_with_gst_and_payment_rows_give_the_card_gross()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales
            {
                TransactionID = 40,
                MachineID = 10,
                SettlementValue = 100m,
                PaymentMethod = "Credit Card",
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                MachineAuthorizationTime = new DateTime(2025, 8, 12)
            },
            new NayaxSales
            {
                TransactionID = 41,
                MachineID = 10,
                SettlementValue = 25m,
                PaymentMethod = "Cash",
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                MachineAuthorizationTime = new DateTime(2025, 8, 12)
            });
        var file = new ImportedFile { FileName = "fees.xml", FileHash = "fees", ImportedAt = DateTime.UtcNow };
        file.Reimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2025, 8, 12),
            ReimbursementEndDate = new DateTime(2025, 8, 12),
            ReimbursementPayoutDate = new DateTime(2025, 8, 14),
            Total = 88m,
            Devices = { new ImportedReimbursementDevice { EntityId = "device-1", MachineNumber = "10", TotalBillableTransactionAmount = 100m, TotalBillableTransactionCount = 1 } },
            DevicePayments = { new ImportedDevicePayment { EntityId = "device-1", PaymentMethodDescription = "Credit Card", SalesCount = 1, TotalSum = 100m } },
            Fees =
            {
                new ImportedFee { FeeTypeDescription = "Processing fee", TotalSum = 2m, TotalSumWithVat = 2.2m, VatPercentage = 10m },
                new ImportedFee { FeeTypeDescription = "Service fee", TotalSum = 3m, TotalSumWithVat = 3.3m, VatPercentage = 10m }
            }
        });
        db.ImportedFiles.Add(file);
        await db.SaveChangesAsync();
        var provider = new EfReconciliationReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 12), new DateTime(2025, 8, 12), null, CancellationToken.None);

        var period = Assert.Single(facts.Periods);
        Assert.Equal(125m, period.TotalVendingSales);
        Assert.Equal(100m, period.CardSales);
        Assert.Equal(25m, period.CashSales);
        Assert.Equal(100m, period.ReportedGross);
        Assert.Equal(2m, period.ProcessingFeesExGst);
        Assert.Equal(0.5m, period.FeeGst);
        Assert.Equal(3m, period.OtherFees);
        Assert.Equal(new DateTime(2025, 8, 14), period.PayoutDate);
    }

    [Fact]
    public async Task Non_completed_sales_are_excluded_from_gross_but_counted_by_status_for_quality_notes()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 12m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 2, MachineID = 10, SettlementValue = 5m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.PendingSettlementNotFinal },
            new NayaxSales { TransactionID = 3, MachineID = 10, SettlementValue = 4m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Refunded },
            new NayaxSales { TransactionID = 4, MachineID = 10, SettlementValue = 3m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = 21 },
            new NayaxSales { TransactionID = 5, MachineID = 10, SettlementValue = 7m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = null });
        await db.SaveChangesAsync();
        var provider = new EfReconciliationReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        Assert.Equal(1, facts.TotalTransactionCount);
        Assert.Equal(1, facts.PendingTransactionCount);
        Assert.Equal(1, facts.RefundedTransactionCount);
        Assert.Equal(1, facts.UnknownStatusTransactionCount);
        Assert.Equal(1, facts.NullStatusTransactionCount);
    }

    /// <summary>
    /// Issue #477: each reimbursement period reports the non-completed transactions inside its own
    /// coverage dates, so a period-level data-quality flag describes that period rather than the
    /// whole requested range. The aggregate counts must never be repeated onto every period.
    /// </summary>
    [Fact]
    public async Task Each_matched_period_counts_only_the_non_completed_transactions_inside_its_own_dates()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 20m, PaymentMethod = "Credit Card", MachineAuthorizationTime = new DateTime(2025, 8, 5), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 2, MachineID = 10, SettlementValue = 30m, PaymentMethod = "Credit Card", MachineAuthorizationTime = new DateTime(2025, 8, 20), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            // Second period only: one cancelled, one pending, one unrecognised and one absent status.
            new NayaxSales { TransactionID = 3, MachineID = 10, SettlementValue = 4m, PaymentMethod = "Credit Card", MachineAuthorizationTime = new DateTime(2025, 8, 20), TransactionStatusId = NayaxTransactionStatusIds.CancelledOrDeclined26 },
            new NayaxSales { TransactionID = 4, MachineID = 10, SettlementValue = 4m, PaymentMethod = "Credit Card", MachineAuthorizationTime = new DateTime(2025, 8, 21), TransactionStatusId = NayaxTransactionStatusIds.PendingBatch },
            new NayaxSales { TransactionID = 5, MachineID = 10, SettlementValue = 4m, PaymentMethod = "Credit Card", MachineAuthorizationTime = new DateTime(2025, 8, 22), TransactionStatusId = 21 },
            new NayaxSales { TransactionID = 6, MachineID = 10, SettlementValue = 4m, PaymentMethod = "Credit Card", MachineAuthorizationTime = new DateTime(2025, 8, 23), TransactionStatusId = null },
            // First period only: one refunded row.
            new NayaxSales { TransactionID = 7, MachineID = 10, SettlementValue = 4m, PaymentMethod = "Credit Card", MachineAuthorizationTime = new DateTime(2025, 8, 6), TransactionStatusId = NayaxTransactionStatusIds.Refunded });
        var file = new ImportedFile { FileName = "two-periods.xml", FileHash = "two-periods", ImportedAt = DateTime.UtcNow };
        file.Reimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2025, 8, 1),
            ReimbursementEndDate = new DateTime(2025, 8, 15),
            Total = 20m
        });
        file.Reimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2025, 8, 16),
            ReimbursementEndDate = new DateTime(2025, 8, 31),
            Total = 30m
        });
        db.ImportedFiles.Add(file);
        await db.SaveChangesAsync();
        var provider = new EfReconciliationReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), null, CancellationToken.None);

        Assert.Equal(2, facts.Periods.Count);
        var first = facts.Periods[0];
        var second = facts.Periods[1];
        Assert.Equal(0, first.PendingTransactionCount);
        Assert.Equal(1, first.RefundedTransactionCount);
        Assert.Equal(0, first.DeclinedOrCancelledTransactionCount);
        Assert.Equal(0, first.UnknownStatusTransactionCount);
        Assert.Equal(0, first.MissingStatusTransactionCount);
        Assert.Equal(1, second.PendingTransactionCount);
        Assert.Equal(0, second.RefundedTransactionCount);
        Assert.Equal(1, second.DeclinedOrCancelledTransactionCount);
        Assert.Equal(1, second.UnknownStatusTransactionCount);
        Assert.Equal(1, second.MissingStatusTransactionCount);
        // The aggregate still covers the whole requested range.
        Assert.Equal(1, facts.PendingTransactionCount);
        Assert.Equal(1, facts.RefundedTransactionCount);
        Assert.Equal(1, facts.DeclinedOrCancelledTransactionCount);
        Assert.Equal(1, facts.UnknownStatusTransactionCount);
        Assert.Equal(1, facts.NullStatusTransactionCount);
    }

    /// <summary>
    /// Issue #477: with no matched reimbursement the single fallback period is the requested range,
    /// so its own counts are the range's counts - still read per period, not copied from the
    /// aggregate.
    /// </summary>
    [Fact]
    public async Task The_fallback_period_counts_the_non_completed_transactions_in_the_requested_range()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 20m, PaymentMethod = "Credit Card", MachineAuthorizationTime = new DateTime(2025, 8, 5), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 2, MachineID = 10, SettlementValue = 4m, PaymentMethod = "Credit Card", MachineAuthorizationTime = new DateTime(2025, 8, 6), TransactionStatusId = NayaxTransactionStatusIds.CancelledOrDeclined31 },
            new NayaxSales { TransactionID = 3, MachineID = 10, SettlementValue = 4m, PaymentMethod = "Credit Card", MachineAuthorizationTime = new DateTime(2025, 8, 7), TransactionStatusId = null });
        await db.SaveChangesAsync();
        var provider = new EfReconciliationReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), null, CancellationToken.None);

        var period = Assert.Single(facts.Periods);
        Assert.False(period.HasImported);
        Assert.Equal(1, period.DeclinedOrCancelledTransactionCount);
        Assert.Equal(1, period.MissingStatusTransactionCount);
        Assert.Equal(0, period.RefundedTransactionCount);
    }
}
