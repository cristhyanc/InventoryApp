using Inventory.Application.Nayax;
using Inventory.Application.Sites;
using Inventory.Infrastructure.Sites;
using InventoryApi.Tests.Application.Access;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Sites;

/// <summary>
/// The four sales periods a site row publishes (issue #1012): Today, This week, Last week and Month
/// to date, every one of them resolved from the same <c>MachineDashboardWindow</c> the machine rows
/// already use, so no period arithmetic of its own exists here to disagree with theirs.
///
/// <para>Each case runs on a fixed instant paired with the production
/// <c>Australia/Sydney</c> calendar, because every boundary these figures are selected between is a
/// business-day boundary that falls inside a UTC day, and two of them fall on a daylight-saving
/// change. The instants are written out with the Sydney wall time they mean, so a boundary case is
/// chosen rather than inherited from whichever day the suite runs on.</para>
///
/// <para>The completed-sales read is what made this a server change rather than a presentation one:
/// a fixed day count cannot cover a period whose own start moves, so the lookback is the earliest
/// instant any published period starts at, and it is inclusive - a sale at exactly a period's
/// <c>StartUtc</c> belongs to that period, which is the comparison the figures themselves
/// make.</para>
/// </summary>
public class SiteSalesPeriodTests
{
    private const long SiteId = 42;
    private const long OtherSiteId = 43;
    private const long LeftMachineId = 10;
    private const long RightMachineId = 11;
    private const long OtherSiteMachineId = 20;

    /// <summary>
    /// 1 October 2023 is both the first day of a month and the day Sydney moves to AEDT, so the
    /// month-to-date period begins at an AEST (+10) midnight while the instant it is read at is an
    /// AEDT (+11) one. A fixed-offset conversion gets this month's first instant wrong by an hour.
    /// </summary>
    private static readonly FixedSydneyTime LastDayOfOctober2023 =
        new(new DateTime(2023, 10, 31, 5, 0, 0, DateTimeKind.Utc));

    /// <summary>
    /// Thursday 5 October 2023, 10:00 in Sydney. Last week is Monday 25 September to Sunday
    /// 1 October, the week the AEDT transition falls in: it starts at an AEST midnight and ends at
    /// an AEDT one, so it is 167 hours long rather than 168.
    /// </summary>
    private static readonly FixedSydneyTime AfterTheAedtTransition =
        new(new DateTime(2023, 10, 4, 23, 0, 0, DateTimeKind.Utc));

    /// <summary>
    /// Wednesday 4 March 2026, 21:00 in Sydney: early enough in the month that last week and the
    /// previous comparable week both start <em>before</em> the month does, so the month start is not
    /// the earliest instant the read has to cover.
    /// </summary>
    private static readonly FixedSydneyTime EarlyInMarch2026 =
        new(new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc));

    private static readonly SiteProductActivityFact[] ActiveProducts = [new(ProductId: 1, IsActive: true)];

    /// <summary>
    /// A sale on the first of the month is still inside month to date on the thirty-first of it.
    /// The former fixed sixteen-day lookback did not reach it, so the figure would have been short
    /// of every sale before the fifteenth rather than wrong by a visible boundary.
    /// </summary>
    [Fact]
    public async Task Month_to_date_includes_a_sale_on_the_first_read_on_the_thirty_first()
    {
        var facts = Facts(
            // 1 October 2023, 00:00 in Sydney: exactly the instant month to date begins at.
            Sale(LeftMachineId, 7m, Utc(2023, 9, 30, 14, 0)),
            // 30 September 2023, 23:59:59.999 in Sydney: the last instant of the month before.
            Sale(LeftMachineId, 1000m, Utc(2023, 9, 30, 13, 59, 59, 999)));

        var site = Assert.Single(await Summaries(LastDayOfOctober2023, facts));

        Assert.Equal(7m, site.MonthToDateRevenue);
        Assert.Equal(0m, site.LastWeekRevenue);
        Assert.Equal(0m, site.TodayRevenue);
        Assert.Equal(0m, site.CurrentWeekRevenue);
    }

    /// <summary>
    /// A period a caller may see and that holds no completed sale is an honest <c>0</c>, never
    /// <c>null</c>: <c>null</c> is reserved for a figure that was never calculated (issue #697).
    /// </summary>
    [Fact]
    public async Task A_period_with_no_completed_sale_is_zero_rather_than_unavailable()
    {
        var site = Assert.Single(await Summaries(LastDayOfOctober2023, Facts()));

        Assert.Equal(0m, site.TodayRevenue);
        Assert.Equal(0m, site.CurrentWeekRevenue);
        Assert.Equal(0m, site.LastWeekRevenue);
        Assert.Equal(0m, site.MonthToDateRevenue);
        Assert.Equal(0m, site.PreviousComparableWeekRevenue);
    }

    /// <summary>
    /// Last week and month to date across the daylight-saving change, by their own boundaries: the
    /// week starts at an AEST midnight, ends an hour before the following AEDT midnight, and the
    /// month begins in the middle of it. Every boundary instant is on the inside of the period it
    /// bounds, and the instant one millisecond past the week's end belongs to the current week only.
    /// </summary>
    [Fact]
    public async Task Last_week_and_month_to_date_use_their_own_business_day_boundaries_across_a_transition()
    {
        var facts = Facts(
            // Monday 25 September 2023, 00:00 AEST: the first instant of last week.
            Sale(LeftMachineId, 11m, Utc(2023, 9, 24, 14, 0)),
            // Wednesday 27 September 2023, 12:00 AEST: inside last week, before the month began.
            Sale(LeftMachineId, 13m, Utc(2023, 9, 27, 2, 0)),
            // Sunday 1 October 2023, 00:00 AEST: last week and the new month's first instant.
            Sale(LeftMachineId, 17m, Utc(2023, 9, 30, 14, 0)),
            // Sunday 1 October 2023, 23:59:59.999 AEDT: last week's final instant.
            Sale(LeftMachineId, 19m, Utc(2023, 10, 1, 12, 59, 59, 999)),
            // Monday 2 October 2023, 00:00 AEDT: one millisecond later, and the current week.
            Sale(LeftMachineId, 23m, Utc(2023, 10, 1, 13, 0)),
            // Sunday 24 September 2023, 23:59:59.999 AEST: the week before last week.
            Sale(LeftMachineId, 29m, Utc(2023, 9, 24, 13, 59, 59, 999)));

        var site = Assert.Single(await Summaries(AfterTheAedtTransition, facts));

        Assert.Equal(60m, site.LastWeekRevenue);
        Assert.Equal(59m, site.MonthToDateRevenue);
        Assert.Equal(23m, site.CurrentWeekRevenue);
        Assert.Equal(0m, site.TodayRevenue);
    }

    /// <summary>
    /// The read has to cover every period the row publishes from that period's own first instant,
    /// and in the first half of a month that instant is last week's rather than the month's. A
    /// lookback derived from a day count - sixteen days, or any other - happens to cover this one
    /// and not the month-to-date case above, which is why it is derived from the window instead.
    /// </summary>
    [Fact]
    public async Task The_completed_sales_read_starts_at_the_earliest_published_period()
    {
        var facts = Facts(
            // Monday 23 February 2026, 00:00 in Sydney: last week's first instant, which is before
            // the first instant of March.
            Sale(LeftMachineId, 31m, Utc(2026, 2, 22, 13, 0)));

        var site = Assert.Single(await Summaries(EarlyInMarch2026, facts));

        Assert.Equal(31m, site.LastWeekRevenue);
        Assert.Equal(0m, site.MonthToDateRevenue);

        var request = Assert.Single(facts.CompletedSalesRequests);
        Assert.Equal(EarlyInMarch2026.Window.LastWeek.StartUtc, request.FromUtc);
    }

    /// <summary>
    /// A site's figure is its own machines' sales and nothing else: both machines standing at it are
    /// counted once each, and neither the machine at another site nor a machine the fleet read never
    /// returned reaches it. Membership is the machine's current site, exactly as it already is for
    /// Today and This week.
    /// </summary>
    [Fact]
    public async Task A_sites_figure_is_the_sum_of_the_machines_currently_standing_at_it()
    {
        var facts = Facts(
            // Inside last week and inside the month, on each of the two machines at the site.
            Sale(LeftMachineId, 5m, Utc(2023, 9, 27, 2, 0)),
            Sale(RightMachineId, 8m, Utc(2023, 9, 28, 2, 0)),
            // The other site's machine, and a machine standing at no site in this fleet at all.
            Sale(OtherSiteMachineId, 500m, Utc(2023, 9, 27, 2, 0)),
            Sale(machineId: 99, 900m, Utc(2023, 9, 27, 2, 0)));

        var summaries = await Summaries(AfterTheAedtTransition, facts, TwoSiteFleet());
        var site = summaries.Single(summary => summary.SiteId == SiteId);
        var otherSite = summaries.Single(summary => summary.SiteId == OtherSiteId);

        Assert.Equal(13m, site.LastWeekRevenue);
        Assert.Equal(500m, otherSite.LastWeekRevenue);

        // Both machines really are at the one site, so the sum above is two rows and not one.
        Assert.Equal(2, site.MachineCount);
    }

    private static Task<IReadOnlyList<SiteSummary>> Summaries(
        FixedSydneyTime time, ISiteFactsStore facts, RecordingNayaxLynxClient? fleet = null) =>
        new GetSiteSummaries(
                fleet ?? OneSiteFleet(),
                facts,
                FakeNayaxProductLinkStore.EveryIdLinkedToItself(),
                new SiteNameResolver(),
                time.Clock,
                time.Calendar,
                TestCurrentCapabilities.Owner)
            .Handle(CancellationToken.None);

    private static RecordingSiteFactsStore Facts(params SiteCompletedSaleFact[] sales) =>
        new(ActiveProducts, sales);

    private static SiteCompletedSaleFact Sale(long machineId, decimal settlementValue, DateTime authorizedAtUtc) =>
        new(machineId, settlementValue, authorizedAtUtc);

    private static DateTime Utc(int year, int month, int day, int hour, int minute, int second = 0, int millisecond = 0) =>
        new(year, month, day, hour, minute, second, millisecond, DateTimeKind.Utc);

    /// <summary>Two machines standing at one site, each holding one fully stocked selection.</summary>
    private static RecordingNayaxLynxClient OneSiteFleet() =>
        new(
            [
                new NayaxMachine { MachineID = LeftMachineId, CustomerID = SiteId, MachineName = "Lobby Left" },
                new NayaxMachine { MachineID = RightMachineId, CustomerID = SiteId, MachineName = "Lobby Right" },
            ],
            new Dictionary<long, List<NayaxMachineProduct>>
            {
                [LeftMachineId] = [Selection(LeftMachineId)],
                [RightMachineId] = [Selection(RightMachineId)],
            });

    /// <summary>The same fleet with a third machine standing at a second site.</summary>
    private static RecordingNayaxLynxClient TwoSiteFleet() =>
        new(
            [
                new NayaxMachine { MachineID = LeftMachineId, CustomerID = SiteId, MachineName = "Lobby Left" },
                new NayaxMachine { MachineID = RightMachineId, CustomerID = SiteId, MachineName = "Lobby Right" },
                new NayaxMachine { MachineID = OtherSiteMachineId, CustomerID = OtherSiteId, MachineName = "Annex" },
            ],
            new Dictionary<long, List<NayaxMachineProduct>>
            {
                [LeftMachineId] = [Selection(LeftMachineId)],
                [RightMachineId] = [Selection(RightMachineId)],
                [OtherSiteMachineId] = [Selection(OtherSiteMachineId)],
            });

    private static NayaxMachineProduct Selection(long machineId) =>
        new() { MachineID = machineId, NayaxProductID = 1, PAR = 10, MissingStockByMDB = 0, VendOutAlertThreshold = 1 };
}
