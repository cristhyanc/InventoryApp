using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Transactions;
using Inventory.Application.Time;
using Inventory.Domain.FinancialConfiguration;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Issue #380 (owner decision on PR #392): the daily report buckets and filters sales by the
/// <c>Australia/Sydney</c> business day, the same day the Sites/Machines dashboards and Transaction
/// Sales put the sale on.
///
/// The requested range is a pair of inclusive Sydney business dates. Its sales are the ones from the
/// UTC instant of Sydney midnight at the start of the first date up to, exclusively, the UTC instant of
/// Sydney midnight at the start of the day after the last date, and each sale's row is the Sydney date
/// of its instant. Imported reimbursement coverage dates are date-only values and are not shifted.
///
/// Every test runs the real <c>SydneyBusinessCalendar</c> over a real (non-InMemory) SQLite database,
/// because the boundaries are UTC instants compared in SQL and a Sydney day is 23, 24 or 25 hours long.
/// </summary>
public class DailyReportSydneyBusinessDayTests
{
    private static readonly IBusinessCalendar Sydney = new FixedSydneyTime(new DateTime(2026, 10, 5, 3, 0, 0)).Calendar;

    /// <summary>
    /// 00:30 on Monday 5 October 2026 in Sydney (AEDT, +11), which is 13:30Z on Sunday 4 October:
    /// the UTC date and the Sydney date differ, which is exactly where the old UTC-date bucketing put
    /// the sale on the wrong row.
    /// </summary>
    private static readonly DateTime JustAfterSydneyMidnightUtc = new(2026, 10, 4, 13, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_sale_just_after_Sydney_midnight_is_on_the_same_day_in_daily_transaction_sales_and_the_dashboard()
    {
        await using var connection = await CreateSqliteAsync();
        await using (var seed = Context(connection))
        {
            seed.NayaxSales.Add(Sale(1, JustAfterSydneyMidnightUtc, 3.5m));
            await seed.SaveChangesAsync();
        }

        await using var db = Context(connection);
        var monday = new DateTime(2026, 10, 5);
        var sunday = new DateTime(2026, 10, 4);

        var mondayFacts = await Provider(db).GetFactsAsync(monday, monday, null, CancellationToken.None);
        var sundayFacts = await Provider(db).GetFactsAsync(sunday, sunday, null, CancellationToken.None);

        var day = Assert.Single(mondayFacts.Days);
        Assert.Equal(monday, day.Date);
        Assert.Equal(DateTimeKind.Unspecified, day.Date.Kind);
        Assert.Equal(3.5m, day.GrossSales);
        Assert.Empty(sundayFacts.Days);

        var transactions = await new GetTransactionSalesReport(new EfTransactionSalesReportFactsProvider(db))
            .Handle(new TransactionSalesFilterDto(From: sunday, To: monday), CancellationToken.None);
        var transaction = Assert.Single(transactions.Rows);
        Assert.Equal(monday, Sydney.ToBusinessDate(transaction.TransactionDate));

        var window = new FixedSydneyTime(new DateTime(2026, 10, 5, 3, 0, 0)).Window;
        Assert.Equal(monday, window.BusinessToday);
        Assert.True(JustAfterSydneyMidnightUtc >= window.Today.StartUtc && JustAfterSydneyMidnightUtc <= window.Today.EndUtc);
    }

    public static TheoryData<string, DateTime, DateTime, DateTime, int> BusinessDays => new()
    {
        // Wednesday 12 August 2026, a normal AEST (+10) day: 24 hours.
        { "normal AEST day", new DateTime(2026, 8, 12), Utc(2026, 8, 11, 14), Utc(2026, 8, 12, 14), 24 },
        // Wednesday 18 November 2026, a normal AEDT (+11) day: 24 hours.
        { "normal AEDT day", new DateTime(2026, 11, 18), Utc(2026, 11, 17, 13), Utc(2026, 11, 18, 13), 24 },
        // Sunday 4 October 2026, daylight saving starts at 02:00 AEST: 23 hours.
        { "DST start", new DateTime(2026, 10, 4), Utc(2026, 10, 3, 14), Utc(2026, 10, 4, 13), 23 },
        // Sunday 5 April 2026, daylight saving ends at 03:00 AEDT: 25 hours.
        { "DST end", new DateTime(2026, 4, 5), Utc(2026, 4, 4, 13), Utc(2026, 4, 5, 14), 25 },
    };

    /// <summary>
    /// Both ends of a one-day request: the first instant of the Sydney day is in, the instant before it
    /// is not; the last instant before the next Sydney midnight is in, that midnight is not. The day's
    /// expected UTC bounds are literals, so the test pins the calendar rather than restating it.
    /// </summary>
    [Theory]
    [MemberData(nameof(BusinessDays))]
    public async Task Daily_filtering_includes_exactly_the_UTC_instants_of_the_Sydney_business_day(
        string scenario, DateTime businessDate, DateTime startUtc, DateTime nextStartUtc, int hours)
    {
        Assert.Equal(hours, (nextStartUtc - startUtc).TotalHours);
        Assert.Equal(startUtc, Sydney.StartOfBusinessDayUtc(businessDate));

        await using var connection = await CreateSqliteAsync();
        await using (var seed = Context(connection))
        {
            seed.NayaxSales.AddRange(
                Sale(1, startUtc.AddSeconds(-1), 1m),
                Sale(2, startUtc, 2m),
                Sale(3, nextStartUtc.AddSeconds(-1), 4m),
                Sale(4, nextStartUtc, 8m));
            await seed.SaveChangesAsync();
        }

        await using var db = Context(connection);
        var facts = await Provider(db).GetFactsAsync(businessDate, businessDate, null, CancellationToken.None);

        var day = Assert.Single(facts.Days);
        Assert.Equal(businessDate, day.Date);
        Assert.True(6m == day.GrossSales, $"{scenario}: expected only the two sales inside the Sydney day, got {day.GrossSales}.");
        Assert.Equal(2, day.TransactionCount);
        Assert.Equal(2, facts.Totals.CompletedTransactionCount);
    }

    /// <summary>
    /// A two-day request spanning the 4 October 2026 daylight-saving start: each sale lands on its own
    /// Sydney date, and the per-day revenue, COGS, card counts, status counts and fees add up to the
    /// period totals. The fee estimate for a day counts exactly the card sales that day's revenue
    /// counted, because both are bucketed by the same Sydney date.
    /// </summary>
    [Fact]
    public async Task Daily_rows_totals_status_counts_cogs_and_fees_describe_the_same_Sydney_days()
    {
        await using var connection = await CreateSqliteAsync();
        await using (var seed = Context(connection))
        {
            seed.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2026, 1, 1), FeeExGst = 0.10m });
            seed.NayaxSales.AddRange(
                // 23:30 Saturday 3 October (AEST) - 13:30Z on the 3rd: before the range.
                Sale(1, Utc(2026, 10, 3, 13, 30), 100m, cost: 1m),
                // 00:30 Sunday 4 October (AEST) - 14:30Z on the 3rd.
                Sale(2, Utc(2026, 10, 3, 14, 30), 2m, cost: 1m),
                // 03:30 Sunday 4 October (AEDT, after the skipped hour) - 16:30Z on the 3rd; pending.
                Sale(3, Utc(2026, 10, 3, 16, 30), 50m, status: NayaxTransactionStatusIds.PendingSettlementNotFinal),
                // 23:30 Sunday 4 October (AEDT) - 12:30Z on the 4th.
                Sale(4, Utc(2026, 10, 4, 12, 30), 3m, cost: 1.5m),
                // 00:30 Monday 5 October (AEDT) - 13:30Z on the 4th.
                Sale(5, Utc(2026, 10, 4, 13, 30), 5m, cost: 2m, paymentMethod: "Cash"),
                // 10:30 Monday 5 October (AEDT) - 23:30Z on the 4th.
                Sale(6, Utc(2026, 10, 4, 23, 30), 7m, cost: 3m),
                // 00:30 Tuesday 6 October (AEDT) - 13:30Z on the 5th: after the range.
                Sale(7, Utc(2026, 10, 5, 13, 30), 100m, cost: 1m));
            await seed.SaveChangesAsync();
        }

        await using var db = Context(connection);
        var facts = await Provider(db).GetFactsAsync(new DateTime(2026, 10, 4), new DateTime(2026, 10, 5), null, CancellationToken.None);

        Assert.Equal([new DateTime(2026, 10, 4), new DateTime(2026, 10, 5)], facts.Days.Select(x => x.Date).ToList());
        var sunday = facts.Days[0];
        var monday = facts.Days[1];

        Assert.Equal(5m, sunday.GrossSales);
        Assert.Equal(2, sunday.TransactionCount);
        Assert.Equal(2.5m, sunday.PartialCostOfGoods);
        Assert.Equal(1, sunday.PendingTransactionCount);
        Assert.Equal(2, sunday.CompletedTransactionCount);
        Assert.Equal(2, sunday.ProcessingFees.EstimatedCardTransactionCount);

        Assert.Equal(12m, monday.GrossSales);
        Assert.Equal(5m, monday.CashSales);
        Assert.Equal(7m, monday.CardSales);
        Assert.Equal(5m, monday.PartialCostOfGoods);
        Assert.Equal(2, monday.CompletedTransactionCount);
        Assert.Equal(1, monday.ProcessingFees.EstimatedCardTransactionCount);

        Assert.Equal(sunday.PartialCostOfGoods + monday.PartialCostOfGoods, facts.Totals.PartialCostOfGoods);
        Assert.True(facts.Totals.IsCogsComplete);
        Assert.Equal(4, facts.Totals.CompletedTransactionCount);
        Assert.Equal(1, facts.Totals.PendingTransactionCount);
        Assert.Equal(3, facts.Totals.ProcessingFees.EstimatedCardTransactionCount);
        Assert.Equal(
            sunday.ProcessingFees.TotalFeeExGst + monday.ProcessingFees.TotalFeeExGst,
            facts.Totals.ProcessingFees.TotalFeeExGst);
    }

    /// <summary>
    /// The request end-to-end through the use case: the API/export DTO's rows and totals carry the
    /// same Sydney days, and the totals equal the sum of the rows.
    /// </summary>
    [Fact]
    public async Task The_daily_report_dto_rows_and_totals_follow_the_Sydney_business_day()
    {
        await using var connection = await CreateSqliteAsync();
        await using (var seed = Context(connection))
        {
            seed.NayaxSales.AddRange(
                Sale(1, JustAfterSydneyMidnightUtc, 3.5m, cost: 1m),
                Sale(2, Utc(2026, 10, 4, 12, 30), 2m, cost: 1m));
            await seed.SaveChangesAsync();
        }

        await using var db = Context(connection);
        var report = await new GetDailyReport(Provider(db)).Handle(
            new Inventory.Application.Reporting.Shared.ReportingFilterDto(From: new DateTime(2026, 10, 5), To: new DateTime(2026, 10, 5)),
            CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal(new DateTime(2026, 10, 5), row.Date);
        var totals = Assert.IsType<DailyReportTotalsDto>(report.Totals);
        Assert.Equal(3.5m, totals.GrossSales);
        Assert.Equal(3.5m, report.Totals.GrossSales);
        Assert.Equal(1, totals.CompletedTransactionCount);
    }

    /// <summary>
    /// Reimbursement coverage dates are date-only values: a single-day reimbursement for 5 October is
    /// on the 5 October row, and a multi-day one marks exactly its own dates as period-only. Neither is
    /// moved by the Sydney/UTC conversion that the sale instants go through.
    /// </summary>
    [Fact]
    public async Task Date_only_reimbursement_coverage_is_not_timezone_shifted()
    {
        await using var connection = await CreateSqliteAsync();
        await using (var seed = Context(connection))
        {
            seed.NayaxSales.AddRange(
                Sale(1, JustAfterSydneyMidnightUtc, 10m),
                Sale(2, Utc(2026, 10, 5, 23, 30), 4m));
            var file = new ImportedFile { FileName = "oct.xml", FileHash = "oct", ImportedAt = DateTime.UtcNow };
            file.Reimbursements.Add(new ImportedReimbursement
            {
                ReimbursementStartDate = new DateTime(2026, 10, 5),
                ReimbursementEndDate = new DateTime(2026, 10, 5),
                Total = 10m
            });
            file.Reimbursements.Add(new ImportedReimbursement
            {
                ReimbursementStartDate = new DateTime(2026, 10, 6),
                ReimbursementEndDate = new DateTime(2026, 10, 7),
                Total = 20m
            });
            seed.ImportedFiles.Add(file);
            await seed.SaveChangesAsync();
        }

        await using var db = Context(connection);
        var facts = await Provider(db).GetFactsAsync(new DateTime(2026, 10, 5), new DateTime(2026, 10, 6), null, CancellationToken.None);

        Assert.Equal([new DateTime(2026, 10, 5), new DateTime(2026, 10, 6)], facts.Days.Select(x => x.Date).ToList());
        Assert.True(facts.Days[0].HasImportedReimbursement);
        Assert.Equal(10m, facts.Days[0].ImportedReimbursement);
        Assert.False(facts.Days[0].HasPeriodOnlyImportedData);
        Assert.False(facts.Days[1].HasImportedReimbursement);
        Assert.True(facts.Days[1].HasPeriodOnlyImportedData);
    }

    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static NayaxSales Sale(
        long transactionId,
        DateTime instantUtc,
        decimal value,
        decimal? cost = null,
        int status = NayaxTransactionStatusIds.Completed,
        string paymentMethod = "Credit Card") => new()
        {
            TransactionID = transactionId,
            MachineID = 10,
            SettlementValue = value,
            PaymentMethod = paymentMethod,
            MachineAuthorizationTime = instantUtc,
            TransactionStatusId = status,
            CostOfGoodsSold = cost
        };

    private static EfDailyReportFactsProvider Provider(AppDbContext db) =>
        new(db, TestFinancialUseCases.ProcessingFees(db, Sydney), Sydney);

    private static async Task<SqliteConnection> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = Context(connection);
        await setup.Database.EnsureCreatedAsync();
        return connection;
    }

    private static AppDbContext Context(SqliteConnection connection) =>
        TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
}
