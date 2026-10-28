namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// One piece of work about to be issued (issue #924): a supplier, and the product to search for it
/// at, named in exactly one of the two ways a product can be named.
/// </summary>
/// <param name="SupplierPresetId">The supplier master record to search.</param>
/// <param name="MasterProductId">
/// The master product to search for, or <see langword="null"/> when a waiting product is being
/// searched for by name instead.
/// </param>
/// <param name="WaitingKey">
/// The waiting key to search for, or <see langword="null"/> once a master product is named.
/// </param>
public sealed record NewPriceCollectorWorkItem(
    int SupplierPresetId,
    long? MasterProductId,
    string? WaitingKey);

/// <summary>
/// A collector run about to be issued, with all of its work (issue #924).
/// </summary>
/// <param name="IssuedAtUtc">When the run is being issued, from the application clock.</param>
/// <param name="ExpiresAtUtc">
/// When it stops being open unless the collector reports back -
/// <c>PriceCollectorRunPolicy.ExpiresAt</c> of the instant above and the deployment's configured
/// lifetime.
/// </param>
/// <param name="WorkItems">
/// Its work, in the order the work list publishes it. Every item is stored, and every stored item
/// comes back with the id a result must cite.
/// </param>
public sealed record NewPriceCollectorRun(
    DateTime IssuedAtUtc,
    DateTime ExpiresAtUtc,
    IReadOnlyList<NewPriceCollectorWorkItem> WorkItems);

/// <summary>One stored piece of work, with the id the collector must cite for it (issue #924).</summary>
public sealed record IssuedPriceCollectorWorkItem(
    long Id,
    int SupplierPresetId,
    long? MasterProductId,
    string? WaitingKey);

/// <summary>
/// The stored run and its stored work (issue #924). The run id is what the collector reports under,
/// and every work item id is what one result row references.
/// </summary>
public sealed record IssuedPriceCollectorRun(
    long RunId,
    DateTime IssuedAtUtc,
    DateTime ExpiresAtUtc,
    IReadOnlyList<IssuedPriceCollectorWorkItem> WorkItems);

/// <summary>
/// Where the most recently issued run stopped (issue #924): the product its last work item was for,
/// named in exactly one of the two ways a product can be named, and <see langword="null"/> as a whole
/// while no run has ever been issued.
///
/// <para><strong>It is the continuation of a bounded run, and it is read rather than stored.</strong>
/// A run's work items are written in the order the work list publishes them, so the last one is the
/// product that run stopped at - there is no separate cursor column to keep in step with the work it
/// describes, and nothing to migrate. The next run starts at the product after this one
/// (<c>PriceCollectorWorkListPolicy.MatchedProductComesAfter</c>), which is what makes a catalogue
/// larger than one run's budget get searched in full over successive runs instead of the same prefix
/// for ever.</para>
/// </summary>
/// <param name="MasterProductId">
/// The master product that run stopped at, or <see langword="null"/> when it stopped at a waiting
/// product.
/// </param>
/// <param name="WaitingKey">The waiting key it stopped at, on the same terms.</param>
public sealed record PriceCollectorWorkCursor(long? MasterProductId, string? WaitingKey);

/// <summary>
/// The narrow write port the collector work list is issued through (issue #924, parent #817): one
/// run, its work items, and the expiry of whatever run was still open.
///
/// <para><strong>Two platform tables and nothing else.</strong> <c>PriceCollectorRuns</c> and
/// <c>PriceCollectorWorkItems</c> are about no business, so this port takes no business, no
/// predicate, no column list, no page and no statement, and a request that reaches it carries a
/// <em>denied</em> <c>BusinessScope</c> - which is exactly why the two tables cannot be tenant-owned
/// (<c>BusinessOwnershipEnforcer</c> would have no business to stamp). Nothing tenant-owned is
/// readable or writable through it.</para>
///
/// <para><strong>Issuing a run and expiring the previous one are one operation.</strong> They happen
/// in one transaction, so there is never a moment with two open runs and never a moment with none
/// because the expiry committed and the new run did not. "At most one open run" is the invariant the
/// results endpoint of #937 relies on in order to know which work a collector is reporting against.
/// </para>
///
/// <para><strong>Expiring removes nothing.</strong> An expired run keeps its own work items and their
/// outcomes, and the listings it never reached keep their last accepted observation - the owner's
/// constraint of 27 October 2026, which this port is not an exception to.</para>
/// </summary>
public interface IPriceCollectorRunStore
{
    /// <summary>
    /// Expires every run that is still open and stores <paramref name="run"/> with its work, in one
    /// transaction, and answers the stored ids.
    /// </summary>
    Task<IssuedPriceCollectorRun> IssueAsync(
        NewPriceCollectorRun run,
        CancellationToken cancellationToken);

    /// <summary>
    /// Where the most recently issued run stopped, or <see langword="null"/> while none ever has
    /// been - the last work item of the newest run, which is the product that run was issued up to.
    ///
    /// <para>One row, read rather than counted: it is the next run's starting point in the product
    /// rotation, and it says nothing about any business because a work item names a product
    /// description and a supplier's shelf.</para>
    /// </summary>
    Task<PriceCollectorWorkCursor?> LastIssuedWorkAsync(CancellationToken cancellationToken);
}
