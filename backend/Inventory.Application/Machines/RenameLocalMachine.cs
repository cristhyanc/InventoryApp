using Inventory.Domain.Machines;

namespace Inventory.Application.Machines;

/// <summary>
/// The machine rename use case (issue #740). The submitted name is validated here; whether the
/// machine exists at all is a statement about stored rows and is decided by the store inside the
/// same operation as the write.
///
/// <para>A provider-named machine is renamed like any other, unlike a product's or a category's
/// name (issue #736): nothing synchronises a machine's name, so a local rename cannot be overwritten
/// by an import and the operator's own label is the only one there is.</para>
/// </summary>
public sealed class RenameLocalMachine
{
    private readonly ILocalMachineStore _store;

    public RenameLocalMachine(ILocalMachineStore store)
    {
        _store = store;
    }

    public Task<LocalMachineWriteAttempt> Handle(
        long id, LocalMachineWriteFields fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var error = MachineCataloguePolicy.Validate(fields.Name);
        return error is not null
            ? Task.FromResult(LocalMachineWriteAttempt.Refused(error))
            : _store.RenameAsync(id, fields, cancellationToken);
    }
}
