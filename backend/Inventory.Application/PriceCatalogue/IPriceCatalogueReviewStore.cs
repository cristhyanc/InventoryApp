namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// The narrow port the Super Admin's review of the shared price catalogue is read and written through
/// (issue #928, parent #817): the queue, one listing decision, one match decision, and one decision
/// about an automatic match.
///
/// <para><strong>What it may touch.</strong> It writes the platform tables of the catalogue -
/// <c>MasterProducts</c> (this is their only writer, and the only way a master product is ever
/// created), <c>SupplierListings</c>, <c>ProductMatchSuggestions</c>,
/// <c>SupplierPriceObservations</c> and <c>SupplierListingPendingObservations</c> - and the four match
/// columns of the <c>ProductPriceTracking</c> rows that carry the key being decided. Nothing else is
/// reachable through it: no method takes a business, a predicate, a column list, a page, a sort or a
/// statement, and no answer carries a business id, a business name or a product id of anybody's.</para>
///
/// <para><strong>The one tenant-owned write is this issue's own reviewed exception</strong>, and it is
/// #941's exactly: a platform-admin request carries a <em>denied</em> <c>BusinessScope</c>, so the
/// mappings are invisible to the central query filters and <c>BusinessOwnershipEnforcer</c> refuses
/// every tenant-owned write - which is the rule, and the right one. A match decided for a waiting
/// <em>key</em> nevertheless has to reach the mappings that carry it, in every business that carries
/// it, because the key is what deduplicates several businesses' products into one search. The
/// exception is therefore bounded by what may be written rather than by whose row it is: one entity
/// type, updates only, four columns and nothing else. See AGENTS.md § Tenant ownership and data
/// isolation and docs/architecture.md § Super Admin review of the supplier price catalogue.</para>
///
/// <para><strong>One transaction each, with the write lock taken before the reads.</strong> Every
/// decision here compares a listing's or a suggestion's <em>current</em> state against the state the
/// reviewer was looking at and refuses a stale one, so the authoritative read, the comparison and the
/// mutation have to be one operation - the shape the switch of #938, the machine delete of #896 and
/// the matches of #941 all use.</para>
///
/// <para><strong>Every rule is the Domain's.</strong> <c>SupplierListingReviewPolicy</c> decides which
/// listings may be decided and what confirming one does with the reading it holds,
/// <c>ProductMatchReviewPolicy</c> decides which suggestions and mappings a match or an undo reaches,
/// <c>MasterProductPolicy</c> and <c>SupplierListingPolicy</c> decide what may be stored,
/// <c>SupplierPriceFreshnessPolicy</c> decides whether a reading is still guidance and
/// <c>SupplierListingPriceStatePolicy</c> decides what a listing then currently costs. The adapter
/// behind this port reads, writes and sequences; it does not judge.</para>
/// </summary>
public interface IPriceCatalogueReviewStore
{
    /// <summary>
    /// The whole review queue as of <paramref name="asOfUtc"/>: the products awaiting a match, the
    /// automatic matches waiting to be checked, and the listings waiting for a decision.
    ///
    /// <para>It takes no filter, page, sort or search parameter, for the reason the platform business
    /// directory and the supplier presets take none: a caller-supplied predicate is how a fixed
    /// platform list becomes a query engine. The instant is passed in rather than read here because
    /// freshness is a Domain answer about a stored reading and this port makes no clock call of its
    /// own.</para>
    /// </summary>
    Task<PriceCatalogueReviewQueue> ReadQueueAsync(DateTime asOfUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Decides one supplier listing: confirm it at the identity the reviewer reviewed, or refuse it.
    ///
    /// <para>Answers <see langword="null"/> when no listing has that id, which the endpoint reports
    /// as <c>404</c>.</para>
    /// </summary>
    /// <exception cref="Domain.Exceptions.DomainValidationException">
    /// The submitted identity is not one this catalogue stores - a pack quantity that is not positive,
    /// a barcode without its scope, a size without its unit. Nothing is written.
    /// </exception>
    /// <exception cref="Domain.Exceptions.DomainConflictException">
    /// The listing is no longer waiting for a decision. Nothing is written.
    /// </exception>
    Task<SupplierListingReviewResult?> DecideListingAsync(
        SupplierListingReviewSubmission submission,
        DateTime asOfUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Decides one waiting product: accept a suggestion, pick another master product, create one,
    /// refuse a suggestion, or record that nothing in the catalogue is this product.
    ///
    /// <para>Answers <see langword="null"/> when nothing is waiting under the submitted key, which
    /// the endpoint reports as <c>404</c>: a key nothing is waiting under is not a decision anybody
    /// can make.</para>
    /// </summary>
    /// <exception cref="Domain.Exceptions.DomainValidationException">
    /// The decision's own values are incomplete, the master product named does not exist, or a
    /// created master product is not one this catalogue stores. Nothing is written.
    /// </exception>
    /// <exception cref="Domain.Exceptions.DomainConflictException">
    /// The suggestion named is no longer waiting for a decision, or another master product already
    /// holds the description a create submitted. Nothing is written.
    /// </exception>
    Task<ProductMatchReviewResult?> DecideMatchAsync(
        ProductMatchReviewSubmission submission,
        DateTime asOfUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Confirms or undoes one automatic match.
    ///
    /// <para>Answers <see langword="null"/> when no suggestion has that id.</para>
    /// </summary>
    /// <exception cref="Domain.Exceptions.DomainConflictException">
    /// The suggestion is not an automatic match waiting to be checked. Nothing is written.
    /// </exception>
    Task<AutomaticMatchReviewResult?> DecideAutomaticMatchAsync(
        long suggestionId,
        Domain.PriceCatalogue.AutomaticMatchReviewDecision decision,
        DateTime asOfUtc,
        CancellationToken cancellationToken);
}
