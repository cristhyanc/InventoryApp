using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Suppliers;

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
/// One supplier master record as the collector work list reads it (issue #924, with the master
/// fields of #931): what it is called, how its prices are collected, and when it was last checked.
///
/// <para><strong>Whether it is due is not this record's answer.</strong> The record carries the
/// stored facts - enabled, connector, cadence, last successful check - and
/// <see cref="PriceCollectorWorkListPolicy.IsSupplierDue"/> is the one place they become a decision,
/// so no adapter and no controller holds a second version of the cadence rule.</para>
/// </summary>
/// <param name="SupplierPresetId">The supplier master record's own key.</param>
/// <param name="Name">Its name, which is what a collector's log and a reviewer read.</param>
/// <param name="IsEnabled">Whether the platform still suggests it.</param>
/// <param name="PriceConnector">
/// How its prices are collected, or <see langword="null"/> when nothing collects them.
/// </param>
/// <param name="PriceConnectorKey">
/// What that connector needs in order to know which retailer it is for, or <see langword="null"/>
/// where the route takes none.
/// </param>
/// <param name="SiteUrl">
/// The supplier's own site, which is the endpoint the JB Metro route talks to, or
/// <see langword="null"/>.
/// </param>
/// <param name="PricesIncludeGst">Whether the prices it publishes include GST.</param>
/// <param name="PriceCheckFreshnessHours">Its own stored freshness limit, which is its cadence.</param>
/// <param name="LastCheckedAtUtc">
/// The most recent instant any of its listings was checked successfully, or <see langword="null"/>
/// while none ever has been.
/// </param>
public sealed record CollectableSupplier(
    int SupplierPresetId,
    string Name,
    bool IsEnabled,
    SupplierPriceConnector? PriceConnector,
    string? PriceConnectorKey,
    string? SiteUrl,
    bool PricesIncludeGst,
    int PriceCheckFreshnessHours,
    DateTime? LastCheckedAtUtc);

/// <summary>
/// One master product as the matching list of #941 needs it (issue #924): its id and its description,
/// and deliberately nothing else - no listing, no price, no provenance and nothing about any
/// business.
/// </summary>
public sealed record CatalogueMasterProduct(
    long Id,
    string Brand,
    string Name,
    decimal SizeValue,
    MasterProductSizeUnit SizeUnit);

/// <summary>
/// One master product a Super Admin has refused for one waiting key (issue #924): a suggestion that
/// was rejected, or undone after it had been applied.
///
/// <para>It is sent so the matcher of #941 does not propose that pair again. A person who rejected a
/// match once must not have to reject it every run, which is why the refusals outlive the refusal
/// itself (see <c>ProductMatchSuggestion</c>).</para>
/// </summary>
public sealed record RefusedMasterProduct(string WaitingKey, long MasterProductId);

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
/// <param name="LastCheckedAtUtc">
/// When the listing was last checked <em>successfully</em>, or <see langword="null"/> while it never
/// has been (issue #924). It is the listing's own instant rather than its supplier's, because that is
/// what tells the collector which of a supplier's known listings it has already priced today.
/// </param>
public sealed record TrackedSupplierListing(
    long Id,
    int SupplierPresetId,
    string SupplierProductId,
    string? Url,
    string? Title,
    int? UnitsPerPack,
    string? Barcode,
    BarcodeScope? BarcodeScope,
    SupplierListingMatchStatus MatchStatus,
    DateTime? LastCheckedAtUtc);

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
/// <para><strong>This is the one cross-business read of the collector surface, and issue #924 is the
/// reviewed decision that made it one.</strong> Issue #923 left the adapter reading the mappings
/// through the ordinary tenant query filters and recorded that "which caller may read the work list
/// across businesses, and how it authenticates, is #924's own reviewed decision". That decision is
/// made: the collector is not an actor and has no business, its request carries a <em>denied</em>
/// <c>BusinessScope</c>, and the only tenant-owned rows it may reach are the three text columns of
/// <c>ProductPriceTracking</c> that this port's answers are built from - a waiting key, a search name
/// and a unit label, plus the master product id a mapping names. None of those says whose product it
/// is, which is the whole of what makes the read safe. The adapter makes exactly one
/// <c>IgnoreQueryFilters</c> statement per read, over that projection and no other, and it is bounded
/// by authorization rather than by ownership: the only endpoint that calls it is
/// <c>GET /api/price-collector/work</c>, which no member, Super Admin or support session can reach.
/// See AGENTS.md § Tenant ownership and data isolation and docs/architecture.md § Collector work
/// list.</para>
///
/// <para><strong>Everything else it reads is platform data.</strong> The supplier master records, the
/// master products, the listings and the match refusals carry no owner at all, so there is no filter
/// for them to ignore and no business for them to leak.</para>
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

    /// <summary>
    /// Every supplier master record the platform holds, with its collection route, its cadence and
    /// its most recent successful check (issue #924), ordered by its own key.
    ///
    /// <para>All of them, not the due ones: whether a supplier is due is
    /// <see cref="PriceCollectorWorkListPolicy.IsSupplierDue"/>'s decision, and an adapter that
    /// filtered by it would be a second copy of that rule in SQL. It is a platform read - a supplier
    /// master record is about no business - so no filter applies to it either way.</para>
    /// </summary>
    Task<IReadOnlyList<CollectableSupplier>> ListCollectableSuppliersAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// The master products a waiting product may be matched against (issue #924, for the automatic
    /// and tentative matching of #941): id, brand, name, size and unit, and nothing else.
    ///
    /// <para>At most <paramref name="limit"/> of them, ordered by comparison key then row key, so a
    /// catalogue larger than the limit yields the same deterministic prefix on two reads rather than
    /// an arbitrary one. The caller sends this list only while something is actually waiting.</para>
    /// </summary>
    Task<IReadOnlyList<CatalogueMasterProduct>> ListMasterProductsForMatchingAsync(
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// The listings already discovered under each of <paramref name="waitingKeys"/> and still held
    /// there - the proposals a previous run found and nothing has matched yet - keyed by that waiting
    /// key.
    ///
    /// <para>They are sent so the collector can recognise what it already found for a search it is
    /// about to run again, the same way the known listings under a matched product exist. A platform
    /// read: a listing is a fact about a supplier's shelf.</para>
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<TrackedSupplierListing>>>
        ListWaitingListingsAsync(
            IReadOnlyCollection<string> waitingKeys,
            CancellationToken cancellationToken);

    /// <summary>
    /// The master products a Super Admin has rejected, or undone after applying, for each of
    /// <paramref name="waitingKeys"/> (issue #924), ordered by key and then by master product.
    ///
    /// <para>A refusal that proposed creating a <em>new</em> master product names none, so it is not
    /// in this answer: there is no master product id to send, and what to do about such a refusal is
    /// #941's matching decision rather than this list's. Nothing here says who rejected anything or
    /// when.</para>
    /// </summary>
    Task<IReadOnlyList<RefusedMasterProduct>> ListRefusedMasterProductsAsync(
        IReadOnlyCollection<string> waitingKeys,
        CancellationToken cancellationToken);
}
