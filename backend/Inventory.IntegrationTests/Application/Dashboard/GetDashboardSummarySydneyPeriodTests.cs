using Inventory.Application.Dashboard;
using Inventory.Application.Nayax;
using Inventory.Application.Reorder;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Reporting.Dashboard;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Dashboard;

/// <summary>
/// The home Dashboard sales card over the real <c>Australia/Sydney</c> calendar (issue #459), on a
/// relational SQLite database and with the production <c>SydneyBusinessCalendar</c> rather than the
/// identity <c>FakeBusinessCalendar</c>. These are the boundary cases that a host running in UTC gets
/// wrong: the business week starts at Sydney midnight, not UTC midnight, and across a daylight-saving
/// transition the current and comparable weeks start a different number of hours apart, so the
/// comparable period must be measured from its own week's start and must never reach into the current
/// week and count a sale twice.
/// </summary>
public class GetDashboardSummarySydneyPeriodTests : IDisposable
{
    private const int BusinessId = 1;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public GetDashboardSummarySydneyPeriodTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.Add(new Business { Id = BusinessId, Name = "Vending", CreatedAtUtc = DateTime.UtcNow });
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Seed(params (DateTime AuthorizedUtc, decimal SettlementValue)[] sales)
    {
        using var setup = TestAppDbContext.Unrestricted(_options);
        var transactionId = 1;
        foreach (var sale in sales)
        {
            setup.NayaxSales.Add(new NayaxSales
            {
                BusinessId = BusinessId,
                TransactionID = transactionId++,
                MachineID = 10,
                SettlementValue = sale.SettlementValue,
                PaymentMethod = "Credit",
                MachineAuthorizationTime = sale.AuthorizedUtc,
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
            });
        }

        setup.SaveChanges();
    }

    private async Task<DashboardSummaryDto> SummaryAt(FixedSydneyTime time)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var outstandingOrders = new Mock<IOutstandingSupplierOrderQuantityStore>();
        outstandingOrders.Setup(x => x.GetOutstandingQuantitiesByProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<long, decimal>());

        await using var db = TestAppDbContext.For(_options, BusinessId);
        return await new GetDashboardSummary(
                time.Clock,
                time.Calendar,
                new EfDashboardSummarySalesFactsProvider(db),
                new EfProductCatalogStore(db),
                new CalculateReorderNeeds(nayax.Object, outstandingOrders.Object))
            .Handle(CancellationToken.None);
    }

    /// <summary>
    /// A sale early on Monday morning in Sydney falls on the previous Sunday in UTC. It belongs to the
    /// Sydney business week-to-date, which is what makes this figure the business's week and not the
    /// host timezone's.
    /// </summary>
    [Fact]
    public async Task The_week_to_date_starts_at_Sydney_midnight_not_UTC_midnight()
    {
        // Tuesday 6 October 2026, 11:00 in Sydney.
        var time = new FixedSydneyTime(new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc));
        var window = time.Window;
        var mondayMorningSydney = window.CurrentWeek.StartUtc.AddHours(7);
        Seed(
            (window.CurrentWeek.StartUtc, 5m),
            (mondayMorningSydney, 11m),
            (window.CurrentWeek.StartUtc.AddMilliseconds(-1), 999m));

        var summary = await SummaryAt(time);

        Assert.Equal(DayOfWeek.Sunday, window.CurrentWeek.StartUtc.DayOfWeek);
        Assert.Equal(DayOfWeek.Monday, window.CurrentWeek.FirstBusinessDate.DayOfWeek);
        Assert.Equal(16m, summary.SalesThisWeek.Sales);
        Assert.Equal(2, summary.SalesThisWeek.TransactionCount);
        Assert.Equal(window.CurrentWeek.StartUtc, summary.SalesThisWeek.Period.StartUtc);
        Assert.Equal(time.NowUtc, summary.SalesThisWeek.Period.EndUtc);
    }

    /// <summary>
    /// A partial week is compared against the same elapsed trading time into the previous week, never
    /// against the whole of it: a sale made later in the previous week than the current week has yet
    /// reached is outside the comparison.
    /// </summary>
    [Fact]
    public async Task The_comparison_covers_only_the_same_elapsed_time_into_the_previous_week()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc));
        var window = time.Window;
        Seed(
            (window.CurrentWeek.StartUtc.AddHours(1), 40m),
            (window.PreviousComparableWeek.StartUtc.AddHours(1), 30m),
            (window.PreviousComparableWeek.EndUtc, 20m),
            (window.PreviousComparableWeek.EndUtc.AddMilliseconds(1), 999m),
            (window.PreviousComparableWeek.StartUtc.AddYears(-1), 1m));

        var sales = (await SummaryAt(time)).SalesThisWeek;

        Assert.True(sales.IsComparisonAvailable);
        Assert.Equal(40m, sales.Sales);
        Assert.Equal(50m, sales.ComparisonSales);
        Assert.Equal(2, sales.ComparisonTransactionCount);
        Assert.Equal(-10m, sales.ChangeAmount);
        Assert.Equal(-20m, sales.ChangePercent);
    }

    /// <summary>
    /// The week daylight saving starts is 167 hours long, so the current and previous weeks begin 167
    /// hours apart in UTC. The comparable period's end must be measured from the previous week's own
    /// Sydney Monday midnight; subtracting seven days from the current instant would land an hour out
    /// and silently move sales in or out of the comparison.
    /// </summary>
    [Fact]
    public async Task Across_the_start_of_daylight_saving_the_comparable_period_is_measured_from_its_own_week_start()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc));
        var window = time.Window;
        var naiveSevenDaysEarlier = time.NowUtc.AddDays(-7);
        Seed(
            (window.PreviousComparableWeek.StartUtc.AddYears(-1), 1m),
            (naiveSevenDaysEarlier.AddMinutes(30), 25m));

        var sales = (await SummaryAt(time)).SalesThisWeek;

        Assert.Equal(
            167, (window.CurrentWeek.StartUtc - window.PreviousComparableWeek.StartUtc).TotalHours);
        Assert.True(naiveSevenDaysEarlier < window.PreviousComparableWeek.EndUtc);
        Assert.Equal(25m, sales.ComparisonSales);
    }

    /// <summary>
    /// The week daylight saving ends is 169 hours long, which puts the two week starts 169 hours
    /// apart. Both periods are still anchored to their own Sydney Monday midnight.
    /// </summary>
    [Fact]
    public async Task Across_the_end_of_daylight_saving_both_weeks_are_anchored_to_their_own_week_start()
    {
        // Wednesday 8 April 2026, 10:00 in Sydney - the first week back on AEST.
        var time = new FixedSydneyTime(new DateTime(2026, 4, 8, 0, 0, 0, DateTimeKind.Utc));
        var window = time.Window;
        Seed(
            (window.PreviousComparableWeek.StartUtc.AddYears(-1), 1m),
            (window.CurrentWeek.StartUtc, 12m),
            (window.PreviousComparableWeek.StartUtc, 9m),
            (window.PreviousComparableWeek.StartUtc.AddMilliseconds(-1), 999m));

        var sales = (await SummaryAt(time)).SalesThisWeek;

        Assert.Equal(
            169, (window.CurrentWeek.StartUtc - window.PreviousComparableWeek.StartUtc).TotalHours);
        Assert.Equal(12m, sales.Sales);
        Assert.Equal(9m, sales.ComparisonSales);
    }

    /// <summary>
    /// At the very end of the 169-hour week the elapsed time into the current week exceeds the
    /// previous week's whole length, so the comparable period is held at the previous week's own end.
    /// A sale inside the current week must appear in the week-to-date total and in neither case in the
    /// comparison, or the same sale would be counted on both sides of the comparison.
    /// </summary>
    [Fact]
    public async Task The_comparable_period_never_reaches_into_the_current_week()
    {
        // Sunday 5 April 2026, 23:00 in Sydney: the last hour of a 169-hour business week.
        var time = new FixedSydneyTime(new DateTime(2026, 4, 5, 13, 0, 0, DateTimeKind.Utc));
        var window = time.Window;
        Seed(
            (window.PreviousComparableWeek.StartUtc.AddYears(-1), 1m),
            (window.CurrentWeek.StartUtc, 77m));

        var sales = (await SummaryAt(time)).SalesThisWeek;

        Assert.Equal(
            window.CurrentWeek.StartUtc.AddMilliseconds(-1), sales.ComparisonPeriod.EndUtc);
        Assert.Equal(77m, sales.Sales);
        Assert.Equal(0m, sales.ComparisonSales);
    }

    /// <summary>
    /// A business whose recorded sales begin inside the comparable period has no comparison: the
    /// prior period's part-week total is missing data, and presenting it would read as a collapse in
    /// trade. The week-to-date figure itself stays a known figure.
    /// </summary>
    [Fact]
    public async Task A_business_whose_recorded_sales_start_inside_the_comparable_period_has_no_comparison()
    {
        var time = new FixedSydneyTime(new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc));
        var window = time.Window;
        Seed(
            (window.PreviousComparableWeek.StartUtc.AddMilliseconds(1), 15m),
            (window.CurrentWeek.StartUtc.AddHours(1), 60m));

        var sales = (await SummaryAt(time)).SalesThisWeek;

        Assert.Equal(60m, sales.Sales);
        Assert.False(sales.IsComparisonAvailable);
        Assert.Null(sales.ComparisonSales);
        Assert.Null(sales.ComparisonTransactionCount);
        Assert.Null(sales.ChangePercent);
        Assert.Equal(PeriodRevenueComparisonPolicy.PriorPeriodNotCoveredNote, sales.ComparisonNote);
    }
}
