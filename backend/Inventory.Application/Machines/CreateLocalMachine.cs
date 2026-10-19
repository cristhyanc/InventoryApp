using Inventory.Domain.Machines;

namespace Inventory.Application.Machines;

/// <summary>
/// The machine creation use case (issue #740, parent #329 child G1): a business with no sales
/// provider has to be able to record the machines it actually operates.
///
/// <para>It validates the submitted name - the one rule that needs no database - and leaves the
/// insert to the store. The created machine never carries a provider id: a machine a person created
/// is this business's own record, and only provider synchronization or the provider machine review
/// (issue #742) may ever attach one.</para>
/// </summary>
public sealed class CreateLocalMachine
{
    private readonly ILocalMachineStore _store;

    public CreateLocalMachine(ILocalMachineStore store)
    {
        _store = store;
    }

    public Task<LocalMachineWriteAttempt> Handle(
        LocalMachineWriteFields fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var error = MachineCataloguePolicy.Validate(fields.Name);
        return error is not null
            ? Task.FromResult(LocalMachineWriteAttempt.Refused(error))
            : _store.CreateAsync(fields, cancellationToken);
    }
}
