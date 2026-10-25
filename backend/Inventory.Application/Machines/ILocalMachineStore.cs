namespace Inventory.Application.Machines;

/// <summary>
/// Narrow persistence port for the machines a business operates (issue #740), owned by the
/// Application layer.
///
/// <para>Nothing here reads or writes a provider's machine id: it is set by the <c>AddMachines</c>
/// backfill and, since issue #742, by the provider machine review through its own port,
/// <see cref="IProviderMachineLinkStore"/>. This port reports only <em>whether</em> a machine has
/// one, because that is what a caller needs in order to say so.</para>
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
    /// Renames the machine. The read of the stored row and the write are one operation, so a machine
    /// the business's sales provider names - which is managed at the provider and is refused here -
    /// is refused even when it was linked between a caller's read and this write.
    /// </summary>
    Task<LocalMachineWriteAttempt> RenameAsync(
        long id, LocalMachineWriteFields fields, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the machine out of service or puts it back. Idempotent: setting the activation a
    /// machine already has succeeds and changes nothing. Deactivating a machine the business's
    /// sales provider names is refused, decided in the same operation as the write; reactivating
    /// one is not.
    /// </summary>
    Task<LocalMachineWriteAttempt> SetActiveAsync(long id, bool isActive, CancellationToken cancellationToken);
}
