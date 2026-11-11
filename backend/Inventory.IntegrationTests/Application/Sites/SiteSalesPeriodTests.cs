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
/// already use, so no period arithmetic of its own exists here to disagree with theirs - together
/// with their comparison baselines, the previous comparable week, the previous comparable day
/// (issue #1177) and the previous comparable month (issue #1180).
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
        Assert.Equal(0m, site.PreviousComparableDayRevenue);
        Assert.Equal(0m, site.PreviousComparableMonthRevenue);
    }

    /// <summary>
    /// Today's baseline (issue #1177): yesterday up to the same time of day. Read at 10:00 on
    /// Thursday 5 October 2023 in Sydney, it is Wednesday 4 October from that day's own midnight
    /// (3 October 13:00 UTC) to 10:00 (3 October 23:00 UTC) - a period whose first instant falls on
    /// the <em>previous</em> UTC date, which is what a figure derived from a UTC day would get wrong.
    /// Both bounds are inclusive, exactly as every other site figure's are.
    /// </summary>
    [Fact]
    public async Task The_previous_comparable_day_counts_yesterday_up_to_the_same_time_only()
    {
        var facts = Facts(
            // Wednesday 4 October 2023, 00:00 AEDT: the baseline's first instant, on UTC 3 October.
            Sale(LeftMachineId, 7m, Utc(2023, 10, 3, 13, 0)),
            // Wednesday 4 October 2023, 10:00 AEDT: exactly the cutoff, which is inclusive.
            Sale(LeftMachineId, 11m, Utc(2023, 10, 3, 23, 0)),
            // One millisecond later: yesterday, but past the point today has reached.
            Sale(LeftMachineId, 1000m, Utc(2023, 10, 3, 23, 0, 0, 1)),
            // Tuesday 3 October 2023, 23:59:59.999 AEDT: one millisecond before the baseline began.
            Sale(LeftMachineId, 2000m, Utc(2023, 10, 3, 12, 59, 59, 999)),
            // Today, which the baseline it is compared with must never reach into.
            Sale(LeftMachineId, 5m, Utc(2023, 10, 4, 22, 0)));

        var site = Assert.Single(await Summaries(AfterTheAedtTransition, facts));

        Assert.Equal(18m, site.PreviousComparableDayRevenue);
        Assert.Equal(5m, site.TodayRevenue);

        // The excluded sales are in the response's other periods, so they really were read and
        // really were placed - the baseline left them out rather than the fixture.
        Assert.Equal(3023m, site.CurrentWeekRevenue);
    }

    /// <summary>
    /// Membership is the machine's current site for the baseline too: each site's figure is the sum
    /// of its own machines' sales in that period and nothing else.
    /// </summary>
    [Fact]
    public async Task The_previous_comparable_day_is_the_sum_of_the_machines_standing_at_the_site()
    {
        var facts = Facts(
            Sale(LeftMachineId, 4m, Utc(2023, 10, 3, 20, 0)),
            Sale(RightMachineId, 6m, Utc(2023, 10, 3, 21, 0)),
            Sale(OtherSiteMachineId, 500m, Utc(2023, 10, 3, 20, 0)));

        var summaries = await Summaries(AfterTheAedtTransition, facts, TwoSiteFleet());

        Assert.Equal(10m, summaries.Single(summary => summary.SiteId == SiteId).PreviousComparableDayRevenue);
        Assert.Equal(500m, summaries.Single(summary => summary.SiteId == OtherSiteId).PreviousComparableDayRevenue);
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
    /// and since issue #1180 the earliest of them is always the previous comparable month's: the
    /// previous calendar month begins before this month, before this week and before last week on
    /// every date. A lookback derived from a day count could not cover it at all - the distance back
    /// to the first of the previous month is between 28 and 62 days depending on the date - which is
    /// why it is derived from the window.
    /// </summary>
    [Fact]
    public async Task The_completed_sales_read_starts_at_the_earliest_published_period()
    {
        var facts = Facts(
            // Monday 23 February 2026, 00:00 in Sydney: last week's first instant, which is before
            // the first instant of March.
            Sale(LeftMachineId, 31m, Utc(2026, 2, 22, 13, 0)),
            // Sunday 1 February 2026, 00:00 in Sydney: the previous comparable month's own first
            // instant, and the earliest instant the read now has to reach.
            Sale(LeftMachineId, 13m, Utc(2026, 1, 31, 13, 0)));

        var site = Assert.Single(await Summaries(EarlyInMarch2026, facts));

        Assert.Equal(31m, site.LastWeekRevenue);
        Assert.Equal(0m, site.MonthToDateRevenue);
        // The baseline runs to 21:00 on Wednesday 4 February, so it holds the sale on the first of
        // the month and not the one on the twenty-third - which is itself proof the sale was read.
        Assert.Equal(13m, site.PreviousComparableMonthRevenue);

        var request = Assert.Single(facts.CompletedSalesRequests);
        Assert.Equal(EarlyInMarch2026.Window.PreviousComparableMonth.StartUtc, request.FromUtc);
        Assert.True(request.FromUtc < EarlyInMarch2026.Window.LastWeek.StartUtc);
    }

    /// <summary>
    /// Last month's baseline (issue #1180): the previous calendar month up to the same point of the
    /// month, by the comparable day's own business-local clock. Read at 21:00 on Wednesday 4 March
    /// 2026 in Sydney, it is February from 1 February's own midnight (31 January 13:00 UTC) to 21:00
    /// on Wednesday 4 February (4 February 10:00 UTC). Both bounds are inclusive, exactly as every
    /// other site figure's are.
    /// </summary>
    [Fact]
    public async Task The_previous_comparable_month_counts_last_month_up_to_the_same_point_only()
    {
        var facts = Facts(
            // Sunday 1 February 2026, 00:00 in Sydney: the baseline's first instant, on UTC
            // 31 January.
            Sale(LeftMachineId, 7m, Utc(2026, 1, 31, 13, 0)),
            // Wednesday 4 February 2026, 21:00 in Sydney: exactly the cutoff, which is inclusive.
            Sale(LeftMachineId, 11m, Utc(2026, 2, 4, 10, 0)),
            // One millisecond later: last month, but past the point this month has reached.
            Sale(LeftMachineId, 1000m, Utc(2026, 2, 4, 10, 0, 0, 1)),
            // Saturday 31 January 2026, 23:59:59.999 in Sydney: one millisecond before February.
            Sale(LeftMachineId, 2000m, Utc(2026, 1, 31, 12, 59, 59, 999)),
            // This month, which the baseline it is compared with must never reach into.
            Sale(LeftMachineId, 5m, Utc(2026, 3, 3, 10, 0)));

        var site = Assert.Single(await Summaries(EarlyInMarch2026, facts));

        Assert.Equal(18m, site.PreviousComparableMonthRevenue);
        Assert.Equal(5m, site.MonthToDateRevenue);
    }

    /// <summary>
    /// Membership is the machine's current site for the month baseline too: each site's figure is the
    /// sum of its own machines' sales in that period and nothing else.
    /// </summary>
    [Fact]
    public async Task The_previous_comparable_month_is_the_sum_of_the_machines_standing_at_the_site()
    {
        var facts = Facts(
            Sale(LeftMachineId, 4m, Utc(2026, 2, 2, 10, 0)),
            Sale(RightMachineId, 6m, Utc(2026, 2, 3, 10, 0)),
            Sale(OtherSiteMachineId, 500m, Utc(2026, 2, 2, 10, 0)));

        var summaries = await Summaries(EarlyInMarch2026, facts, TwoSiteFleet());

        Assert.Equal(10m, summaries.Single(summary => summary.SiteId == SiteId).PreviousComparableMonthRevenue);
        Assert.Equal(500m, summaries.Single(summary => summary.SiteId == OtherSiteId).PreviousComparableMonthRevenue);
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
