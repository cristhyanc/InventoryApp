namespace Inventory.Application.Machines;

/// <summary>One of the business's own machine records (issue #740), or nothing.</summary>
public sealed class GetLocalMachine
{
    private readonly ILocalMachineStore _store;

    public GetLocalMachine(ILocalMachineStore store)
    {
        _store = store;
    }

    public Task<LocalMachineRecord?> Handle(long id, CancellationToken cancellationToken) =>
        _store.FindByIdAsync(id, cancellationToken);
}
