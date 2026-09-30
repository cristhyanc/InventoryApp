using Inventory.Application.Nayax;

namespace Inventory.Application.Machines;

/// <summary>
/// The single-machine dashboard use case. Mirrors the former
/// <c>InventoryApi.Services.MachineService.GetById</c> exactly (issue #241).
/// </summary>
public sealed class GetMachineDashboard
{
    private readonly INayaxLynxClient _nayax;
    private readonly IMachineDashboardFactsStore _facts;

    public GetMachineDashboard(INayaxLynxClient nayax, IMachineDashboardFactsStore facts)
    {
        _nayax = nayax;
        _facts = facts;
    }

    public async Task<MachineSummary?> Handle(long machineId, CancellationToken cancellationToken)
    {
        var machine = await _nayax.GetMachineAsync(machineId, cancellationToken);
        if (machine is null) return null;

        var now = DateTime.Now;
        var facts = await _facts.GetFactsAsync(machine.MachineID, machine.CustomerID, now, cancellationToken);
        return MachineDashboardAssembler.Build(machine, facts);
    }
}
