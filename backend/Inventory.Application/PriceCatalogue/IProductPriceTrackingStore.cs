using Inventory.Domain.PriceCatalogue;

namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// One business's price-tracking mapping as the Application layer sees it (issue #923, parent #817).
///
/// <para>It carries no business id, and that is deliberate rather than incidental: the owner is
/// resolved from the authenticated actor's membership and stamped centrally, never read from or
/// published to a caller (AGENTS.md § Tenant ownership and data isolation). A record that carried one
/// would be a business key travelling through the Application layer for no reader to use.</para>
/// </summary>
/// <param name="Id">The mapping's own key.</param>
/// <param name="ProductId">The business's own product the mapping is about.</param>
/// <param name="MasterProductId">
/// The shared master product it has been matched to, or <see langword="null"/> while it is waiting.
/// </param>
/// <param name="Status">Where the mapping has got to.</param>
/// <param name="MatchSource">How the match was made, or <see langword="null"/> while there is none.</param>
/// <param name="WaitingKey">
/// The key this product is searched for and grouped by - <c>PriceCatalogueWaitingKey.For</c> of the
/// two values below.
/// </param>
/// <param name="SearchName">The product's own name, as it was when tracking was turned on.</param>
/// <param name="UnitLabel">
/// The product's own unit label, as it was then, and the empty string when it has none.
/// </param>
/// <param name="CreatedAtUtc">When tracking was turned on.</param>
/// <param name="UpdatedAtUtc">When the mapping last changed.</param>
public sealed record ProductPriceTrackingRecord(
    long Id,
    long ProductId,
    long? MasterProductId,
    ProductPriceTrackingStatus Status,
    ProductPriceTrackingMatchSource? MatchSource,
    string WaitingKey,
    string SearchName,
    string UnitLabel,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

/// <summary>
/// A business turning tracking on for one of its products: the product, the search text captured from
/// it, and the key that text folds to.
///
/// <para><strong>There is no status, master product or match source here, and there cannot be.</strong>
/// A new mapping is always <see cref="ProductPriceTrackingStatus.Requested"/> with nothing matched:
/// deciding which master product a product is belongs to the automatic matcher (#941) and the Super
/// Admin review (#928), and a business asking for a price is not a business asserting what its
/// product is.</para>
///
/// <para><strong>The values are already through the Domain rules</strong>, like
/// <c>NewSupplierPreset</c>'s are - which is what keeps the adapter from deciding any of them. Use
/// <see cref="For"/> rather than the constructor, so the stored search text and the key it folds to
/// cannot disagree.</para>
/// </summary>
/// <param name="ProductId">The business's own product, which must belong to the caller's business.</param>
/// <param name="SearchName">The validated, trimmed search name.</param>
/// <param name="UnitLabel">The validated, trimmed unit label; empty when the product has none.</param>
/// <param name="WaitingKey">
/// <c>PriceCatalogueWaitingKey.For</c> of the two values above - the canonical form, handed over
/// rather than computed by the adapter.
/// </param>
/// <param name="CreatedAtUtc">The instant to record as both created and updated.</param>
public sealed record NewProductPriceTracking(
    long ProductId,
    string SearchName,
    string UnitLabel,
    string WaitingKey,
    DateTime CreatedAtUtc)
{
    /// <summary>
    /// A new mapping for one product, with its search text taken from the product's own name and unit
    /// label and run through <c>ProductPriceTrackingPolicy</c>, and its waiting key derived from the
    /// result.
    ///
    /// <para>This is the one way a mapping should be built. Deriving the key here rather than at each
    /// call site is what makes "the stored key is the fold of the stored text" true by construction -
    /// a mapping whose key did not match its text would be searched for as one product and grouped as
    /// another.</para>
    /// </summary>
    /// <exception cref="Inventory.Domain.Exceptions.DomainValidationException">
    /// The product has no usable name, or its name or unit label is longer than the policy accepts.
    /// </exception>
    public static NewProductPriceTracking For(
        long productId,
        string? productName,
        string? productUnit,
        DateTime createdAtUtc)
    {
        var searchName = ProductPriceTrackingPolicy.NormaliseSearchName(productName);
        var unitLabel = ProductPriceTrackingPolicy.NormaliseUnitLabel(productUnit);

        return new NewProductPriceTracking(
            productId,
            searchName,
            unitLabel,
            PriceCatalogueWaitingKey.For(searchName, unitLabel),
            createdAtUtc);
    }
}

/// <summary>
/// The narrow persistence port over one business's price-tracking mappings (issue #923, parent #817):
/// which of its own products a business wants priced.
///
/// <para><strong>Ordinary tenant-owned data, reached the ordinary way.</strong> No method here takes a
/// business id, a predicate, a column list, a page or a statement: the owner comes from the
/// authenticated actor's membership, the central query filters scope every read, and
/// <c>BusinessOwnershipEnforcer</c> stamps the insert and refuses anything that would cross a
/// boundary. There is no <c>IgnoreQueryFilters</c> in the adapter behind it and no unscoped context
/// anywhere near it, so another business's mapping is unreachable by listing and by id alike.</para>
///
/// <para><strong>There is no update and no delete yet</strong>, deliberately. Turning tracking on is
/// #938, deciding a match is #941 and #928, and turning it off is whichever task the owner asks for
/// it in; this issue is the schema, the ownership rules and the adapters, so the port holds exactly
/// the three operations those criteria name.</para>
/// </summary>
public interface IProductPriceTrackingStore
{
    /// <summary>
    /// Stores a new mapping for the caller's business and answers it as it was stored.
    ///
    /// <para>The business is never a parameter: it is stamped from the resolved scope, so a caller
    /// cannot choose an owner, and the mapping's product is checked by the database against that same
    /// business - a product of another business is refused by the foreign key rather than quietly
    /// stored.</para>
    /// </summary>
    Task<ProductPriceTrackingRecord> AddAsync(
        NewProductPriceTracking tracking,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every mapping of the caller's business, ordered by the search text. It takes no filter at all:
    /// a caller-supplied predicate over a business's own list is how a list becomes a query engine.
    /// </summary>
    Task<IReadOnlyList<ProductPriceTrackingRecord>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// One mapping of the caller's business by its key, or <see langword="null"/> when there is no
    /// such mapping <em>of that business</em> - which is the same answer another business's mapping
    /// gives, because the central filter matches none of its rows.
    /// </summary>
    Task<ProductPriceTrackingRecord?> FindAsync(long id, CancellationToken cancellationToken);
}
