namespace Inventory.Application.Machines;

/// <summary>
/// One machine of the live provider fleet, with this business's own sales and costs resolved onto
/// it.
///
/// <para><paramref name="MachineId"/> is the <em>provider's</em> own machine number, which is what
/// every fleet read, stock read and report filter still speaks.
/// <paramref name="LocalMachineId"/> is this application's own key for the same machine
/// (<c>Machines.Id</c>, issue #740), resolved from the provider number through
/// <c>Inventory.Application.Nayax.INayaxMachineLinkStore</c>, or <c>null</c> when this business has
/// recorded no machine carrying that number - a machine the provider started naming after the
/// <c>AddMachines</c> backfill, for example. It is what a feature that stores a reference to a
/// machine has to record (issue #780 moved the operating expenses onto it), and the two are never
/// interchangeable: a local id equal to a provider number is a coincidence.</para>
///
/// <para><b>The fleet list resolves it from the provider number; the single-machine read is
/// given it.</b> Since issue #741 every <c>api/machines/{id}</c> route takes the local id, so the
/// route already knows it: <see cref="GetMachineDashboard"/> looks nothing up and leaves it
/// <c>null</c>, and the controller answers the id the route was given.</para>
///
/// <para><paramref name="SiteId"/> is the site the provider places the machine at - its
/// <c>CustomerID</c>, the same value the site reads group the fleet by and publish as a site's id -
/// or <c>null</c> when the machine is at no site (issue #980). It is copied from the machine read,
/// never derived, so the dashboard can list each site's machines under that site.</para>
///
/// <para><paramref name="PreviousComparableDayGrossRevenue"/> is Today's own baseline (issue
/// #1177): the completed-sale revenue of
/// <see cref="MachineDashboardWindow.PreviousComparableDay"/>, counted exactly as
/// <paramref name="TodayGrossRevenue"/> is counted. It is <b>revenue only</b> - there is
/// deliberately no matching direct profit, so the period resolves no commission and looks up no
/// processing fee - and it is appended last so every figure that came before it keeps its name,
/// order and value.</para>
///
/// <para><paramref name="PreviousComparableMonthGrossRevenue"/> is Month to date's own baseline
/// (issue #1180), built the same way one month back: the completed-sale revenue of
/// <see cref="MachineDashboardWindow.PreviousComparableMonth"/>, counted exactly as
/// <paramref name="MonthToDateGrossRevenue"/> is counted. It is revenue only for the same reason,
/// and is appended after the day baseline so every figure before it keeps its name, order and
/// value.</para>
///
/// <para><paramref name="PreviousDayGrossRevenue"/> is yesterday's revenue in full (issue #1215):
/// the completed-sale revenue of <see cref="MachineDashboardWindow.PreviousDay"/>, from yesterday's
/// business-local midnight to today's, counted exactly as <paramref name="TodayGrossRevenue"/> is
/// counted. It is the Dashboard's Today baseline; <paramref name="PreviousComparableDayGrossRevenue"/>
/// keeps its same-elapsed-time meaning. Revenue only, and appended last.</para>
/// </summary>
public sealed record MachineSummary(
    long MachineId,
    long? LocalMachineId,
    string? MachineName,
    string? MachineNumber,
    long? ActorId,
    decimal TodayGrossRevenue,
    decimal CurrentWeekGrossRevenue,
    decimal PreviousComparableWeekGrossRevenue,
    decimal LastWeekGrossRevenue,
    decimal MonthToDateGrossRevenue,
    decimal TwoWeeksAgoGrossRevenue,
    decimal? TodayDirectProfit,
    decimal? CurrentWeekDirectProfit,
    decimal? PreviousComparableWeekDirectProfit,
    decimal? LastWeekDirectProfit,
    decimal? MonthToDateDirectProfit,
    decimal? TwoWeeksAgoDirectProfit,
    string? ProfitabilityStatus,
    long? SiteId,
    decimal PreviousComparableDayGrossRevenue,
    decimal PreviousComparableMonthGrossRevenue,
    decimal PreviousDayGrossRevenue);
