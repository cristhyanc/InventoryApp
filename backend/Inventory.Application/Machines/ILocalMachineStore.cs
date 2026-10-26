namespace Inventory.Application.Machines;

/// <summary>
/// Whether one named machine of the caller's business is in service, and nothing else about it
/// (issue #897). It is the whole answer a write that merely has to refuse a machine it may not
/// record against needs, which is why it is a different thing from <see cref="LocalMachineRecord"/>:
/// that one carries a machine's name, whether the provider names it and - since issue #896 - its
/// deletion eligibility, whose reference counts are several correlated reads a refill has no
/// interest in and must not hold a write lock across.
/// </summary>
public sealed record LocalMachineActivation(long MachineId, bool IsActive);

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
    ///
    /// <para>Each record carries its <see cref="LocalMachineRecord.DeletionEligibility"/> (issue
    /// #896), so the list can offer <strong>Delete</strong> only where <see cref="DeleteAsync"/>
    /// would allow it.</para>
    /// </summary>
    Task<IReadOnlyList<LocalMachineRecord>> ListOrderedByNameAsync(CancellationToken cancellationToken);

    /// <summary>
    /// One machine of the caller's business, with its <see cref="LocalMachineRecord.DeletionEligibility"/>
    /// resolved too, or <see langword="null"/> when the business has no such machine.
    /// </summary>
    Task<LocalMachineRecord?> FindByIdAsync(long id, CancellationToken cancellationToken);

    /// <summary>
    /// Whether each of the named machines is a machine of the caller's business and in service, in
    /// one statement, bounded by the ids the caller asked about rather than by the fleet - the same
    /// shape as <see cref="Inventory.Application.PickList.IPickListStorageStockStore"/>'s storage
    /// read. A machine the business does not have is simply absent from the answer, which is what
    /// the central tenant query filters produce, so a caller can refuse "not an active machine of
    /// this business" without ever learning whether the id exists somewhere else.
    ///
    /// <para>It exists because a read taken inside a write lock must be the read the rule needs and
    /// no more (issue #897): <see cref="ListOrderedByNameAsync"/> and <see cref="FindByIdAsync"/>
    /// also resolve a machine's name, its provider link and its deletion eligibility, and that last
    /// one counts four other tables per machine and may read the business's sales provider (issue
    /// #896). None of it decides a refill, and every other picker waits on the lock it would be read
    /// under.</para>
    /// </summary>
    Task<IReadOnlyDictionary<long, LocalMachineActivation>> GetActivationsAsync(
        IReadOnlyCollection<long> machineIds, CancellationToken cancellationToken);

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

    /// <summary>
    /// Deletes a machine the business added by mistake, when it may be deleted at all (issue #896).
    ///
    /// <para>The authoritative read, the reference count and the delete are <strong>one</strong>
    /// operation, under the write lock the provider machine review's own apply takes: a machine that
    /// gained a provider link, an expense, a count, a movement or a refill after a caller read it is
    /// refused rather than deleted. The refusal is
    /// <c>Inventory.Domain.Machines.MachineDeletionPolicy</c>'s, and the restricted foreign keys are
    /// the database's own backstop behind it - a constraint violation becomes the same refusal, never
    /// an unhandled failure.</para>
    ///
    /// <para>Nothing is cascaded, nulled, reassigned or compensated for: a machine with history is
    /// deactivated instead, which is what keeps every stored count, movement, refill and expense
    /// exactly where it is.</para>
    /// </summary>
    Task<LocalMachineWriteAttempt> DeleteAsync(long id, CancellationToken cancellationToken);
}
