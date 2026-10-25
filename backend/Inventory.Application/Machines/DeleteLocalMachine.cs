namespace Inventory.Application.Machines;

/// <summary>
/// Deletes one of the business's own machines, when it may be deleted at all (issue #896).
///
/// <para><strong>Only a machine nothing refers to.</strong> A machine added by mistake is a mistake
/// an operator has to be able to undo; a machine with history is not, because its machine stock
/// counts, stock movements, pick-list refills and operating expenses are inventory and financial
/// history. One is deleted and the other is deactivated, and
/// <c>Inventory.Domain.Machines.MachineDeletionPolicy</c> is the one rule that decides which.
/// Nothing here cascades, nulls or reassigns a reference, and nothing records a compensating
/// movement.</para>
///
/// <para><strong>A machine the provider names is not deleted here</strong> for the same reason it is
/// not renamed or deactivated here: it is managed at the provider, and its stored provider id is
/// what every later lookup resolves that machine's sales through.</para>
///
/// <para>The count and the delete are one operation in the store, which is what makes the refusal
/// reliable while another request is linking a provider machine or recording a refill against the
/// same row.</para>
/// </summary>
public sealed class DeleteLocalMachine
{
    private readonly ILocalMachineStore _store;

    public DeleteLocalMachine(ILocalMachineStore store)
    {
        _store = store;
    }

    public Task<LocalMachineWriteAttempt> Handle(long id, CancellationToken cancellationToken) =>
        _store.DeleteAsync(id, cancellationToken);
}
