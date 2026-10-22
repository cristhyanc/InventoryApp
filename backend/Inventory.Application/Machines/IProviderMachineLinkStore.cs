using Inventory.Domain.Machines;

namespace Inventory.Application.Machines;

/// <summary>
/// The database transaction one provider machine linking operation runs its authoritative read and
/// its write in (issue #742), the same shape as <c>ICatalogueReviewTransaction</c> (issue #738).
/// Disposing it without <see cref="CommitAsync"/> rolls back everything the operation staged, which
/// is what makes an apply atomic: one refused link leaves no linked machine and no created one
/// behind.
/// </summary>
public interface IProviderMachineLinkTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One machine of the business, as the linking feature reads it. It is the one read in the
/// application that returns <c>LocalMachine.ExternalMachineId</c> itself rather than whether one is
/// present: this is the feature that decides what that column should say, and it has to know which
/// machines carry nothing.
/// </summary>
public sealed record LocalMachineLinkRow(
    long MachineId, string Name, bool IsActive, string? ExternalMachineId);

/// <summary>
/// One link to record: the provider machine's id as the opaque text the column stores, the name a
/// <em>created</em> machine takes, and the machine of the business to link when the operator named
/// one.
///
/// <para><paramref name="Name"/> is deliberately ignored for
/// <see cref="ProviderMachineLinkAction.LinkExisting"/>: a machine's name is the operator's own label
/// and nothing synchronises it (issue #740), so linking records the provider's id and leaves the name
/// exactly as it is.</para>
/// </summary>
public sealed record ProviderMachineLinkCommand(
    string ExternalMachineId,
    string Name,
    ProviderMachineLinkAction Action,
    long? LocalMachineId);

/// <summary>
/// One link the store recorded, keyed by the provider id as stored - opaque text, exactly as the
/// column holds it. The caller turns it back into the provider's own machine number, because the
/// caller is the one that read the fleet and so the one that knows which provider issued it.
/// </summary>
public sealed record ProviderMachineLinkWritten(
    string ExternalMachineId, long LocalMachineId, string Name, bool Created);

/// <summary>
/// Narrow persistence port for provider machine linking (issue #742, parent #329 child G3), owned by
/// the Application layer and implemented by
/// <c>Inventory.Infrastructure.Persistence.EfProviderMachineLinkStore</c>.
///
/// <para>Reads and writes are scoped to the caller's business by the central <c>AppDbContext</c>
/// query filters and <c>BusinessOwnershipEnforcer</c> alone, never by a predicate of its own, and
/// every one of them refuses outright unless the business's sales provider is Nayax - the
/// <c>ExternalMachineId</c> column holds <em>that</em> provider's ids and nothing in it says who
/// issued them (issue #759).</para>
///
/// <para>It deliberately does not reuse <see cref="ILocalMachineStore"/>. That port is the machine
/// records an operator maintains by hand and never reads or writes a provider id at all, which is the
/// invariant that makes a machine's own name safe from an import. This one exists to write exactly
/// that column, on a link somebody decided about, and has to refuse a link the stored rows
/// conflict with.</para>
/// </summary>
public interface IProviderMachineLinkStore
{
    /// <summary>
    /// Begins the transaction the authoritative read and the write share. It is opened after the
    /// provider has been read, so no provider request is ever made while a database transaction is
    /// held open.
    /// </summary>
    Task<IProviderMachineLinkTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Every machine of the caller's business, active and inactive alike, ordered by name - with the
    /// provider id each one carries, if any.
    /// </summary>
    Task<IReadOnlyList<LocalMachineLinkRow>> ListLocalMachinesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records <paramref name="commands"/> against the machines as they are <em>now</em>, inside the
    /// caller's transaction: it re-reads every machine it is about to write, applies
    /// <see cref="ProviderMachineLinkPolicy.LinkRefusal"/> to each link, writes the links and creates
    /// the machines, and answers what it wrote with the keys the database assigned.
    ///
    /// <para>The read, the comparison and the write are one operation, deliberately: a machine that
    /// gained a provider id between the review and the apply must refuse the apply rather than have a
    /// plan nobody approved overwrite it.</para>
    ///
    /// <para>It touches no sale, no stock movement and no machine count. A link says which machine a
    /// provider number means from now on; the provider's own stored data keeps naming the provider's
    /// number exactly as before (issue #741).</para>
    /// </summary>
    /// <exception cref="Inventory.Domain.Exceptions.DomainValidationException">
    /// A link the stored machines refuse. Nothing is staged, so the caller's transaction rolls back
    /// clean.
    /// </exception>
    Task<IReadOnlyList<ProviderMachineLinkWritten>> LinkAsync(
        IReadOnlyList<ProviderMachineLinkCommand> commands, CancellationToken cancellationToken);
}
