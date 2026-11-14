using Inventory.Application.Commissions;
using Inventory.Application.Nayax;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Dashboard;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Export;
using Inventory.Application.Reporting.Gst;
using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.Reporting.Transactions;
using Inventory.Application.Time;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Reporting.Persistence;
using Inventory.Infrastructure.Sites;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Issue #1250 (the repository owner's decision of 13 November 2026): bookkeeping, the GST
/// accounting aid, the reports dashboard and reconciliation count a sale on its <em>business</em>
/// date - <c>Australia/Sydney</c> for this business - exactly as the daily report
/// (<see cref="DailyReportSydneyBusinessDayTests"/>) and the Sites/Machines dashboards already do.
///
/// The requested <c>from</c>/<c>to</c> are inclusive business dates, so each report's sales run from
/// the UTC instant of business-day midnight at the start of the first date up to, exclusively,
/// business-day midnight at the start of the day after the last - 23, 24 or 25 hours per day.
/// Reimbursement coverage, expense and purchase dates are date-only values and are not shifted.
///
/// Every test runs the real <c>ZonedBusinessCalendar</c> over a real (non-InMemory) SQLite database,
/// because the boundaries are UTC instants compared in SQL and a Sydney day is 23, 24 or 25 hours
/// long.
/// </summary>
public class MoneyReportSydneyBusinessDayTests
{
    private static readonly IBusinessCalendar Sydney = new FixedSydneyTime(new DateTime(2026, 10, 5, 3, 0, 0)).Calendar;

    private static readonly DateTime Sunday = new(2026, 10, 4);
    private static readonly DateTime Monday = new(2026, 10, 5);

    /// <summary>The one machine every seeded sale is recorded against, and the site it is mapped to.</summary>
    private const long MachineId = 10;
    private const long SiteId = 91;

    /// <summary>
    /// 00:30 on Monday 5 October 2026 in Sydney (AEDT, +11), which is 13:30Z on Sunday 4 October:
    /// the UTC date and the Sydney date differ, which is where UTC-date selection put the sale in the
    /// wrong period.
    /// </summary>
    private static readonly DateTime JustAfterSydneyMidnightUtc = new(2026, 10, 4, 13, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Bookkeeping_counts_a_sale_just_after_Sydney_midnight_on_the_Sydney_date()
    {
        await using var connection = await CreateSqliteAsync();
        await using (var seed = Context(connection))
        {
            seed.NayaxSales.Add(Sale(1, JustAfterSydneyMidnightUtc, 3.5m, cost: 1m));
            await seed.SaveChangesAsync();
        }

        await using var db = Context(connection);
        var monday = await Bookkeeping(db).Handle(Filter(Monday), CancellationToken.None);
        var sunday = await Bookkeeping(db).Handle(Filter(Sunday), CancellationToken.None);

        Assert.Equal(3.5m, monday.Sales);
        Assert.Equal(3.5m, monday.CardSales);
        Assert.Equal(1m, monday.CostOfGoods);
        Assert.Equal(1, monday.TransactionCount);
        Assert.Equal(0m, sunday.Sales);
        Assert.Equal(0, sunday.TransactionCount);
    }

    [Fact]
    public async Task The_gst_accounting_aid_counts_it_on_the_Sydney_date()
    {
        await using var connection = await CreateSqliteAsync();
        await using (var seed = Context(connection))
        {
            seed.NayaxSales.Add(Sale(1, JustAfterSydneyMidnightUtc, 3.5m, cost: 1m));
            await seed.SaveChangesAsync();
        }

        await using var db = Context(connection);
        var monday = await Gst(db).Handle(Filter(Monday), CancellationToken.None);
        var sunday = await Gst(db).Handle(Filter(Sunday), CancellationToken.None);

        // The aid reports the GST-inclusive sale split into its two parts, so the $3.50 sale is
        // 3.50/11 of GST on 3.50 - 3.50/11 of taxable sales.
        Assert.Equal(3.5m / 11m, monday.GstOnSales);
        Assert.Equal(3.5m - 3.5m / 11m, monday.TaxableSales);
        Assert.Equal(0m, sunday.TaxableSales);
        Assert.Equal(0m, sunday.GstOnSales);
    }

    [Fact]
    public async Task The_reports_dashboard_counts_it_on_the_Sydney_date()
    {
        await using var connection = await CreateSqliteAsync();
        await using (var seed = Context(connection))
        {
            seed.NayaxSales.Add(Sale(1, JustAfterSydneyMidnightUtc, 3.5m, cost: 1m));
            await seed.SaveChangesAsync();
        }

        await using var db = Context(connection);
        var monday = await Dashboard(db).Handle(Filter(Monday), CancellationToken.None);
        var sunday = await Dashboard(db).Handle(Filter(Sunday), CancellationToken.None);

        Assert.Equal(3.5m, monday.Sales);
        Assert.Equal(1m, monday.CostOfGoodsSold);
        // The distinct machine count is this report's own facts provider rather than bookkeeping's.
        Assert.Equal(1, monday.MachineCount);
        Assert.Equal(0m, sunday.Sales);
        Assert.Equal(0, sunday.MachineCount);
    }

    [Fact]
    public async Task Reconciliation_counts_it_on_the_Sydney_date()
    {
        await using var connection = await CreateSqliteAsync();
        await using (var seed = Context(connection))
        {
            seed.NayaxSales.Add(Sale(1, JustAfterSydneyMidnightUtc, 3.5m, cost: 1m));
            await seed.SaveChangesAsync();
        }

        await using var db = Context(connection);
        var monday = await Reconciliation(db).Handle(Filter(Monday), 0.01m, CancellationToken.None);
        var sunday = await Reconciliation(db).Handle(Filter(Sunday), 0.01m, CancellationToken.None);

        Assert.Equal(3.5m, monday.TotalVendingSales);
        Assert.Equal(3.5m, monday.CardSales);
        Assert.Equal(1, monday.CardTransactionCount);
        Assert.Equal(0m, sunday.TotalVendingSales);
        Assert.Equal(0, sunday.CardTransactionCount);
    }

    /// <summary>
    /// The Nayax processing fee rate a report estimates a sale at is the rate effective on the
    /// sale's <em>business</em> date (issue #1250 acceptance criterion 2). The 00:30 Monday sale is
    /// charged Monday's rate, not the rate that was effective on the UTC date its instant falls on.
    /// </summary>
    [Fact]
    public async Task A_processing_fee_rate_is_resolved_by_the_sales_business_date()
    {
        await using var connection = await CreateSqliteAsync();
        await using (var seed = Context(connection))
        {
            seed.NayaxProcessingFeeRates.AddRange(
                new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2026, 1, 1), FeeExGst = 0.10m },
                new NayaxProcessingFeeRate { EffectiveFrom = Monday, FeeExGst = 0.50m });
            seed.NayaxSales.Add(Sale(1, JustAfterSydneyMidnightUtc, 3.5m, cost: 1m));
            await seed.SaveChangesAsync();
        }

        await using var db = Context(connection);
        var monday = await Bookkeeping(db).Handle(Filter(Monday), CancellationToken.None);

        Assert.Equal(1, monday.NayaxProcessingFees.EstimatedCardTransactionCount);
        Assert.Equal(0.50m, monday.NayaxProcessingFees.EstimatedFeeExGst);
        Assert.Equal(Monday, monday.NayaxProcessingFees.EstimatedFeeFromDate);
    }

    /// <summary>
    /// The site commission these reports publish is calculated over exactly the sales their revenue
    /// counts (issue #1250; the repository owner's blocking review of the commission window on
    /// 14 November 2026). Bookkeeping's <c>SiteCommission</c> is what the Domain profit rule
    /// subtracts to reach direct and net profit, so a commission measured over a different window
    /// than sales makes one report disagree with itself.
    ///
    /// The real <see cref="EfSiteCommissionStore"/> and the real commission use case answer here -
    /// the amount comes out of the same relational query the Commission report uses, so a faked
    /// commission report could not show which sales the window selected. With an active 10%
    /// gross-sales agreement, the $3.50 sale at 00:30 Monday Sydney owes $0.35 on Monday 5 October
    /// and nothing at all on Sunday 4 October.
    /// </summary>
    [Fact]
    public async Task Site_commission_follows_the_sales_business_date_in_bookkeeping_and_the_reports_dashboard()
    {
        await using var connection = await CreateSqliteAsync();
        await SeedTenPercentAgreementAndTheMondaySaleAsync(connection);

        await using var db = Context(connection);
        var monday = await Bookkeeping(db, SiteCommissions(db)).Handle(Filter(Monday), CancellationToken.None);
        var sunday = await Bookkeeping(db, SiteCommissions(db)).Handle(Filter(Sunday), CancellationToken.None);
        // The machine filter is a separate path through the same resolution: it picks the selected
        // machine's own commission row rather than totalling the sites'.
        var mondayForTheMachine = await Bookkeeping(db, SiteCommissions(db)).Handle(
            new ReportingFilterDto(From: Monday, To: Monday, MachineId: MachineId), CancellationToken.None);
        var mondayDashboard = await Dashboard(db, SiteCommissions(db)).Handle(Filter(Monday), CancellationToken.None);
        var sundayDashboard = await Dashboard(db, SiteCommissions(db)).Handle(Filter(Sunday), CancellationToken.None);

        Assert.Equal(0.35m, monday.SiteCommission);
        Assert.Equal(3.5m, monday.Sales);
        Assert.Equal(0m, sunday.SiteCommission);
        Assert.Equal(0m, sunday.Sales);
        Assert.Equal(0.35m, mondayForTheMachine.SiteCommission);
        Assert.Equal(0.35m, mondayDashboard.SiteCommission);
        Assert.Equal(0m, sundayDashboard.SiteCommission);
    }

    /// <summary>
    /// The standalone Commission report is deliberately left on its calendar-date selection until
    /// issue #1252 migrates it, so the same fixture still shows the sale's commission under its
    /// <em>UTC</em> date - $0.35 asked for 4 October and nothing asked for the 5th, the mirror image
    /// of the money reports above. This pins that the fix did not move the report #1252 owns.
    /// </summary>
    [Fact]
    public async Task The_standalone_commission_report_keeps_its_calendar_date_selection()
    {
        await using var connection = await CreateSqliteAsync();
        await SeedTenPercentAgreementAndTheMondaySaleAsync(connection);

        await using var db = Context(connection);
        var commissions = SiteCommissions(db);
        var sunday = await commissions.Handle(Sunday, Sunday, null, CancellationToken.None);
        var monday = await commissions.Handle(Monday, Monday, null, CancellationToken.None);

        Assert.Equal(0.35m, Assert.Single(sunday.Rows).CommissionDue);
        Assert.Equal(0m, Assert.Single(monday.Rows).CommissionDue);
    }

    private static async Task SeedTenPercentAgreementAndTheMondaySaleAsync(SqliteConnection connection)
    {
        await using var seed = Context(connection);
        seed.SiteCommissionAgreements.Add(new SiteCommissionAgreement
        {
            SiteId = SiteId,
            EffectiveFrom = new DateTime(2026, 1, 1),
            EffectiveTo = null,
            CommissionRate = 0.10m,
            Frequency = CommissionFrequency.Monthly,
            Basis = CommissionBasis.GrossSales
        });
        seed.NayaxSales.Add(Sale(1, JustAfterSydneyMidnightUtc, 3.5m, cost: 1m));
        await seed.SaveChangesAsync();
    }

    public static TheoryData<string, DateTime, DateTime, DateTime, int> BusinessDays => new()
    {
        // Sunday 4 October 2026, daylight saving starts at 02:00 AEST: 23 hours.
        { "DST start", new DateTime(2026, 10, 4), Utc(2026, 10, 3, 14), Utc(2026, 10, 4, 13), 23 },
        // Sunday 5 April 2026, daylight saving ends at 03:00 AEDT: 25 hours.
        { "DST end", new DateTime(2026, 4, 5), Utc(2026, 4, 4, 13), Utc(2026, 4, 5, 14), 25 },
    };

    /// <summary>
    /// Both ends of a one-day request on each daylight-saving transition day: the first instant of
    /// the Sydney day is in, the instant before it is not; the last instant before the next Sydney
    /// midnight is in, that midnight is not. The day's expected UTC bounds are literals, so the test
    /// pins the calendar rather than restating it.
    /// </summary>
    [Theory]
    [MemberData(nameof(BusinessDays))]
    public async Task Every_money_report_includes_exactly_the_sales_of_a_daylight_saving_business_day(
        string scenario, DateTime businessDate, DateTime startUtc, DateTime nextStartUtc, int hours)
    {
        Assert.Equal(hours, (nextStartUtc - startUtc).TotalHours);
        Assert.Equal(startUtc, Sydney.StartOfBusinessDayUtc(businessDate));

        await using var connection = await CreateSqliteAsync();
        await using (var seed = Context(connection))
        {
            seed.NayaxSales.AddRange(
                Sale(1, startUtc.AddSeconds(-1), 1m, cost: 0.5m),
                Sale(2, startUtc, 2m, cost: 0.5m),
                Sale(3, nextStartUtc.AddSeconds(-1), 4m, cost: 0.5m),
                Sale(4, nextStartUtc, 8m, cost: 0.5m));
            await seed.SaveChangesAsync();
        }

        await using var db = Context(connection);
        var filter = Filter(businessDate);
        var bookkeeping = await Bookkeeping(db).Handle(filter, CancellationToken.None);
        var gst = await Gst(db).Handle(filter, CancellationToken.None);
        var dashboard = await Dashboard(db).Handle(filter, CancellationToken.None);
        var reconciliation = await Reconciliation(db).Handle(filter, 0.01m, CancellationToken.None);

        Assert.True(6m == bookkeeping.Sales, $"{scenario}: bookkeeping counted {bookkeeping.Sales}.");
        Assert.Equal(2, bookkeeping.TransactionCount);
        Assert.Equal(1m, bookkeeping.CostOfGoods);
        Assert.True(6m - 6m / 11m == gst.TaxableSales,
            $"{scenario}: the GST aid counted {gst.TaxableSales} of taxable sales.");
        Assert.True(6m / 11m == gst.GstOnSales, $"{scenario}: the GST aid counted {gst.GstOnSales} of GST on sales.");
        Assert.True(6m == dashboard.Sales, $"{scenario}: the dashboard counted {dashboard.Sales}.");
        Assert.True(6m == reconciliation.TotalVendingSales,
            $"{scenario}: reconciliation counted {reconciliation.TotalVendingSales}.");
    }

    /// <summary>
    /// The consistency the issue is about: for one requested range, bookkeeping's totals are the sum
    /// of the daily report's rows. The range spans the 23-hour daylight-saving start day, so a
    /// report that selected or bucketed by the UTC date would disagree here.
    /// </summary>
    [Fact]
    public async Task Bookkeeping_totals_equal_the_sum_of_the_daily_report_rows()
    {
        await using var connection = await CreateSqliteAsync();
        await using (var seed = Context(connection))
        {
            seed.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2026, 1, 1), FeeExGst = 0.10m });
            seed.NayaxSales.AddRange(
                // 23:30 Saturday 3 October (AEST) - before the range.
                Sale(1, Utc(2026, 10, 3, 13, 30), 100m, cost: 1m),
                // 00:30 Sunday 4 October (AEST).
                Sale(2, Utc(2026, 10, 3, 14, 30), 2m, cost: 1m),
                // 23:30 Sunday 4 October (AEDT).
                Sale(3, Utc(2026, 10, 4, 12, 30), 3m, cost: 1.5m),
                // 00:30 Monday 5 October (AEDT), paid in cash.
                Sale(4, JustAfterSydneyMidnightUtc, 5m, cost: 2m, paymentMethod: "Cash"),
                // 10:30 Monday 5 October (AEDT).
                Sale(5, Utc(2026, 10, 4, 23, 30), 7m, cost: 3m),
                // 00:30 Tuesday 6 October (AEDT) - after the range.
                Sale(6, Utc(2026, 10, 5, 13, 30), 100m, cost: 1m));
            await seed.SaveChangesAsync();
        }

        await using var db = Context(connection);
        var filter = new ReportingFilterDto(From: Sunday, To: Monday);
        var bookkeeping = await Bookkeeping(db).Handle(filter, CancellationToken.None);
        var daily = await Daily(db).Handle(filter, CancellationToken.None);

        Assert.Equal([Sunday, Monday], daily.Rows.Select(x => x.Date).ToList());
        Assert.Equal(daily.Rows.Sum(x => x.GrossSales), bookkeeping.Sales);
        Assert.Equal(daily.Rows.Sum(x => x.CardSales), bookkeeping.CardSales);
        Assert.Equal(daily.Rows.Sum(x => x.CashSales), bookkeeping.CashSales);
        Assert.Equal(daily.Rows.Sum(x => x.TransactionCount), bookkeeping.TransactionCount);
        Assert.Equal(daily.Rows.Sum(x => x.CostOfGoods), bookkeeping.CostOfGoods);
        Assert.Equal(17m, bookkeeping.Sales);
        Assert.Equal(5m, bookkeeping.CashSales);
    }

    /// <summary>
    /// Reimbursement coverage dates, expense dates and purchase dates are date-only values. They
    /// keep plain calendar-date bounds and are never shifted by the sale instants' timezone
    /// conversion: a reimbursement period and an expense dated on the requested boundary stay on the
    /// day they state.
    /// </summary>
    [Fact]
    public async Task Date_only_reimbursement_expense_and_purchase_filters_are_unchanged()
    {
        await using var connection = await CreateSqliteAsync();
        await using (var seed = Context(connection))
        {
            var file = new ImportedFile { FileName = "oct.xml", FileHash = "oct", ImportedAt = DateTime.UtcNow };
            file.Reimbursements.Add(new ImportedReimbursement
            {
                ReimbursementStartDate = Monday,
                ReimbursementEndDate = Monday,
                Total = 9m
            });
            seed.ImportedFiles.Add(file);
            seed.OperatingExpenses.AddRange(
                new OperatingExpense
                {
                    ExpenseDate = Monday,
                    Category = OperatingExpenseCategory.Insurance,
                    Description = "Monday",
                    AmountExGst = 10m,
                    GstAmount = 1m,
                    TotalAmount = 11m
                },
                new OperatingExpense
                {
                    ExpenseDate = Sunday,
                    Category = OperatingExpenseCategory.Insurance,
                    Description = "Sunday",
                    AmountExGst = 20m,
                    GstAmount = 2m,
                    TotalAmount = 22m
                });
            seed.Receipts.Add(new Purchase { PurchaseDate = Monday, DeliveryCost = 4m, PackageCost = 2m });
            await seed.SaveChangesAsync();
        }

        await using var db = Context(connection);
        var monday = await Bookkeeping(db).Handle(Filter(Monday), CancellationToken.None);
        var sunday = await Bookkeeping(db).Handle(Filter(Sunday), CancellationToken.None);

        Assert.Equal(9m, monday.ActualReimbursement);
        Assert.Equal(11m, monday.StructuredOperatingExpenses);
        Assert.Equal(4m, monday.DeliveryCosts);
        Assert.Equal(2m, monday.PackageCosts);
        Assert.Equal(0m, sunday.ActualReimbursement);
        Assert.Equal(22m, sunday.StructuredOperatingExpenses);
        Assert.Equal(0m, sunday.DeliveryCosts);
    }

    /// <summary>
    /// The CSV/XLSX export of each of these reports shows exactly the figures its screen shows,
    /// because both are built from the same use-case result. Full recorded coverage is seeded so the
    /// export prints the amounts rather than substituting an availability state for them.
    /// </summary>
    [Fact]
    public async Task The_exports_show_the_same_business_date_figures_as_the_reports()
    {
        await using var connection = await CreateSqliteAsync();
        await using (var seed = Context(connection))
        {
            seed.NayaxSales.Add(Sale(1, JustAfterSydneyMidnightUtc, 3.5m, cost: 1m));
            foreach (var source in new[] { ProviderDataSource.SalesSync, ProviderDataSource.SettlementImport })
                seed.ProviderDataCoverages.Add(new ProviderDataCoverage
                {
                    Provider = ExternalProvider.Nayax,
                    Source = source,
                    FromUtc = Utc(2026, 10, 1, 0),
                    ToUtc = Utc(2026, 10, 8, 0),
                    RecordedAtUtc = Utc(2026, 10, 8, 0),
                });
            await seed.SaveChangesAsync();
        }

        await using var db = Context(connection);
        var exports = Exports(db);

        Assert.Equal("3.5", await CellAsync(exports, "bookkeeping", Monday, "GrossSales"));
        Assert.Equal(Invariant(3.5m / 11m), await CellAsync(exports, "gst", Monday, "GstOnSales"));
        Assert.Equal("3.5", await CellAsync(exports, "dashboard", Monday, "Sales"));
        Assert.Equal("3.5", await CellAsync(exports, "reconciliation", Monday, "TotalVendingSales"));
        Assert.Equal("0", await CellAsync(exports, "bookkeeping", Sunday, "GrossSales"));
        Assert.Equal("0", await CellAsync(exports, "dashboard", Sunday, "Sales"));
    }

    private static async Task<string> CellAsync(GetReportExportRows exports, string report, DateTime date, string column)
    {
        var table = await exports.Handle(report, Filter(date), CancellationToken.None);
        var index = table.Rows[0].ToList().IndexOf(column);
        Assert.True(index >= 0, $"The {report} export has no '{column}' column.");
        return table.Rows[1][index];
    }

    private static string Invariant(decimal value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static ReportingFilterDto Filter(DateTime businessDate) => new(From: businessDate, To: businessDate);

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
            MachineID = MachineId,
            ProductId = null,
            SettlementValue = value,
            PaymentMethod = paymentMethod,
            MachineAuthorizationTime = instantUtc,
            TransactionStatusId = status,
            CostOfGoodsSold = cost
        };

    private static IGetSiteCommissionReport EmptyCommissions()
    {
        var mock = new Mock<IGetSiteCommissionReport>();
        mock.Setup(x => x.HandleBusinessPeriod(It.IsAny<SiteCommissionBusinessPeriod>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SiteCommissionBusinessPeriod period, long? _, CancellationToken _) =>
                new SiteCommissionReport(period.FirstBusinessDate, period.LastBusinessDate, []));
        return mock.Object;
    }

    /// <summary>
    /// The real commission use case over the real <see cref="EfSiteCommissionStore"/>, so the
    /// commission amount these reports consume is the one the relational query produces. Only the
    /// Nayax fleet read is faked, because a site is known only from a machine's <c>CustomerID</c>
    /// and nothing stored here can supply it.
    /// </summary>
    private static IGetSiteCommissionReport SiteCommissions(AppDbContext db)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine> { new() { MachineID = MachineId, CustomerID = SiteId } });
        return new GetSiteCommissionReport(nayax.Object, new EfSiteCommissionStore(db), new SiteNameResolver(), Sydney);
    }

    private static GetBookkeepingReport Bookkeeping(AppDbContext db, IGetSiteCommissionReport? commissions = null) =>
        new(new EfBookkeepingReportFactsProvider(
            db, TestFinancialUseCases.ProcessingFees(db, Sydney), commissions ?? EmptyCommissions(), Sydney));

    private static GetDailyReport Daily(AppDbContext db) =>
        new(new EfDailyReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db, Sydney), Sydney));

    private static GetGstAccountingAid Gst(AppDbContext db) =>
        new(Bookkeeping(db), new EfGstReportFactsProvider(db));

    private static GetProductProfitabilityReport Products(AppDbContext db) =>
        new(new EfProductProfitabilityReportFactsProvider(db), new EfProductPurchaseCostFactsProvider(db));

    private static GetDashboardReport Dashboard(AppDbContext db, IGetSiteCommissionReport? commissions = null) =>
        new(Bookkeeping(db, commissions), Products(db),
            new EfDashboardReportFactsProvider(db, commissions ?? EmptyCommissions(), Sydney));

    private static GetReconciliationReport Reconciliation(AppDbContext db) =>
        new(new EfReconciliationReportFactsProvider(db, Sydney));

    private static GetReportExportRows Exports(AppDbContext db) =>
        new(Bookkeeping(db), Daily(db), Reconciliation(db),
            new GetMachineProfitabilityReport(new EfMachineProfitabilityReportFactsProvider(
                db, TestFinancialUseCases.ProcessingFees(db, Sydney), EmptyCommissions())),
            Products(db), Gst(db), Dashboard(db),
            new GetTransactionSalesReport(new EfTransactionSalesReportFactsProvider(db)));

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
