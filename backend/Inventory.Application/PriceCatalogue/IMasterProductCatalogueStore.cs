namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// The narrow port the Super Admin's view of the shared master-product catalogue is read through
/// (issue #1023, parent #817): one bounded, searchable page of master products, and one master
/// product with its supplier listings.
///
/// <para><strong>It is read-only, and that is the whole of it.</strong> There is no method here that
/// creates, edits or deletes anything: nothing is written by anything behind this port, so it opens
/// no write lease, takes no transaction and makes no <c>SaveChanges</c>. The two writes of the same
/// surface are <see cref="IMasterProductCatalogueWriteStore"/>'s (issue #1024), deliberately a
/// separate port so that "what reads the catalogue?" and "what changes it?" have separate answers;
/// the only other writer of a master product is the Super Admin review of issue #928
/// (<see cref="IPriceCatalogueReviewStore"/>), and deleting one is a separate issue that follows.</para>
///
/// <para><strong>What it may touch.</strong> Four platform tables, all of them about products and
/// suppliers and none of them about any business: <c>MasterProducts</c>, <c>SupplierListings</c>, the
/// <c>SupplierPresets</c> that publish those listings, and the <c>SupplierPriceObservations</c> row a
/// listing points at for its current price. It reaches <strong>no tenant-owned table at all</strong> -
/// not a product, not a price-tracking mapping, not a business - so it needs none of the reviewed
/// cross-business reads the review and the collector surfaces have, and it adds no
/// <c>IgnoreQueryFilters</c> statement anywhere: a platform table carries no business key and no query
/// filter, so a denied <c>BusinessScope</c> reads all of it and none of anybody's.</para>
///
/// <para><strong>No business id, business name or business product id can leave it</strong>, because
/// there is nowhere in any of its answers for one to travel and no statement behind it that reads one.
/// A count of the businesses tracking a product is deliberately not offered either: that count is
/// about businesses, and this surface is about the catalogue (the owner's clarification of
/// 31 October 2026 - master-catalogue management only, no cross-business mapping-count surface).</para>
///
/// <para><strong>No method takes a predicate, a column list, a sort or a statement.</strong> The one
/// thing a caller may narrow by is a search term over the brand and the name, and the one thing it may
/// choose is which bounded page it wants - both of them already resolved by
/// <see cref="MasterProductCataloguePaging"/> before they reach here. See docs/architecture.md § Super
/// Admin master products.</para>
/// </summary>
public interface IMasterProductCatalogueStore
{
    /// <summary>
    /// One page of master products matching <paramref name="filter"/>, ordered by brand, name, size
    /// and then the row's own key, with the total number the search matches.
    ///
    /// <para>The order is fixed here rather than chosen by a caller, and it ends in the key so that
    /// two products with the same brand, name and size still come out in the same order on every
    /// read - which is what makes paging over it stable.</para>
    /// </summary>
    Task<MasterProductCatalogueResult> ListAsync(
        MasterProductCatalogueFilter filter,
        CancellationToken cancellationToken);

    /// <summary>
    /// One master product with every supplier listing that hangs from it, or <see langword="null"/>
    /// when no master product has that id - which the endpoint reports as <c>404</c>.
    /// </summary>
    Task<MasterProductDetail?> FindAsync(long masterProductId, CancellationToken cancellationToken);
}
