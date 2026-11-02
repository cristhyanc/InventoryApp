namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// The narrow port the Super Admin's management of the shared master-product catalogue is written
/// through (issue #1024, parent #817): adding a master product before any business asks for one, and
/// correcting a wrong brand, name or size on one that exists.
///
/// <para><strong>It is the second writer of <c>MasterProducts</c>, and that is the whole of this
/// issue.</strong> The first is the Super Admin review of issue #928
/// (<see cref="IPriceCatalogueReviewStore"/>), which creates a master product as part of deciding
/// which product a business's search was for. This one is the catalogue kept in its own right. No
/// automatic match creates one, a Business Owner reaches neither path, and <strong>deleting</strong>
/// one is a separate issue that follows this.</para>
///
/// <para><strong>One table, and nothing else at all.</strong> <c>MasterProducts</c> is written;
/// <c>SupplierListings</c> is <em>read</em>, for one reason only - counting how many of a product's
/// listings are confirmed, which is what decides whether its size may change. Nothing else is read
/// and nothing else is written: not an observation, not a suggestion, not a collector work item, and
/// above all nothing tenant-owned. No <c>Product</c>, no <c>ProductPriceTracking</c> and no
/// <c>Business</c> is read or written here, so a business's own product names, sizes, stock, costs,
/// purchases and mappings are untouched by a create or an edit (the owner's clarification of
/// 31 October 2026, <em>catalogue-only writes</em>). A platform table carries no business key and no
/// query filter, so this adapter needs no reviewed cross-business read and no write lease: the write
/// passes <c>BusinessOwnershipEnforcer</c> on the denied <c>BusinessScope</c> these requests carry
/// because a master product is not <c>IBusinessOwned</c>, not because anything was relaxed.</para>
///
/// <para><strong>An edit keeps the row's key.</strong> Correcting a brand, a name or a size is an
/// update of the row somebody is already pointing at, never a new row and a copy: every tracking
/// mapping, supplier listing, match suggestion and collector work item that names this master product
/// still names it afterwards, and none of them is rewritten.</para>
///
/// <para><strong>Each call is one <c>BEGIN IMMEDIATE</c> transaction.</strong> The duplicate-key
/// check, the confirmed-listing count and the expected-instant comparison are all questions about the
/// state a person was looking at, and they are only authoritative while nothing can commit between
/// the question and the write - the shape every decision of #928 uses. The unique index over the
/// normalised key is the database's own backstop under the duplicate check, and its violation is
/// reported as the same conflict rather than as a database failure.</para>
///
/// <para>See docs/architecture.md § Super Admin master products.</para>
/// </summary>
public interface IMasterProductCatalogueWriteStore
{
    /// <summary>
    /// Creates one master product from <paramref name="description"/> and answers it as the catalogue
    /// reports it, with no supplier listings yet.
    /// </summary>
    /// <exception cref="Domain.Exceptions.DomainConflictException">
    /// Another master product already carries that normalised key. Nothing is written.
    /// </exception>
    Task<CataloguedMasterProduct> CreateAsync(
        Domain.PriceCatalogue.NormalisedMasterProduct description,
        DateTime asOfUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Corrects one master product's brand, name and size, recomputing its normalised key and its
    /// <c>UpdatedAtUtc</c>, and answers it as the catalogue reports it - or <see langword="null"/>
    /// when no master product has that id, which the endpoint reports as <c>404</c>.
    /// </summary>
    /// <exception cref="Domain.Exceptions.DomainConflictException">
    /// Another master product already carries the resulting key, the product has been changed since
    /// the edit was prepared, or the size would change while a supplier listing of it is confirmed.
    /// Nothing is written.
    /// </exception>
    Task<CataloguedMasterProduct?> UpdateAsync(
        long masterProductId,
        MasterProductEdit edit,
        DateTime asOfUtc,
        CancellationToken cancellationToken);
}
