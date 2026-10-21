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
    string? ProfitabilityStatus);
