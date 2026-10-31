using Inventory.Application.Machines;
using Inventory.Application.Nayax;
using Inventory.Application.Sites;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Sites;
using InventoryApi.Tests.Application.Access;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The site row and the machine rows under it are read from one table through two adapters, and
/// issue #1012's invariant is that for each of the four published periods the site figure is exactly
/// the sum of the gross revenue of the machines standing at it. This test is the only place both
/// production paths run over the same seeded sales: <see cref="GetSiteSummaries"/> with the real
/// <see cref="EfSiteFactsStore"/> for the site figure, and the real
/// <see cref="EfMachineDashboardFactsStore"/> for each machine's own figure, over one relational
/// SQLite database, one <see cref="MachineDashboardWindow"/> and one <c>Australia/Sydney</c>
/// business calendar.
///
/// <para>Asserting it here rather than in either adapter's own tests is deliberate: the two
/// lookbacks, the two completed-sale filters and the two period comparisons are what could drift
/// apart, and only a test that reads both can notice. The seeded sales deliberately straddle the
/// boundaries where a difference would show - the month's first instant, the week's first and last
/// instants, and the instants just outside each - and include a refunded transaction, which neither
/// figure may count.</para>
/// </summary>
public class SiteRevenueMatchesMachineRevenueTests : IDisposable
{
    private const int BusinessId = 1;
    private const long SiteId = 42;
    private const long LeftMachineId = 10;
    private const long RightMachineId = 11;
    private const long OtherSiteMachineId = 20;
    private const long OtherSiteId = 43;

    /// <summary>
    /// Thursday 5 October 2023, 10:00 in Sydney. Last week is the week the AEDT transition falls in
    /// (Monday 25 September to Sunday 1 October), and month to date begins on the transition day
    /// itself, so each boundary below is one a fixed-offset or fixed-day-count read gets wrong.
    /// </summary>
    private static readonly FixedSydneyTime Time =
        new(new DateTime(2023, 10, 4, 23, 0, 0, DateTimeKind.Utc));

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public SiteRevenueMatchesMachineRevenueTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.Add(new Business
        {
            Id = BusinessId,
            Name = "Vending Co",
            TimeZoneId = FixedSydneyTime.SydneyTimeZoneId,
            Status = Inventory.Domain.Tenancy.BusinessStatus.Active,
            CreatedAtUtc = Time.NowUtc,
        });
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Each_published_period_of_a_site_equals_the_sum_of_the_machines_standing_at_it()
    {
        await SeedSalesAsync();

        var site = (await SiteSummariesAsync()).Single(summary => summary.SiteId == SiteId);
        var left = await MachineFactsAsync(LeftMachineId);
        var right = await MachineFactsAsync(RightMachineId);

        Assert.Equal(left.Today.GrossRevenue + right.Today.GrossRevenue, site.TodayRevenue);
        Assert.Equal(left.CurrentWeek.GrossRevenue + right.CurrentWeek.GrossRevenue, site.CurrentWeekRevenue);
        Assert.Equal(left.LastWeek.GrossRevenue + right.LastWeek.GrossRevenue, site.LastWeekRevenue);
        Assert.Equal(left.MonthToDate.GrossRevenue + right.MonthToDate.GrossRevenue, site.MonthToDateRevenue);

        // The fixtures really do put money in each of the four periods, so the equalities above are
        // not four pairs of zeroes agreeing with each other.
        Assert.Equal(3m, site.TodayRevenue);
        Assert.Equal(26m, site.CurrentWeekRevenue);
        Assert.Equal(60m, site.LastWeekRevenue);
        Assert.Equal(62m, site.MonthToDateRevenue);
    }

    /// <summary>
    /// The machine standing at the other site is in the same table, the same periods and the same
    /// read, and belongs to neither figure above: no machine is counted twice and none is counted at
    /// a site it does not stand at.
    /// </summary>
    [Fact]
    public async Task A_machine_at_another_site_is_counted_at_that_site_only()
    {
        await SeedSalesAsync();

        var summaries = await SiteSummariesAsync();
        var otherSite = summaries.Single(summary => summary.SiteId == OtherSiteId);
        var annex = await MachineFactsAsync(OtherSiteMachineId);

        Assert.Equal(annex.LastWeek.GrossRevenue, otherSite.LastWeekRevenue);
        Assert.Equal(500m, otherSite.LastWeekRevenue);
        Assert.Equal(60m, summaries.Single(summary => summary.SiteId == SiteId).LastWeekRevenue);
    }

    /// <summary>
    /// Both figures read the same completed-sale filter, so a refunded transaction is in neither.
    /// It is seeded inside last week with an amount nothing else uses, so it would be visible in the
    /// sums above if either filter let it through.
    /// </summary>
    [Fact]
    public async Task A_transaction_that_is_not_a_completed_sale_is_in_neither_figure()
    {
        await SeedSalesAsync();

        var site = (await SiteSummariesAsync()).Single(summary => summary.SiteId == SiteId);
        var left = await MachineFactsAsync(LeftMachineId);

        Assert.Equal(60m, site.LastWeekRevenue);
        Assert.Equal(47m, left.LastWeek.GrossRevenue);
    }

    /// <summary>
    /// Sales placed at the boundaries the two reads could disagree about. The left machine carries
    /// the week and month boundary cases; the right machine adds a second machine's worth to the
    /// site so the sum is a real sum.
    /// </summary>
    private async Task SeedSalesAsync()
    {
        await using var seed = TestAppDbContext.Unrestricted(_options);
        seed.NayaxSales.AddRange(
            // Monday 25 September 2023, 00:00 AEST: last week's first instant.
            CompletedSale(1, LeftMachineId, 11m, Utc(2023, 9, 24, 14, 0)),
            // Sunday 1 October 2023, 00:00 AEST: last week, and the new month's first instant.
            CompletedSale(2, LeftMachineId, 17m, Utc(2023, 9, 30, 14, 0)),
            // Sunday 1 October 2023, 23:59:59.999 AEDT: last week's final instant.
            CompletedSale(3, LeftMachineId, 19m, Utc(2023, 10, 1, 12, 59, 59, 999)),
            // Monday 2 October 2023, 00:00 AEDT: one millisecond later, so the current week.
            CompletedSale(4, LeftMachineId, 23m, Utc(2023, 10, 1, 13, 0)),
            // Thursday 5 October 2023, 09:00 AEDT: today.
            CompletedSale(5, LeftMachineId, 3m, Utc(2023, 10, 4, 22, 0)),
            // Sunday 24 September 2023, 23:59:59.999 AEST: the week before last week, which no
            // published period covers.
            CompletedSale(6, LeftMachineId, 29m, Utc(2023, 9, 24, 13, 59, 59, 999)),
            // Refunded, inside last week: a completed-sale filter either figure got wrong would show.
            RefundedTransaction(7, LeftMachineId, 777m, Utc(2023, 9, 27, 2, 0)),
            // The second machine at the same site, inside last week and the month.
            CompletedSale(8, RightMachineId, 13m, Utc(2023, 9, 27, 2, 0)),
            // Another site's machine, inside last week and the month.
            CompletedSale(9, OtherSiteMachineId, 500m, Utc(2023, 9, 27, 2, 0)));
        await seed.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<SiteSummary>> SiteSummariesAsync()
    {
        await using var db = TestAppDbContext.For(_options, BusinessId);
        return await new GetSiteSummaries(
                Fleet(),
                new EfSiteFactsStore(db),
                FakeNayaxProductLinkStore.EveryIdLinkedToItself(),
                new SiteNameResolver(),
                Time.Clock,
                Time.Calendar,
                TestCurrentCapabilities.Owner)
            .Handle(CancellationToken.None);
    }

    private async Task<MachineDashboardFacts> MachineFactsAsync(long machineId)
    {
        await using var db = TestAppDbContext.For(_options, BusinessId);
        return await new EfMachineDashboardFactsStore(db, TestFinancialUseCases.ProcessingFees(db, Time.Calendar))
            .GetFactsAsync(machineId, SiteId, Time.Window, CancellationToken.None);
    }

    private static NayaxSales CompletedSale(
        long transactionId, long machineId, decimal settlementValue, DateTime authorizedAtUtc) =>
        Transaction(transactionId, machineId, settlementValue, authorizedAtUtc, NayaxTransactionStatusIds.Completed);

    private static NayaxSales RefundedTransaction(
        long transactionId, long machineId, decimal settlementValue, DateTime authorizedAtUtc) =>
        Transaction(transactionId, machineId, settlementValue, authorizedAtUtc, NayaxTransactionStatusIds.Refunded);

    private static NayaxSales Transaction(
        long transactionId, long machineId, decimal settlementValue, DateTime authorizedAtUtc, int statusId) =>
        new()
        {
            BusinessId = BusinessId,
            TransactionID = transactionId,
            MachineID = machineId,
            SettlementValue = settlementValue,
            PaymentMethod = "Credit",
            MachineAuthorizationTime = authorizedAtUtc,
            TransactionStatusId = statusId,
        };

    private static DateTime Utc(int year, int month, int day, int hour, int minute, int second = 0, int millisecond = 0) =>
        new(year, month, day, hour, minute, second, millisecond, DateTimeKind.Utc);

    /// <summary>
    /// Two machines at the site under test and one at a second site. The fleet read is mocked
    /// because it is a remote call; everything the figures are made of comes from the database.
    /// </summary>
    private static INayaxLynxClient Fleet()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new NayaxMachine { MachineID = LeftMachineId, CustomerID = SiteId, MachineName = "Lobby Left" },
                new NayaxMachine { MachineID = RightMachineId, CustomerID = SiteId, MachineName = "Lobby Right" },
                new NayaxMachine { MachineID = OtherSiteMachineId, CustomerID = OtherSiteId, MachineName = "Annex" },
            ]);
        nayax.Setup(x => x.GetMachineProductsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return nayax.Object;
    }
}
