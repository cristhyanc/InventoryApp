using Inventory.Domain.PriceCatalogue;

namespace InventoryApi.DTOs;

/// <summary>
/// The body of <c>POST /api/cheapest-suppliers/summary</c> (issue #930, parent #817): the products a
/// page is showing, and nothing else.
///
/// <para><strong>A POST only because of the id list.</strong> The request changes nothing - it is a
/// read with a body, because a page of up to 200 ids does not belong in a query string - and the
/// review finding of 27 October 2026 fixed both the method and the maximum. No business, supplier,
/// master product, basis or price is submittable: the business comes from the caller's membership,
/// and every figure is the server's.</para>
/// </summary>
/// <param name="ProductIds">
/// The caller's own product ids, 1 to 200 of them once duplicates are collapsed. An id of another
/// business is ignored rather than reported.
/// </param>
public record CheapestSupplierSummaryRequest(IReadOnlyList<long>? ProductIds);

/// <summary>
/// One supplier's answer for one product, as the shared cell and popup render it (issue #930).
///
/// <para><strong>Nothing is dropped and nothing is a zero.</strong> A supplier that does not list
/// the product, a listing nobody has checked yet and an offer that could not be compared are each
/// published with the state that says so, and a price that is not knowable is <c>null</c> rather
/// than <c>0</c> - which is a price (AGENTS.md § Frontend rules).</para>
///
/// <para><strong>Both GST bases travel with every unit price, and the frontend does no
/// arithmetic.</strong> <see cref="UnitPrice"/> is already on the business's own basis, decided by
/// <c>CheapestUnitOfferPolicy</c>, so a presentation formats and never divides, converts or picks a
/// cheapest of its own. The figures are unrounded; rounding belongs where something is displayed.</para>
/// </summary>
/// <param name="SupplierId">The business's own supplier.</param>
/// <param name="SupplierName">Its name, as the business holds it.</param>
/// <param name="HasListing">Whether this supplier lists the product at all - <c>false</c> is "no match".</param>
/// <param name="HasPrice">Whether a price has ever been observed for that listing.</param>
/// <param name="PackQuantity">How many items come in the pack, or <see langword="null"/>.</param>
/// <param name="PackPrice">What the pack cost, as observed, or <see langword="null"/>.</param>
/// <param name="PackPriceBasis">Whether that pack price includes GST, as observed.</param>
/// <param name="UnitPrice">The price per item on the business's own basis, or <see langword="null"/>.</param>
/// <param name="UnitPriceExGst">The same price without GST, or <see langword="null"/>.</param>
/// <param name="UnitPriceIncGst">The same price with GST, or <see langword="null"/>.</param>
/// <param name="IsSpecial">Whether the observed price was a promotion.</param>
/// <param name="PromotionEndsAtUtc">When that promotion finishes, or <see langword="null"/>.</param>
/// <param name="StockStatus">
/// What the supplier said about availability, or <see langword="null"/> when nothing has been
/// observed. <c>Unknown</c> is a real answer and is not "in stock".
/// </param>
/// <param name="LastCheckedAtUtc">When the listing was last checked successfully, or <see langword="null"/>.</param>
/// <param name="ProductUrl">The supplier's page for this product, or <see langword="null"/>.</param>
/// <param name="Ineligibility">
/// The one reason this offer was not compared, or <see langword="null"/> when it was - and when
/// there is no offer at all.
/// </param>
/// <param name="IsCheapest">
/// Whether this is one of the cheapest comparable offers. Every supplier in a tie carries it.
/// </param>
public record CheapestSupplierOfferResponse(
    int SupplierId,
    string SupplierName,
    bool HasListing,
    bool HasPrice,
    int? PackQuantity,
    decimal? PackPrice,
    SupplierPriceBasis? PackPriceBasis,
    decimal? UnitPrice,
    decimal? UnitPriceExGst,
    decimal? UnitPriceIncGst,
    bool IsSpecial,
    DateTime? PromotionEndsAtUtc,
    SupplierStockStatus? StockStatus,
    DateTime? LastCheckedAtUtc,
    string? ProductUrl,
    SupplierOfferIneligibility? Ineligibility,
    bool IsCheapest);

/// <summary>One product's cheapest-supplier answer (issue #930).</summary>
/// <param name="ProductId">The product, always one of the caller's own.</param>
/// <param name="TrackingStatus">
/// Where its price-tracking mapping has got to, or <see langword="null"/> when the business does not
/// track it. <c>AutoMatched</c> is what lets the popup say the match was made automatically and has
/// not been checked (#941).
/// </param>
/// <param name="CheapestUnitPrice">
/// The cheapest comparable unit price on the summary's display basis, or <see langword="null"/> when
/// nothing could be compared.
/// </param>
/// <param name="Suppliers">
/// Every one of the business's linked, not-hidden suppliers for a matched product, cheapest first.
/// Empty while the product is not matched, because nothing in the catalogue is this product yet.
/// </param>
public record CheapestSupplierProductResponse(
    long ProductId,
    ProductPriceTrackingStatus? TrackingStatus,
    decimal? CheapestUnitPrice,
    IReadOnlyList<CheapestSupplierOfferResponse> Suppliers);

/// <summary>
/// The cheapest-supplier summary for a page's worth of products (issue #930), as
/// <c>POST /api/cheapest-suppliers/summary</c> answers it.
/// </summary>
/// <param name="DisplayBasis">
/// The GST basis every <c>unitPrice</c> here is on and the cheapest was decided on: ex-GST for a
/// GST-registered business, inc-GST otherwise. It is published so a page can label the figures it
/// shows rather than deciding the basis itself.
/// </param>
/// <param name="Products">
/// One entry per asked-for product of the caller's own business, in the order they were asked for.
/// An id of another business is absent - never reported, and never an error that would confirm it
/// exists somewhere.
/// </param>
public record CheapestSupplierSummaryResponse(
    SupplierPriceBasis DisplayBasis,
    IReadOnlyList<CheapestSupplierProductResponse> Products);
