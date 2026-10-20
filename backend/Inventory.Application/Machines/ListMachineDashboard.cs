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
///
/// <para>Each machine also carries this business's own machine row for it (issue #780), resolved
/// from the provider's number through <see cref="INayaxMachineLinkStore"/> in one lookup for the
/// whole fleet. That is the id a feature storing a reference to a machine records - the operating
/// expenses do - so the form that offers this list can submit it.</para>
/// </summary>
public sealed class ListMachineDashboard
{
    private readonly INayaxLynxClient _nayax;
    private readonly IMachineDashboardFactsStore _facts;
    private readonly INayaxMachineLinkStore _machineLinks;
    private readonly IClock _clock;
    private readonly IBusinessCalendar _businessCalendar;

    public ListMachineDashboard(
        INayaxLynxClient nayax,
        IMachineDashboardFactsStore facts,
        INayaxMachineLinkStore machineLinks,
        IClock clock,
        IBusinessCalendar businessCalendar)
    {
        _nayax = nayax;
        _facts = facts;
        _machineLinks = machineLinks;
        _clock = clock;
        _businessCalendar = businessCalendar;
    }

    public async Task<IReadOnlyList<MachineSummary>> Handle(CancellationToken cancellationToken)
    {
        var machines = await _nayax.GetMachinesAsync(cancellationToken);

        // One lookup for the whole fleet rather than one per machine, and a number this business
        // has recorded no machine for simply resolves to nothing.
        var localMachineIds = await _machineLinks.GetLocalMachineIdsAsync(
            machines.Select(machine => machine.MachineID).ToList(), cancellationToken);

        // One window serves every machine in the response, so two machines in the same listing can
        // never be aggregated over different periods; the former per-machine clock read could land on
        // either side of a midnight or week boundary mid-request.
        var window = MachineDashboardWindow.Resolve(_clock, _businessCalendar);
        var summaries = new List<MachineSummary>();
        foreach (var machine in machines)
        {
            var facts = await _facts.GetFactsAsync(machine.MachineID, machine.CustomerID, window, cancellationToken);
            summaries.Add(MachineDashboardAssembler.Build(
                machine, facts, localMachineIds.TryGetValue(machine.MachineID, out var localId) ? localId : null));
        }
        return summaries;
    }
}
