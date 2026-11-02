namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// The narrow port the <strong>business</strong> lookup of the shared master products is read through
/// (issue #1049, parent #817): one bounded page of master products matching a search term, and
/// nothing else.
///
/// <para><strong>It is read-only, and that is the whole of it.</strong> There is no method here that
/// creates, edits or deletes anything, nothing behind it opens a transaction or calls
/// <c>SaveChanges</c>, and a lookup therefore leaves every master product exactly as it was. The two
/// writers of a master product are the Super Admin review of issue #928 and the Master Products
/// create/edit of issue #1024, and this port is deliberately not a third.</para>
///
/// <para><strong>One platform table, and no tenant-owned one at all.</strong> <c>MasterProducts</c>:
/// a brand, a name and a size are facts about a product and about no business. It reads no supplier
/// listing, no price, no match suggestion, no price-tracking mapping, no product and no business - so
/// it needs no <c>IgnoreQueryFilters</c> statement and no write lease, and there is nowhere in its
/// answer for a business id, a business name or a business's product id to travel. The caller's own
/// business is resolved by the request pipeline and is simply not a parameter of this port.</para>
///
/// <para><strong>It is separate from <see cref="IMasterProductCatalogueStore"/> on purpose.</strong>
/// That port is the Super Admin's view and answers with the catalogue's own working detail - the
/// instants, the supplier listings and their counts, a listing's current price. None of that may
/// reach a business, so "what may a member read of a master product?" has a port of its own whose
/// every answer is the reviewed field set, rather than a widened projection of an administrator's
/// one.</para>
///
/// <para><strong>No method takes a predicate, a column list, a sort, a statement or a master product
/// id.</strong> The one thing a caller may narrow by is a search term over the brand and the name,
/// and the one thing it may choose is which bounded page it wants - both already validated and
/// resolved by <see cref="MasterProductSuggestionPaging"/> before they reach here. See
/// docs/architecture.md § Tenant ownership and § Tracking supplier prices on a product.</para>
/// </summary>
public interface IMasterProductSuggestionStore
{
    /// <summary>
    /// The master products whose brand or name matches <paramref name="filter"/>'s search term,
    /// ordered by brand, name, size and then the row's own key, for the requested slice.
    ///
    /// <para>The order is fixed here rather than chosen by a caller, and it ends in the key so that
    /// two products with the same brand, name and size still come out in the same order on every
    /// read - which is what makes paging over it stable.</para>
    ///
    /// <para>It answers the slice it was asked for and no count of the whole match: whether more
    /// matches remain is decided by the use case from the rows themselves, so no business is told how
    /// large the shared catalogue is.</para>
    /// </summary>
    Task<IReadOnlyList<MasterProductIdentity>> SearchAsync(
        MasterProductSuggestionFilter filter,
        CancellationToken cancellationToken);
}
