using Inventory.Application.Machines;
using Inventory.Application.Nayax;
using Inventory.Domain.Machines;
using InventoryApi.Tests.Application.Time;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Machines;

/// <summary>
/// Issue #310: the machine dashboard use cases decide the reference window themselves, from the
/// <c>Australia/Sydney</c> business day through the <c>IClock</c>/<c>IBusinessCalendar</c> ports, and
/// hand it to <see cref="IMachineDashboardFactsStore"/> as UTC instants. The facts adapter must not be
/// left to infer "today" from the host's timezone, which on a UTC App Service host is not the business
/// day the dashboard reports.
/// </summary>
public class MachineDashboardWindowResolutionTests
{
    /// <summary>14:30 UTC on Wednesday 11 March 2026 is 01:30 on Thursday 12 March in Sydney.</summary>
    private static readonly FixedSydneyTime Time =
        new(new DateTime(2026, 3, 11, 14, 30, 0, DateTimeKind.Utc));

    [Fact]
    public async Task GetMachineDashboard_PassesTheSydneyBusinessDayWindowToTheFactsStore()
    {
        var facts = new RecordingMachineDashboardFactsStore();
        var useCase = new GetMachineDashboard(Nayax(10), facts, Time.Clock, Time.Calendar);

        var summary = await useCase.Handle(10, CancellationToken.None);

        Assert.NotNull(summary);
        var window = Assert.Single(facts.Windows);
        Assert.Equal(new DateTime(2026, 3, 12), window.BusinessToday);
        Assert.Equal(Time.NowUtc, window.NowUtc);
        Assert.Equal(new DateTime(2026, 3, 11, 13, 0, 0, DateTimeKind.Utc), window.Today.StartUtc);
        Assert.NotEqual(Time.NowUtc.Date, window.BusinessToday);
    }

    [Fact]
    public async Task GetMachineDashboard_ReadsNoFactsForAnUnknownMachine()
    {
        var facts = new RecordingMachineDashboardFactsStore();
        var useCase = new GetMachineDashboard(Nayax(10), facts, Time.Clock, Time.Calendar);

        Assert.Null(await useCase.Handle(404, CancellationToken.None));
        Assert.Empty(facts.Windows);
    }

    /// <summary>
    /// Every machine in one listing is aggregated over exactly the same window, so two machines in the
    /// same response can never be measured over different periods.
    /// </summary>
    [Fact]
    public async Task ListMachineDashboard_UsesOneWindowForEveryMachine()
    {
        var facts = new RecordingMachineDashboardFactsStore();
        var useCase = new ListMachineDashboard(Nayax(10, 11, 12), facts, Time.Clock, Time.Calendar);

        var summaries = await useCase.Handle(CancellationToken.None);

        Assert.Equal(3, summaries.Count);
        Assert.Equal(3, facts.Windows.Count);
        Assert.All(facts.Windows, window => Assert.Equal(facts.Windows[0], window));
        Assert.Equal(new DateTime(2026, 3, 12), facts.Windows[0].BusinessToday);
    }

    private static INayaxLynxClient Nayax(params long[] machineIds)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(client => client.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(machineIds.Select(Machine).ToList());
        nayax.Setup(client => client.GetMachineAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long machineId, CancellationToken _) =>
                machineIds.Contains(machineId) ? Machine(machineId) : null!);
        return nayax.Object;
    }

    private static NayaxMachine Machine(long machineId) =>
        new() { MachineID = machineId, CustomerID = 91 };

    /// <summary>Records the window each dashboard read was given and returns empty, costed facts.</summary>
    private sealed class RecordingMachineDashboardFactsStore : IMachineDashboardFactsStore
    {
        public List<MachineDashboardWindow> Windows { get; } = [];

        public Task<MachineDashboardFacts> GetFactsAsync(
            long machineId, long? siteId, MachineDashboardWindow window, CancellationToken cancellationToken)
        {
            Windows.Add(window);

            var period = new MachineDashboardPeriodFacts(
                0m, new MachineDashboardDirectProfitInputs(false, 0m, false, 0m));
            return Task.FromResult(new MachineDashboardFacts(
                period, period, period, period, period, period,
                new MachineProfitabilityStatusInputs(false, false, true, false)));
        }
    }

}
