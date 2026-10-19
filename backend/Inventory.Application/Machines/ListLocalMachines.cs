namespace Inventory.Application.Machines;

/// <summary>
/// The business's own machine list (issue #740): every machine it has recorded, active and inactive,
/// with no provider involved at all.
/// </summary>
public sealed class ListLocalMachines
{
    private readonly ILocalMachineStore _store;

    public ListLocalMachines(ILocalMachineStore store)
    {
        _store = store;
    }

    public Task<IReadOnlyList<LocalMachineRecord>> Handle(CancellationToken cancellationToken) =>
        _store.ListOrderedByNameAsync(cancellationToken);
}
