using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;

namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// One supplier's answer for one product in a cheapest-supplier summary (issue #930, parent #817):
/// the business's own supplier, what it charges per vending unit, and - when it cannot be compared -
/// why.
///
/// <para><strong>Nothing is dropped.</strong> A supplier with no listing of the product, a listing
/// nobody has checked yet, and an offer that cannot take part in the comparison are all published
/// with the state that says so, because an owner who cannot see that BIG W is out of stock will
/// wonder why BIG W is not on the list (issue #925).</para>
///
/// <para><strong>Both GST bases travel with every unit price</strong>, and
/// <see cref="UnitPrice"/> is the one the business is shown and compared on. The display basis is
/// decided once, by <c>CheapestUnitOfferPolicy.BasisFor</c>, so "cheapest" and "the smallest number
/// on the page" cannot name different suppliers - and a presentation chooses nothing and divides
/// nothing.</para>
/// </summary>
/// <param name="SupplierId">The business's own supplier.</param>
/// <param name="SupplierName">Its name, as the business holds it.</param>
/// <param name="HasListing">
/// Whether this supplier lists the product at all. <see langword="false"/> is "no match" - the
/// supplier is published with nothing else, rather than left out.
/// </param>
/// <param name="HasPrice">
/// Whether a price has ever been observed for that listing. <see langword="false"/> is "not checked
/// yet", which is not a missing supplier and not a price of zero.
/// </param>
/// <param name="PackQuantity">How many items the pack holds, or <see langword="null"/> when unknown.</param>
/// <param name="PackPrice">
/// What the whole pack cost, exactly as observed, or <see langword="null"/>. It is published on
/// <paramref name="PackPriceBasis"/> and is deliberately not restated on the other basis: the one
/// authoritative conversion is per unit, and a second one here would be a second answer.
/// </param>
/// <param name="PackPriceBasis">Whether <paramref name="PackPrice"/> includes GST, as observed.</param>
/// <param name="UnitPrice">
/// The price of one item on the business's own basis, unrounded, or <see langword="null"/> when it
/// is not knowable. Rounding happens only where it is displayed.
/// </param>
/// <param name="UnitPriceExGst">The same price without GST, or <see langword="null"/>.</param>
/// <param name="UnitPriceIncGst">The same price with GST, or <see langword="null"/>.</param>
/// <param name="IsSpecial">Whether the observed price was a promotion rather than the ordinary price.</param>
/// <param name="PromotionEndsAtUtc">When that promotion finishes, or <see langword="null"/>.</param>
/// <param name="StockStatus">
/// What the supplier said about availability when the price was read, or <see langword="null"/> when
/// there is no observation. <c>Unknown</c> is a real answer and is not "in stock".
/// </param>
/// <param name="LastCheckedAtUtc">
/// When the listing was last checked successfully, or <see langword="null"/> while it never has been.
/// </param>
/// <param name="ProductUrl">The supplier's page for this product, or <see langword="null"/>.</param>
/// <param name="Ineligibility">
/// The one reason this offer was not compared, or <see langword="null"/> when it was - and also
/// <see langword="null"/> when there is no offer to compare at all, which
/// <paramref name="HasListing"/> and <paramref name="HasPrice"/> already say.
/// </param>
/// <param name="IsCheapest">
/// Whether this offer is one of the cheapest comparable ones. <strong>Every tie carries it</strong>:
/// two suppliers at one price is one answer with two suppliers in it.
/// </param>
public sealed record SupplierUnitPriceOffer(
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

/// <summary>
/// The cheapest-supplier answer for one of the caller's products (issue #930).
/// </summary>
/// <param name="ProductId">The product, which is always one of the caller's own.</param>
/// <param name="TrackingStatus">
/// Where its mapping has got to, or <see langword="null"/> when the business does not track it. The
/// two matched states are what a price can exist for;
/// <c>ProductPriceTrackingStatus.AutoMatched</c> in particular is published so a page can say the
/// match was made automatically and not yet checked (#941).
/// </param>
/// <param name="CheapestUnitPrice">
/// The cheapest comparable unit price on the summary's display basis, unrounded, or
/// <see langword="null"/> when nothing could be compared - never zero, which is a price.
/// </param>
/// <param name="Suppliers">
/// Every one of the business's linked, not-hidden suppliers for a matched product - comparable
/// offers first in ascending unit price, then the offers that could not be compared, then the
/// listings nobody has checked, then the suppliers that do not list it. Empty while the product is
/// not matched, because there is no master product whose listings they would be.
/// </param>
public sealed record CheapestSupplierProductSummary(
    long ProductId,
    ProductPriceTrackingStatus? TrackingStatus,
    decimal? CheapestUnitPrice,
    IReadOnlyList<SupplierUnitPriceOffer> Suppliers);

/// <summary>
/// The cheapest-supplier summary for a page's worth of products (issue #930, parent #817).
/// </summary>
/// <param name="DisplayBasis">
/// The GST basis every <c>UnitPrice</c> here is on, and the basis the cheapest was decided on: ex-GST
/// for a GST-registered business, inc-GST otherwise.
/// </param>
/// <param name="Products">
/// One entry per asked-for product of the caller's own business. An id of another business is absent
/// rather than reported.
/// </param>
public sealed record CheapestSupplierSummary(
    SupplierPriceBasis DisplayBasis,
    IReadOnlyList<CheapestSupplierProductSummary> Products);

/// <summary>
/// What one cheapest-supplier summary request may ask for (issue #930, under the review finding of
/// 27 October 2026 that fixed the method and the batch size).
///
/// <para>The rule is here, in one place, rather than as attributes on a DTO: the three pages that
/// call this endpoint split their rows into batches by the same number, and a limit that was stated
/// once in the contract and again in a component would be two limits.</para>
/// </summary>
public static class CheapestSupplierSummaryRequestPolicy
{
    /// <summary>
    /// The most products one request may ask about. A page shows at most a screenful of rows, and a
    /// caller with more splits them; the bound exists so one request cannot ask for a whole
    /// catalogue's worth of catalogue joins.
    /// </summary>
    public const int MaxProductIds = 200;

    /// <summary>What a caller who asked about nothing is told.</summary>
    public const string NoProductIdsMessage =
        "A cheapest-supplier summary needs at least one product id.";

    /// <summary>What a caller who asked about too many products is told.</summary>
    public const string TooManyProductIdsMessage =
        "A cheapest-supplier summary accepts at most 200 product ids in one request.";

    /// <summary>
    /// The ids to read, each once, in the order they were first asked for.
    ///
    /// <para>Duplicates are one id: a page that listed a product twice is asking one question about
    /// it, and answering twice would publish the same product as two rows. The limit is applied to
    /// what is actually read, after that collapse, so a repeated id never costs a caller its
    /// allowance.</para>
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// No ids at all, or more than <see cref="MaxProductIds"/> distinct ones. Nothing is read.
    /// </exception>
    public static IReadOnlyList<long> Accept(IReadOnlyList<long>? productIds)
    {
        var distinct = productIds is null ? [] : productIds.Distinct().ToList();

        return distinct.Count switch
        {
            0 => throw new DomainValidationException(NoProductIdsMessage),
            > MaxProductIds => throw new DomainValidationException(TooManyProductIdsMessage),
            _ => distinct,
        };
    }
}

/// <summary>
/// Which of the business's own suppliers is cheapest per vending unit for each product on a page
/// (issue #930, parent #817) - the one summary the Dashboard's Reorder Alerts (#939), Needs Ordering
/// and Products (#940) all read, so the three pages cannot drift.
///
/// <para><strong>Every comparison is <c>CheapestUnitOfferPolicy</c>'s</strong> (issue #925). This use
/// case reads stored facts, hands them to that policy, and arranges its answer; it divides nothing,
/// converts nothing between GST bases, rounds nothing and picks no cheapest of its own. A second
/// implementation of that arithmetic - here, in a controller or in an Angular component - would be a
/// second answer to "which supplier is cheapest" (AGENTS.md § Architecture rules).</para>
///
/// <para><strong>It is purchasing guidance and never a recorded cost.</strong> An observed supplier
/// price is a price of a pack the business has not bought; it never becomes a purchase, a unit cost,
/// an average cost, an inventory valuation, a GST accounting figure or a line of any report
/// (AGENTS.md § Observed supplier prices). <c>Inventory.Domain.Purchases.SupplierPriceComparisonPolicy</c>
/// remains the separate, unchanged comparison over what the business actually paid.</para>
///
/// <para><strong>Which business is never asked.</strong> It comes from the authenticated actor's
/// membership through <see cref="ICurrentBusinessProvider"/>; the request carries product ids and
/// nothing else, and an id of another business matches nothing and is absent from the answer rather
/// than refused - the same answer an id that exists nowhere gives.</para>
/// </summary>
public sealed class GetCheapestSupplierSummary
{
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly ICheapestSupplierSummaryStore _store;
    private readonly IClock _clock;

    public GetCheapestSupplierSummary(
        ICurrentBusinessProvider currentBusiness,
        ICheapestSupplierSummaryStore store,
        IClock clock)
    {
        _currentBusiness = currentBusiness;
        _store = store;
        _clock = clock;
    }

    /// <exception cref="DomainValidationException">
    /// The request asked about no products, or about more than the accepted number. Nothing is read.
    /// </exception>
    /// <exception cref="BusinessAccessDeniedException">
    /// The caller has no usable membership, so there is no business to answer for.
    /// </exception>
    public async Task<CheapestSupplierSummary> Handle(
        IReadOnlyList<long>? productIds,
        CancellationToken cancellationToken)
    {
        var accepted = CheapestSupplierSummaryRequestPolicy.Accept(productIds);

        var businessId = await _currentBusiness.RequireBusinessIdAsync(cancellationToken)
            .ConfigureAwait(false);

        var facts = await _store.ReadAsync(businessId, accepted, cancellationToken).ConfigureAwait(false);

        // The basis is decided once, from one stored fact about the business, and every product in
        // this answer is compared on it.
        var basis = CheapestUnitOfferPolicy.BasisFor(facts.BusinessIsGstRegistered);
        var asOfUtc = _clock.UtcNow;

        var suppliersByPreset = facts.Suppliers.ToDictionary(supplier => supplier.SupplierPresetId);

        return new CheapestSupplierSummary(
            basis,
            [.. facts.Products.Select(product => Summarise(product, facts.Suppliers, suppliersByPreset, basis, asOfUtc))]);
    }

    private static CheapestSupplierProductSummary Summarise(
        TrackedProductListings product,
        IReadOnlyList<LinkedSupplier> suppliers,
        IReadOnlyDictionary<int, LinkedSupplier> suppliersByPreset,
        SupplierPriceBasis basis,
        DateTime asOfUtc)
    {
        // A listing with no observation is not a candidate: there is no price to compare, and a
        // missing check is not a missing price (issue #925). It is published below as "not checked
        // yet" instead, which is a different answer from "this supplier does not list it".
        var priced = product.Listings.Where(listing => listing.Price is not null).ToList();

        var comparison = CheapestUnitOfferPolicy.Compare(
            [.. priced.Select(Candidate)],
            CheapestUnitOfferPolicy.RateForProductRule(product.ProductGstRule),
            basis,
            asOfUtc);

        var pricedByListing = priced.ToDictionary(listing => listing.SupplierListingId);
        var cheapest = comparison.Cheapest.Select(offer => offer.SupplierListingId).ToHashSet();

        // The policy's own order: comparable offers first in ascending unit price, then the ones
        // that could not be compared. Arranging them a second way here would be a second opinion
        // about which supplier to look at first.
        var offers = comparison.Offers
            .Where(offer => pricedByListing.ContainsKey(offer.SupplierListingId))
            .Select(offer => Offer(
                pricedByListing[offer.SupplierListingId],
                offer,
                suppliersByPreset,
                basis,
                cheapest.Contains(offer.SupplierListingId)))
            .ToList();

        // Then the listings nobody has checked yet, and finally the suppliers that do not list this
        // product at all - both by name, because neither has a price to be ordered by.
        var notChecked = product.Listings
            .Where(listing => listing.Price is null)
            .Select(listing => Unpriced(listing, suppliersByPreset))
            .OrderBy(offer => offer.SupplierName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(offer => offer.SupplierId)
            .ToList();

        var listed = product.Listings
            .Select(listing => listing.SupplierPresetId)
            .ToHashSet();

        var unlisted = suppliers
            .Where(supplier => !listed.Contains(supplier.SupplierPresetId))
            .Select(NoListing)
            .OrderBy(offer => offer.SupplierName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(offer => offer.SupplierId)
            .ToList();

        // A product nobody has matched to a master product has no supplier rows at all - not a list
        // of suppliers that every one of them says "no match" about. There is nothing the catalogue
        // could have been asked about yet, and the page says "awaiting match" instead.
        var matched = product.TrackingStatus is { } status
            && ProductPriceTrackingPolicy.IsMatched(status);

        IReadOnlyList<SupplierUnitPriceOffer> rows = matched
            ? [.. offers, .. notChecked, .. unlisted]
            : [];

        return new CheapestSupplierProductSummary(
            product.ProductId,
            product.TrackingStatus,
            comparison.CheapestUnitPrice,
            rows);
    }

    private static SupplierUnitOffer Candidate(TrackedListingPrice listing)
    {
        var price = listing.Price!;

        return new SupplierUnitOffer(
            listing.SupplierListingId,
            listing.ListingConfirmed,
            listing.UnitsPerPack,
            price.PackPrice,
            price.PriceBasis,
            price.SupplierGstRate,
            price.StockStatus,
            price.PromotionEndsAtUtc,
            price.ObservedAtUtc,
            price.FreshnessLimitHours);
    }

    private static SupplierUnitPriceOffer Offer(
        TrackedListingPrice listing,
        SupplierUnitOfferPrice answer,
        IReadOnlyDictionary<int, LinkedSupplier> suppliersByPreset,
        SupplierPriceBasis basis,
        bool isCheapest)
    {
        var supplier = suppliersByPreset[listing.SupplierPresetId];
        var price = listing.Price!;

        return new SupplierUnitPriceOffer(
            supplier.SupplierId,
            supplier.Name,
            HasListing: true,
            HasPrice: true,
            listing.UnitsPerPack,
            price.PackPrice,
            price.PriceBasis,
            answer.UnitPriceOn(basis),
            answer.UnitPriceExGst,
            answer.UnitPriceIncGst,
            price.IsPromotion,
            price.PromotionEndsAtUtc,
            price.StockStatus,
            listing.LastCheckedAtUtc,
            listing.ProductUrl,
            answer.Ineligibility,
            isCheapest);
    }

    private static SupplierUnitPriceOffer Unpriced(
        TrackedListingPrice listing,
        IReadOnlyDictionary<int, LinkedSupplier> suppliersByPreset)
    {
        var supplier = suppliersByPreset[listing.SupplierPresetId];

        return new SupplierUnitPriceOffer(
            supplier.SupplierId,
            supplier.Name,
            HasListing: true,
            HasPrice: false,
            listing.UnitsPerPack,
            PackPrice: null,
            PackPriceBasis: null,
            UnitPrice: null,
            UnitPriceExGst: null,
            UnitPriceIncGst: null,
            IsSpecial: false,
            PromotionEndsAtUtc: null,
            StockStatus: null,
            listing.LastCheckedAtUtc,
            listing.ProductUrl,
            Ineligibility: null,
            IsCheapest: false);
    }

    private static SupplierUnitPriceOffer NoListing(LinkedSupplier supplier) =>
        new(
            supplier.SupplierId,
            supplier.Name,
            HasListing: false,
            HasPrice: false,
            PackQuantity: null,
            PackPrice: null,
            PackPriceBasis: null,
            UnitPrice: null,
            UnitPriceExGst: null,
            UnitPriceIncGst: null,
            IsSpecial: false,
            PromotionEndsAtUtc: null,
            StockStatus: null,
            LastCheckedAtUtc: null,
            ProductUrl: null,
            Ineligibility: null,
            IsCheapest: false);
}
