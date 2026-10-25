using Inventory.Domain.Machines;

namespace Inventory.Application.Machines;

/// <summary>
/// The machine rename use case (issue #740). The submitted name is validated here; whether the
/// machine exists at all is a statement about stored rows and is decided by the store inside the
/// same operation as the write.
///
/// <para>A machine the business's sales provider names is <em>not</em> renamed here: it is managed
/// at the provider, so the store refuses the rename against the stored row inside the write's own
/// transaction. Issue #740 originally allowed it; that rule was reversed at the owner's
/// request.</para>
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
