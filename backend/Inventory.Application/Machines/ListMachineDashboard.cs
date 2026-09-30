using Inventory.Application.Nayax;

namespace Inventory.Application.Machines;

/// <summary>
/// The machine dashboard listing use case. Mirrors the former
/// <c>InventoryApi.Services.MachineService.GetAll</c> exactly (issue #241): machines calculate from
/// whatever <c>NayaxSales</c> rows are already persisted and never trigger a fresh Nayax sales import
/// as a side effect (issue #187).
/// </summary>
public sealed class ListMachineDashboard
{
    private readonly INayaxLynxClient _nayax;
    private readonly IMachineDashboardFactsStore _facts;

    public ListMachineDashboard(INayaxLynxClient nayax, IMachineDashboardFactsStore facts)
    {
        _nayax = nayax;
        _facts = facts;
    }

    public async Task<IReadOnlyList<MachineSummary>> Handle(CancellationToken cancellationToken)
    {
        var machines = await _nayax.GetMachinesAsync(cancellationToken);
        var summaries = new List<MachineSummary>();
        foreach (var machine in machines)
        {
            var now = DateTime.Now;
            var facts = await _facts.GetFactsAsync(machine.MachineID, machine.CustomerID, now, cancellationToken);
            summaries.Add(MachineDashboardAssembler.Build(machine, facts));
        }
        return summaries;
    }
}
