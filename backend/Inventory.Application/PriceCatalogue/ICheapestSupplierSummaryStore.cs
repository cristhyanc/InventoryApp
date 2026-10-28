using Inventory.Domain.Gst;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// One of the caller's own suppliers that may take part in a price comparison (issue #930, parent
/// #817): a supplier the business holds as a linked copy of a platform supplier record (issue #934)
/// and has not switched out of use.
///
/// <para>The name is the <em>business's own</em> <c>Supplier.Name</c> - tenant-owned data it owns
/// outright - and the preset id is what joins it to the supplier's listings in the shared catalogue.
/// The preset id never leaves the Application layer: a business has no use for a platform row's key,
/// and publishing one would invite a client to ask the platform about it (issue #934).</para>
/// </summary>
/// <param name="SupplierId">The business's own supplier row.</param>
/// <param name="Name">Its name, as the business holds it.</param>
/// <param name="SupplierPresetId">The platform supplier record it is a copy of.</param>
public sealed record LinkedSupplier(int SupplierId, string Name, int SupplierPresetId);

/// <summary>
/// The latest accepted observation of one listing's price (issue #925), as the comparison needs it.
///
/// <para>Every value is stored exactly as it was published - the pack price, the basis it was
/// published on, the supplier's own GST rate when it gives one, the promotion and the stock answer -
/// and nothing here has been divided, converted or rounded. The per-unit arithmetic is
/// <c>CheapestUnitOfferPolicy</c>'s alone.</para>
/// </summary>
/// <param name="PackPrice">What the whole pack cost, on <paramref name="PriceBasis"/>.</param>
/// <param name="PriceBasis">Whether that price includes GST, as observed.</param>
/// <param name="SupplierGstRate">The supplier's own rate for this product, or <see langword="null"/>.</param>
/// <param name="IsPromotion">Whether the price was a special rather than the ordinary price.</param>
/// <param name="PromotionEndsAtUtc">When that special finishes, or <see langword="null"/>.</param>
/// <param name="StockStatus">What the supplier said about availability.</param>
/// <param name="ObservedAtUtc">When the price was read, which is what staleness is measured from.</param>
/// <param name="FreshnessLimitHours">
/// The supplier master record's own freshness limit, which is per supplier and comes from the stored
/// record rather than from any code that names a supplier.
/// </param>
public sealed record ObservedPackPrice(
    decimal PackPrice,
    SupplierPriceBasis PriceBasis,
    decimal? SupplierGstRate,
    bool IsPromotion,
    DateTime? PromotionEndsAtUtc,
    SupplierStockStatus StockStatus,
    DateTime ObservedAtUtc,
    int FreshnessLimitHours);

/// <summary>
/// One supplier's listing of the master product a tracked product is matched to (issue #930), with
/// the latest price observed for it when there is one.
///
/// <para><strong>A listing with no price is a real state and not an absent supplier.</strong>
/// <see cref="Price"/> is <see langword="null"/> while the listing has never been checked
/// successfully - the state every listing of issue #923 shipped in - and that is published as "not
/// checked yet" rather than as a missing supplier or as no price at all. A failed or missing check
/// never clears the last accepted observation (issue #925), so a price that is here is a price that
/// was read.</para>
/// </summary>
/// <param name="SupplierListingId">The listing, which is what the comparison answers about.</param>
/// <param name="SupplierPresetId">The supplier master record that publishes it.</param>
/// <param name="ListingConfirmed">
/// Whether the listing's match to the master product is confirmed. A proposed or flagged listing is
/// shown and not compared - <c>SupplierOfferIneligibility.ListingNotConfirmed</c>.
/// </param>
/// <param name="UnitsPerPack">How many items the pack holds, or <see langword="null"/> when unknown.</param>
/// <param name="ProductUrl">The supplier's page for this product, or <see langword="null"/>.</param>
/// <param name="LastCheckedAtUtc">
/// When the listing was last checked successfully, or <see langword="null"/> while it never has been.
/// </param>
/// <param name="Price">The latest accepted observation, or <see langword="null"/>.</param>
public sealed record TrackedListingPrice(
    long SupplierListingId,
    int SupplierPresetId,
    bool ListingConfirmed,
    int? UnitsPerPack,
    string? ProductUrl,
    DateTime? LastCheckedAtUtc,
    ObservedPackPrice? Price);

/// <summary>
/// One of the caller's products as the summary reads it (issue #930): where its mapping has got to,
/// the GST rule its own product record carries, and the listings of the master product it is matched
/// to.
/// </summary>
/// <param name="ProductId">The product, which is always one of the caller's own.</param>
/// <param name="TrackingStatus">
/// Where the mapping has got to, or <see langword="null"/> when the business does not track this
/// product at all. The two are different answers and neither is a label on the other.
/// </param>
/// <param name="ProductGstRule">
/// The product's own GST rule (issue #430), which supplies the rate where a supplier publishes none.
/// <c>GstRules.None</c> is no rule, and an offer no rate covers is shown and not compared.
/// </param>
/// <param name="Listings">
/// The listings of the matched master product published by the business's own linked, not-hidden
/// suppliers, and nothing else. Empty while the mapping is not matched, because there is no master
/// product whose listings they would be.
/// </param>
public sealed record TrackedProductListings(
    long ProductId,
    ProductPriceTrackingStatus? TrackingStatus,
    GstClassification ProductGstRule,
    IReadOnlyList<TrackedListingPrice> Listings);

/// <summary>
/// Everything one cheapest-supplier summary is calculated from (issue #930), read in one pass: the
/// basis fact about the business, its own comparison suppliers, and the products asked about.
/// </summary>
/// <param name="BusinessIsGstRegistered">
/// Whether the business is registered for GST, which decides the basis it is shown and compared on
/// (<c>CheapestUnitOfferPolicy.BasisFor</c>). A business that has not recorded an answer is treated
/// as not registered, so it is shown the price it actually hands over.
/// </param>
/// <param name="Suppliers">
/// The business's own linked, not-hidden suppliers (issue #934), in the order they are published.
/// </param>
/// <param name="Products">
/// One entry per asked-for product that is <strong>this business's</strong>. An id of another
/// business simply is not here - the same answer an id that exists nowhere gives - so nothing in the
/// response confirms that it exists somewhere.
/// </param>
public sealed record CheapestSupplierFacts(
    bool BusinessIsGstRegistered,
    IReadOnlyList<LinkedSupplier> Suppliers,
    IReadOnlyList<TrackedProductListings> Products);

/// <summary>
/// The narrow persistence port behind the cheapest-supplier summary (issue #930, parent #817): one
/// read, over the caller's own products and suppliers and the shared catalogue rows their matched
/// mappings name.
///
/// <para><strong>It reads the platform price catalogue for the caller's own mappings only, and that
/// is the reviewed limit of this read</strong> (issue #930's third criterion). The master products,
/// supplier listings and price observations are platform tables - facts about products and about
/// suppliers' shelves, never about a business - and the way in is always one of this business's own
/// matched mappings. There is no method here that takes a master product id, a listing id, a
/// supplier preset id, a predicate, a column list, a page or a sort, so nothing can ask it about a
/// row no mapping of the caller's names. See AGENTS.md § Tenant ownership and data isolation and
/// docs/architecture.md § Tenant ownership.</para>
///
/// <para><strong>Nothing it answers identifies another business.</strong> A listing and an
/// observation carry a supplier, a pack and a price; a master product is a brand's product. There is
/// no count of who else tracks it and nowhere for one to travel, which is what makes "no business
/// learns what another is tracking" true by construction rather than by filtering.</para>
///
/// <para><strong>It is a read, and purchasing guidance.</strong> It writes nothing at all, and
/// nothing it answers is a recorded cost: an observed supplier price never becomes a purchase, a
/// unit cost, an inventory valuation, a GST accounting figure or a line of any report (AGENTS.md
/// § Observed supplier prices).</para>
/// </summary>
public interface ICheapestSupplierSummaryStore
{
    /// <summary>
    /// The facts behind the summary for <paramref name="productIds"/>.
    ///
    /// <para>The business is the one tenancy resolution produced from the authenticated actor's
    /// membership; it is passed in because the <c>Businesses</c> row carries no tenant filter of its
    /// own, exactly as <c>EfAvailableSupplierPresetStore</c> reads it. Every other statement goes
    /// through the central tenant query filters, so a product id of another business matches nothing
    /// and is absent from the answer rather than refused.</para>
    /// </summary>
    Task<CheapestSupplierFacts> ReadAsync(
        BusinessId businessId,
        IReadOnlyList<long> productIds,
        CancellationToken cancellationToken);
}
