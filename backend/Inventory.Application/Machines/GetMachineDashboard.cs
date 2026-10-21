using Inventory.Application.Nayax;
using Inventory.Application.Time;

namespace Inventory.Application.Machines;

/// <summary>
/// The single-machine dashboard use case. Mirrors the former
/// <c>InventoryApi.Services.MachineService.GetById</c> exactly (issue #241), except that its rolling
/// comparison periods come from the business day rather than the host's local
/// clock (issue #310; see <see cref="MachineDashboardWindow"/>).
///
/// <para><b>It resolves no local machine id, deliberately.</b> It reads the provider's fleet by the
/// provider's number. Since issue #741 the <c>api/machines/{id}</c> route takes the local id and
/// resolves it to that number before calling here, so the controller already holds the local id
/// and sets <see cref="MachineSummary.LocalMachineId"/> itself; looking it up again here would be a
/// second answer to the same question. <see cref="ListMachineDashboard"/> resolves it for the fleet
/// list, where only the provider numbers are known.</para>
/// </summary>
public sealed class GetMachineDashboard
{
    private readonly INayaxLynxClient _nayax;
    private readonly IMachineDashboardFactsStore _facts;
    private readonly IClock _clock;
    private readonly IBusinessCalendar _businessCalendar;

    public GetMachineDashboard(
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

    public async Task<MachineSummary?> Handle(long machineId, CancellationToken cancellationToken)
    {
        var machine = await _nayax.GetMachineAsync(machineId, cancellationToken);
        if (machine is null) return null;

        var window = MachineDashboardWindow.Resolve(_clock, _businessCalendar);
        var facts = await _facts.GetFactsAsync(machine.MachineID, machine.CustomerID, window, cancellationToken);
        return MachineDashboardAssembler.Build(machine, facts, localMachineId: null);
    }
}
