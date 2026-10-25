namespace Inventory.Application.Machines;

/// <summary>
/// Takes one of the business's machines out of service, or puts it back (issue #740).
///
/// <para><strong>Deactivation, not deletion.</strong> Stored sales, refills, machine counts and
/// expenses refer to a machine, so a machine that stops trading is history rather than a mistake:
/// there is no delete, and nothing here touches a quantity, a cost or any stored movement.</para>
///
/// <para><strong>A provider machine is not deactivated here.</strong> A machine the business's sales
/// provider names is taken out of service at the provider, so the store refuses its deactivation
/// against the stored row. Reactivation is never refused, so a provider machine deactivated before
/// that refusal existed can be put back in service.</para>
///
/// <para>One use case for both directions, because they are the same write with the opposite value:
/// the two endpoints that call it are what make the intent explicit at the HTTP boundary.</para>
/// </summary>
public sealed class SetLocalMachineActivation
{
    private readonly ILocalMachineStore _store;

    public SetLocalMachineActivation(ILocalMachineStore store)
    {
        _store = store;
    }

    public Task<LocalMachineWriteAttempt> Handle(long id, bool isActive, CancellationToken cancellationToken) =>
        _store.SetActiveAsync(id, isActive, cancellationToken);
}
