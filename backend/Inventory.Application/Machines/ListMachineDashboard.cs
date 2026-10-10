using Inventory.Application.Nayax;
using Inventory.Application.Time;

namespace Inventory.Application.Machines;

/// <summary>
/// The machine dashboard listing use case. Mirrors the former
/// <c>InventoryApi.Services.MachineService.GetAll</c> exactly (issue #241): machines calculate from
/// whatever <c>NayaxSales</c> rows are already persisted and never trigger a fresh Nayax sales import
/// as a side effect (issue #187). Its rolling comparison periods come from the
/// business day rather than the host's local clock (issue #310; see
/// <see cref="MachineDashboardWindow"/>).
/// </summary>
public sealed class ListMachineDashboard
{
    private readonly INayaxLynxClient _nayax;
    private readonly IMachineDashboardFactsStore _facts;
    private readonly IClock _clock;
    private readonly IBusinessCalendar _businessCalendar;

    public ListMachineDashboard(
        INayaxLynxClient nayax,
        IMachineDashboardFactsStore facts,
        IClock clock,
        IBusinessCalendar businessCalendar)
    {
        _nayax = nayax;
        _facts = facts;
        _clock = clock;
        _businessCalendar = businessCalendar;
    }

    public async Task<IReadOnlyList<MachineSummary>> Handle(CancellationToken cancellationToken)
    {
        var machines = await _nayax.GetMachinesAsync(cancellationToken);

        // One window serves every machine in the response, so two machines in the same listing can
        // never be aggregated over different periods; the former per-machine clock read could land on
        // either side of a midnight or week boundary mid-request.
        var window = MachineDashboardWindow.Resolve(_clock, _businessCalendar);
        var summaries = new List<MachineSummary>();
        foreach (var machine in machines)
        {
            var facts = await _facts.GetFactsAsync(machine.MachineID, machine.CustomerID, window, cancellationToken);
            summaries.Add(MachineDashboardAssembler.Build(machine, facts));
        }
        return summaries;
    }
}
