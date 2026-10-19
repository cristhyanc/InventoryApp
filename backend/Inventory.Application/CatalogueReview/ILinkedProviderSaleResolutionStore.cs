namespace Inventory.Application.CatalogueReview;

/// <summary>
/// One linked product's unresolved sales: the stored sales that carry the provider product id the
/// product now records, still have no local product, and whose stored provider id has known raw
/// provenance (issue #758).
///
/// <para><see cref="EarliestCompletedSaleUtc"/> is the earliest <em>completed</em> sale among them,
/// or <see langword="null"/> when none of them is completed. Only a completed sale consumes costed
/// inventory, so it is the only kind a replay has to start from.</para>
/// </summary>
public sealed record UnresolvedLinkedSaleGroup(
    long ProductId,
    string ProductName,
    string ProviderProductId,
    int SaleCount,
    DateTime? EarliestCompletedSaleUtc);

/// <summary>
/// Narrow persistence port for resolving the sales a catalogue link made resolvable (issue #738),
/// owned by the Application layer and implemented by
/// <c>Inventory.Infrastructure.Persistence.EfLinkedProviderSaleResolutionStore</c>.
///
/// <para>It decides nothing. Reads and writes are scoped to the caller's business by the central
/// query filter and ownership enforcer alone, never by a predicate of its own, and the only write it
/// offers fills a sale's <c>ProductId</c> <strong>while it is still null</strong> and appends the
/// audit row that records it - which is what makes the whole operation idempotent.</para>
/// </summary>
public interface ILinkedProviderSaleResolutionStore
{
    /// <summary>
    /// Begins the transaction the resolution's writes and its cost replay share, so a product whose
    /// cost history cannot be replayed leaves no resolved sale behind.
    /// </summary>
    Task<ICatalogueReviewTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Every linked product of the caller's business that still has unresolved sales carrying its
    /// provider id, ordered by product. This is the operation's own authoritative read; the review
    /// reports the same counts so an operator sees them before deciding.
    /// </summary>
    Task<IReadOnlyList<UnresolvedLinkedSaleGroup>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Attributes every group's unresolved sales to its product and appends one audit row per sale,
    /// answering how many sales it settled per product id.
    ///
    /// <para>Each sale's <c>ProductId</c> is filled by one guarded statement that carries the
    /// expected null in its own predicate, rather than from a value read beforehand, and the audit
    /// row recording the previous value, the new value, the rule, who decided and when is staged with
    /// it. A sale another writer settled first is simply not in the answer and nothing is written for
    /// it - which is what makes a repeated run, and two overlapping runs, a no-op that can never move
    /// a decided sale.</para>
    ///
    /// <para>Every group is settled before any audit row is staged, deliberately: the guarded write is
    /// a set-based statement, and one must never run with tracked changes of this operation's own
    /// waiting behind it. The audit rows are staged, not saved - the caller saves and commits.</para>
    /// </summary>
    Task<IReadOnlyDictionary<long, int>> ResolveAsync(
        IReadOnlyCollection<UnresolvedLinkedSaleGroup> groups,
        string resolvedBy,
        DateTime resolvedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// The products' inventory-cost transition baseline cutoffs, for the products that have one. A
    /// sale at or before its product's cutoff is covered by that baseline, so replaying it would
    /// recost history the transition owns; a product with no baseline is replayed by no write path in
    /// the application, and this operation does not invent one either.
    /// </summary>
    Task<IReadOnlyDictionary<long, DateTime>> GetTransitionCutoffsAsync(
        IReadOnlyCollection<long> productIds,
        CancellationToken cancellationToken);

    /// <summary>Saves what has been staged, through the ordinary tenant-enforcing save path.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
