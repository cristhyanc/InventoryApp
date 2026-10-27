using Inventory.Domain.PriceCatalogue;

namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// One product that is still waiting for a master product, as the collector work list of #924 needs
/// it (issue #923, parent #817): a search to run, and the key its answer belongs to.
///
/// <para><strong>It carries no business identity of any kind</strong> - no business id, no product
/// id, no count of how many businesses are waiting on it - which is the issue's third known
/// constraint stated as a type: "product names and unit labels sent to the collector carry no
/// business identity", and no business can see which products other businesses track. What is left is
/// a product description, which is a fact about a product.</para>
/// </summary>
/// <param name="WaitingKey">
/// The key the two values below fold to, and the key an answer is recorded against.
/// </param>
/// <param name="SearchName">
/// The product name to search for. One spelling per key: where several businesses' spellings fold to
/// one key, this is the first of them in ordinal order, chosen so two reads of the work list cannot
/// disagree about which spelling to send.
/// </param>
/// <param name="UnitLabel">The unit label beside it, and the empty string where there is none.</param>
public sealed record ProductAwaitingMatch(string WaitingKey, string SearchName, string UnitLabel);

/// <summary>
/// One supplier's listing of a tracked master product (issue #923), as the collector work list reads
/// it.
/// </summary>
/// <param name="Id">The listing's own key.</param>
/// <param name="SupplierPresetId">The supplier master record that publishes it.</param>
/// <param name="SupplierProductId">That supplier's own product id or SKU, as published.</param>
/// <param name="Url">The page it is on, or <see langword="null"/>.</param>
/// <param name="Title">The supplier's own wording, or <see langword="null"/>.</param>
/// <param name="UnitsPerPack">How many come in a pack, or <see langword="null"/> when unknown.</param>
/// <param name="Barcode">The barcode as published, leading zeros intact, or <see langword="null"/>.</param>
/// <param name="BarcodeScope">
/// Whether that barcode is the unit's or the carton's, or <see langword="null"/> when there is none.
/// </param>
/// <param name="MatchStatus">Whether the listing is proposed, confirmed or flagged for review.</param>
public sealed record TrackedSupplierListing(
    long Id,
    int SupplierPresetId,
    string SupplierProductId,
    string? Url,
    string? Title,
    int? UnitsPerPack,
    string? Barcode,
    BarcodeScope? BarcodeScope,
    SupplierListingMatchStatus MatchStatus);

/// <summary>
/// One master product that at least one business is tracking, with the listings the catalogue holds
/// for it (issue #923, parent #817).
///
/// <para><strong>One entry per master product, however many businesses track it.</strong> That is the
/// whole business outcome of the parent issue - "we can't search Coca-Cola 375 five times because we
/// have five businesses" - and it is why this is a list of <em>products</em> rather than a list of
/// mappings: five mappings onto one master product are one thing to collect a price for. Nothing here
/// says which businesses they were, or how many.</para>
/// </summary>
/// <param name="Id">The master product's own key.</param>
/// <param name="Brand">Its brand.</param>
/// <param name="Name">Its name or variant.</param>
/// <param name="SizeValue">The size of one item.</param>
/// <param name="SizeUnit">What that size is measured in.</param>
/// <param name="Listings">
/// Its listings, one per <c>(supplier master record, supplier product id)</c> - which the unique
/// index on those two columns is what guarantees - ordered by supplier and then by that id.
/// </param>
public sealed record TrackedMasterProduct(
    long Id,
    string Brand,
    string Name,
    decimal SizeValue,
    MasterProductSizeUnit SizeUnit,
    IReadOnlyList<TrackedSupplierListing> Listings);

/// <summary>
/// The narrow read port the collector work list of #924 is built from (issue #923, parent #817): what
/// is still waiting for a match, and what is already matched and has listings.
///
/// <para><strong>Two reads, no caller-supplied anything.</strong> Neither method takes a business, a
/// predicate, a column list, a page, a sort or a statement, and neither answer carries a business id,
/// a product id or anything else that could say whose products these are. What crosses this boundary
/// is a product description and a supplier's listing of it.</para>
///
/// <para><strong>The deduplication is this port's job, and it is the point of it.</strong> Products
/// awaiting a match are returned as distinct <c>(search name, unit label)</c> pairs by waiting key -
/// so case and spacing cannot make two searches of one product - and tracked master products are
/// returned once each however many mappings point at them.</para>
///
/// <para><strong>What this port does not do is widen anything.</strong> The adapter behind it reads
/// the mappings through the ordinary tenant query filters, with no <c>IgnoreQueryFilters</c>, no raw
/// SQL and no unscoped context: under a business's scope it answers for that business, and the
/// deduplication is over whatever rows the reader may already see. <strong>Which caller may read the
/// work list across businesses, and how it authenticates, is #924's own reviewed decision</strong> -
/// collector authentication is explicitly out of this issue's scope, and nothing here grants it. See
/// AGENTS.md § Tenant ownership and data isolation, whose cross-business rules this issue leaves
/// exactly as they are.</para>
/// </summary>
public interface IPriceCatalogueWorkListStore
{
    /// <summary>
    /// The products that are still waiting for a master product, as distinct
    /// <c>(search name, unit label)</c> pairs with their waiting key, ordered by that key.
    ///
    /// <para>"Waiting" is <see cref="ProductPriceTrackingStatus.Requested"/> and nothing else. A
    /// mapping that was looked for and <see cref="ProductPriceTrackingStatus.NotFound"/> is a recorded
    /// answer rather than a gap, and re-sending it every run is exactly what recording that answer
    /// prevents; the two matched states have a master product and belong to the other read.</para>
    /// </summary>
    Task<IReadOnlyList<ProductAwaitingMatch>> ListProductsAwaitingMatchAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// The distinct master products at least one mapping points at, each with its supplier listings.
    ///
    /// <para>One entry per master product, and one listing per <c>(supplier master record, supplier
    /// product id)</c>: two businesses tracking the same product produce one entry, not two, because
    /// this reads the master products the mappings name rather than joining the mappings to the
    /// listings.</para>
    /// </summary>
    Task<IReadOnlyList<TrackedMasterProduct>> ListTrackedMasterProductsAsync(
        CancellationToken cancellationToken);
}
