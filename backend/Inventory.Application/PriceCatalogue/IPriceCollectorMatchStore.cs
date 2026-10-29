namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// The narrow write port the collector's match proposals are accepted through (issue #941, parent
/// #817): one batch of proposals for one open run.
///
/// <para><strong>One platform table, one supplier-listing move, and three columns of one tenant-owned
/// row.</strong> It writes <c>ProductMatchSuggestions</c> (the match this run decided or proposed),
/// moves the <c>SupplierListings</c> held under a matched key onto the master product, and sets
/// <c>Status</c>, <c>MasterProductId</c> and <c>MatchSource</c> on the <c>ProductPriceTracking</c>
/// rows that carry that key and are still awaiting a match. Nothing else is reachable through it: no
/// method takes a business, a predicate, a column list, a page or a statement, and no answer carries a
/// business id, a product id, a mapping id or a count of the mappings a match changed.</para>
///
/// <para><strong>The tenant-owned write is the reviewed exception this issue is about.</strong> A
/// collector request carries a <em>denied</em> <c>BusinessScope</c>, so the mappings are invisible to
/// the central query filters and <c>BusinessOwnershipEnforcer</c> refuses every tenant-owned write -
/// which is the rule, and the right one. A match decided for a waiting <em>key</em> nevertheless has
/// to reach the mappings that carry it, in every business that carries it, because the key is what
/// deduplicates five businesses' five products into one search. The exception is therefore bounded by
/// what may be written rather than by whose row it is: one entity type, updates only (never an insert
/// and never a delete), three columns and the instant beside them, and only on rows whose stored
/// waiting key equals the key the run issued. It is documented beside issue #849's supplier
/// provisioning, which it does not widen - see AGENTS.md § Tenant ownership and data isolation and
/// docs/architecture.md § Collector product matching.</para>
///
/// <para><strong>One transaction, and the write lock is taken before the reads.</strong> Deciding a
/// proposal means reading the run, the work the run issued, the master catalogue, the refusals a
/// Super Admin left and the mappings that carry the key, and then writing a suggestion, the listings
/// and those mappings. The authoritative read, the comparison and the mutation are one operation; the
/// idempotency key - this run and this waiting key - is checked inside it, so two submissions of one
/// key cannot both decide that the key was free.</para>
///
/// <para><strong>Every rule is the Domain's.</strong> <c>AutomaticProductMatchPolicy</c> decides
/// whether a match is obvious enough to apply, <c>ProductMatchSuggestionPolicy</c> decides what a
/// suggestion may hold, <c>ProductPriceTrackingPolicy</c> decides which mappings may change and what a
/// matched mapping must look like, and <c>SupplierListingPolicy</c> decides what a moved listing may
/// hang from. The adapter behind this port reads, writes and sequences; it does not judge.</para>
/// </summary>
public interface IPriceCollectorMatchStore
{
    /// <summary>
    /// Decides and stores one batch of proposals, and answers what became of each.
    ///
    /// <para>Answers <see langword="null"/> when no run has that id, which the endpoint reports as
    /// <c>404</c>: a run nothing issued is not a run whose proposals could be refused one by one.
    /// </para>
    /// </summary>
    /// <exception cref="Domain.Exceptions.DomainConflictException">
    /// The run has finished or has expired. Nothing is stored - a closed run accepts no proposals,
    /// and touching an overdue one expires it first.
    /// </exception>
    Task<ProductMatchReceipt?> SubmitAsync(
        ProductMatchBatch batch,
        DateTime asOfUtc,
        CancellationToken cancellationToken);
}
