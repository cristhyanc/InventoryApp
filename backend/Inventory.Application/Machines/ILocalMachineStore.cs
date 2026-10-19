namespace Inventory.Application.Machines;

/// <summary>
/// Narrow persistence port for the machines a business operates (issue #740), owned by the
/// Application layer.
///
/// <para>Nothing here reads or writes a provider's machine id: it is set by the <c>AddMachines</c>
/// backfill and, later, by provider synchronization and the provider machine review (issue #742).
/// This port reports only <em>whether</em> a machine has one, because that is what a caller needs in
/// order to say so.</para>
/// </summary>
public interface ILocalMachineStore
{
    /// <summary>
    /// Every machine of the caller's business, active and inactive alike, ordered by name. A
    /// deactivated machine stays listed: it is history an operator may want to reactivate, not a
    /// row to hide.
    /// </summary>
    Task<IReadOnlyList<LocalMachineRecord>> ListOrderedByNameAsync(CancellationToken cancellationToken);

    Task<LocalMachineRecord?> FindByIdAsync(long id, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a machine with the submitted name, active, and with no provider id at all - a machine
    /// a person created is this business's own, whatever provider it may later be matched to.
    /// </summary>
    Task<LocalMachineWriteAttempt> CreateAsync(
        LocalMachineWriteFields fields, CancellationToken cancellationToken);

    /// <summary>
    /// Renames the machine. The read of the stored row and the write are one operation, and a
    /// provider-named machine is renamed too: unlike a product's or a category's name, a machine's
    /// name is the operator's own label - no import overwrites it, because nothing syncs it.
    /// </summary>
    Task<LocalMachineWriteAttempt> RenameAsync(
        long id, LocalMachineWriteFields fields, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the machine out of service or puts it back. Idempotent: setting the activation a
    /// machine already has succeeds and changes nothing.
    /// </summary>
    Task<LocalMachineWriteAttempt> SetActiveAsync(long id, bool isActive, CancellationToken cancellationToken);
}
