using Inventory.Application.Nayax;
using Inventory.Application.Time;

namespace Inventory.Application.Machines;

/// <summary>
/// The single-machine dashboard use case. Mirrors the former
/// <c>InventoryApi.Services.MachineService.GetById</c> exactly (issue #241), except that its rolling
/// comparison periods come from the business day rather than the host's local
/// clock (issue #310; see <see cref="MachineDashboardWindow"/>).
///
/// <para>It resolves this business's own machine row for the machine the same way
/// <see cref="ListMachineDashboard"/> does (issue #780). The two reads share one wire shape, so the
/// alternative would have been publishing a <c>null</c> here that says "this business has recorded
/// no machine with that number" when it has.</para>
/// </summary>
public sealed class GetMachineDashboard
{
    private readonly INayaxLynxClient _nayax;
    private readonly IMachineDashboardFactsStore _facts;
    private readonly INayaxMachineLinkStore _machineLinks;
    private readonly IClock _clock;
    private readonly IBusinessCalendar _businessCalendar;

    public GetMachineDashboard(
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

    public async Task<MachineSummary?> Handle(long machineId, CancellationToken cancellationToken)
    {
        var machine = await _nayax.GetMachineAsync(machineId, cancellationToken);
        if (machine is null) return null;

        var localMachineIds = await _machineLinks.GetLocalMachineIdsAsync(
            [machine.MachineID], cancellationToken);
        var window = MachineDashboardWindow.Resolve(_clock, _businessCalendar);
        var facts = await _facts.GetFactsAsync(machine.MachineID, machine.CustomerID, window, cancellationToken);
        return MachineDashboardAssembler.Build(
            machine, facts, localMachineIds.TryGetValue(machine.MachineID, out var localId) ? localId : null);
    }
}
