using Inventory.Domain.Exceptions;
using Inventory.Domain.Gst;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// Why one supplier's offer cannot take part in a cheapest-per-unit comparison (issue #925, parent
/// #817).
///
/// <para>Every one of these is a reason the comparison would otherwise be <em>wrong</em> rather than
/// merely incomplete, which is why an ineligible offer is still shown with its reason instead of
/// being dropped: an owner who cannot see that BIG W is out of stock will wonder why BIG W is not on
/// the list. Numbered from 1, like every other declared vocabulary here.</para>
/// </summary>
public enum SupplierOfferIneligibility
{
    /// <summary>
    /// The listing does not say how many items come in a pack, so there is no per-unit price to
    /// compare. Unknown stays unknown: 1 would read a carton's price as a single's, and 0 divides.
    /// </summary>
    PackSizeUnknown = 1,

    /// <summary>
    /// Nobody has confirmed that this listing is the product in question. A proposed or flagged match
    /// may be a different size, a different flavour or another product entirely, and pricing a
    /// product from it would be the matcher's guess presented as the cheapest supplier.
    /// </summary>
    ListingNotConfirmed = 2,

    /// <summary>
    /// Neither the supplier nor the product says what GST applies, so the offer's price cannot be
    /// stated on the basis the others are compared on. The offer is shown on the basis it was
    /// observed on and compared with nothing - the owner's decision of 27 October 2026.
    /// </summary>
    GstRateUnknown = 3,

    /// <summary>The supplier said it could not be bought. A price nobody can act on is not an offer.</summary>
    OutOfStock = 4,

    /// <summary>
    /// The price was a promotion and the promotion has finished. What it costs now is whatever the
    /// next check reads, not the special that expired.
    /// </summary>
    PromotionEnded = 5,

    /// <summary>
    /// The observation is older than this supplier's own freshness limit
    /// (<see cref="SupplierPriceFreshnessPolicy"/>). It is kept and shown - a failed or missing check
    /// never removes the last good price - and it stops being compared.
    /// </summary>
    Stale = 6,
}

/// <summary>
/// One candidate offer in a cheapest-per-unit comparison (issue #925): one supplier listing, the
/// latest accepted observation of its price, and the freshness limit of the supplier master record
/// that published it.
///
/// <para>Everything here is a stored fact. The policy that reads it derives, stores and rounds
/// nothing, so this record is the whole of what a comparison depends on - which is what makes the
/// comparison reproducible from the database afterwards.</para>
/// </summary>
/// <param name="SupplierListingId">The listing this offer is the current price of.</param>
/// <param name="ListingConfirmed">
/// Whether the listing's match to the product is confirmed - <c>SupplierListingMatchStatus.Confirmed</c>
/// and nothing else.
/// </param>
/// <param name="UnitsPerPack">
/// How many individual items the pack holds, or <see langword="null"/> when the supplier does not
/// say.
/// </param>
/// <param name="PackPrice">The observed price of the whole pack, on <paramref name="PriceBasis"/>.</param>
/// <param name="PriceBasis">Whether <paramref name="PackPrice"/> includes GST.</param>
/// <param name="SupplierGstRate">
/// The supplier's own GST rate for this product, when it publishes one, or <see langword="null"/>.
/// It takes precedence over the product's rule, because a supplier that states the tax on the line it
/// is selling is the better evidence about that line.
/// </param>
/// <param name="StockStatus">What the supplier said about availability when the price was observed.</param>
/// <param name="PromotionEndsAtUtc">
/// When the promotional price finishes, or <see langword="null"/> for an ordinary price and for a
/// promotion with no stated end.
/// </param>
/// <param name="ObservedAtUtc">When the price was observed.</param>
/// <param name="FreshnessLimitHours">
/// The supplier master record's own freshness limit, in hours. It is per supplier and comes from the
/// stored record, never from this policy - see <see cref="SupplierPriceFreshnessPolicy"/>.
/// </param>
public readonly record struct SupplierUnitOffer(
    long SupplierListingId,
    bool ListingConfirmed,
    int? UnitsPerPack,
    decimal PackPrice,
    SupplierPriceBasis PriceBasis,
    decimal? SupplierGstRate,
    SupplierStockStatus StockStatus,
    DateTime? PromotionEndsAtUtc,
    DateTime ObservedAtUtc,
    int FreshnessLimitHours);

/// <summary>
/// What one candidate offer costs per vending unit (issue #925), on both GST bases, and whether it
/// could be compared at all.
///
/// <para><strong>Both prices are published and neither is rounded.</strong> The displayed basis
/// follows the business (<c>Business.GstRegistered</c>), and a registered business that is shown
/// ex-GST prices still has the inc-GST figure to hand - so the UI never has to derive the other one
/// and cannot derive it differently. Rounding happens only where a figure is displayed.</para>
///
/// <para>A price is <see langword="null"/> when it is not knowable rather than when it is zero: no
/// pack size means no unit price at all, and an unknown GST rate means the offer is published only on
/// the basis it was observed on.</para>
/// </summary>
/// <param name="SupplierListingId">The listing this answer is about.</param>
/// <param name="UnitPriceExGst">The price of one item without GST, or <see langword="null"/>.</param>
/// <param name="UnitPriceIncGst">The price of one item with GST, or <see langword="null"/>.</param>
/// <param name="GstRate">
/// The rate the two prices were converted with - the supplier's own, else the product's - or
/// <see langword="null"/> when neither was known.
/// </param>
/// <param name="Ineligibility">
/// Why this offer was not compared, or <see langword="null"/> when it was. Exactly one reason is
/// reported, in the order the issue states them, so a page shows the first thing that has to change
/// rather than a list.
/// </param>
public readonly record struct SupplierUnitOfferPrice(
    long SupplierListingId,
    decimal? UnitPriceExGst,
    decimal? UnitPriceIncGst,
    decimal? GstRate,
    SupplierOfferIneligibility? Ineligibility)
{
    /// <summary>Whether this offer took part in the comparison.</summary>
    public bool IsComparable => Ineligibility is null;

    /// <summary>This offer's unit price on <paramref name="basis"/>, when it is known.</summary>
    public decimal? UnitPriceOn(SupplierPriceBasis basis) =>
        basis == SupplierPriceBasis.ExclusiveOfGst ? UnitPriceExGst : UnitPriceIncGst;
}

/// <summary>
/// The answer for one product: every candidate offer's per-unit price, and the cheapest of the ones
/// that could be compared (issue #925).
/// </summary>
/// <param name="ComparisonBasis">
/// The basis the cheapest was decided on, which is also the basis the business is shown. Deciding on
/// the displayed basis is deliberate: "cheapest" and "the smallest number on the page" must be the
/// same offer.
/// </param>
/// <param name="Offers">
/// Every candidate, comparable ones first in ascending unit price and the rest after them, each with
/// its reason. Nothing is dropped - an offer that cannot be compared is shown with why.
/// </param>
/// <param name="Cheapest">
/// The cheapest comparable offers. <strong>Every tie is returned</strong>: two suppliers at one price
/// is one answer with two suppliers in it, and picking one of them would be this policy inventing a
/// preference the owner did not state. Empty when nothing could be compared.
/// </param>
/// <param name="CheapestUnitPrice">
/// The cheapest unit price on <paramref name="ComparisonBasis"/>, unrounded, or
/// <see langword="null"/> when nothing could be compared - never zero, which is a price.
/// </param>
public readonly record struct CheapestUnitOffers(
    SupplierPriceBasis ComparisonBasis,
    IReadOnlyList<SupplierUnitOfferPrice> Offers,
    IReadOnlyList<SupplierUnitOfferPrice> Cheapest,
    decimal? CheapestUnitPrice);

/// <summary>
/// Which of a business's own suppliers is cheapest per vending unit for one product (issue #925,
/// parent #817), on the GST basis that business actually pays.
///
/// <para><strong>This is not <c>Inventory.Domain.Purchases.SupplierPriceComparisonPolicy</c>, and the
/// two must never be confused.</strong> That one (issue #63) compares the unit costs a business has
/// actually <em>paid</em>, from its own immutable purchase history, and feeds the product page's
/// lowest-versus-latest view. This one compares prices this application has <em>observed</em> on
/// suppliers' shelves, for packs the business has not bought, and feeds purchasing guidance only. The
/// names say which: a purchase price history against a cheapest unit offer. Nothing here reads a
/// purchase, a cost, an inventory quantity, a GST accounting figure or a report, and nothing here is
/// read by one.</para>
///
/// <para><strong>Pure, and the only calculation of its kind.</strong> It is given the stored facts and
/// an instant, and it returns an answer: no clock, no database, no configuration, and no rounding -
/// <c>pack price ÷ units per pack</c> at full decimal precision, converted between bases with the
/// rate that applies, and rounded only where something is displayed. A second implementation of this
/// arithmetic in a controller, an endpoint or an Angular component would be a second answer to "which
/// supplier is cheapest", which AGENTS.md § Architecture rules forbids for exactly this reason.</para>
///
/// <para><strong>The GST basis comes from two stored facts, in a fixed precedence</strong> (the
/// owner's decision of 27 October 2026). What the observed price <em>means</em> is the supplier
/// master record's own basis (<c>SupplierPreset.PricesIncludeGst</c>, issue #931), stored on each
/// observation as it was observed. What rate applies is the supplier's own per-product rate when it
/// publishes one, otherwise the product's GST rule (issue #430). If neither is known the offer is
/// shown and not compared, because the alternative - assuming a rate - would put a price on the wrong
/// side of the tax and name the wrong supplier cheapest.</para>
/// </summary>
public static class CheapestUnitOfferPolicy
{
    /// <summary>
    /// The Australian GST rate, as the proportion a rate is expressed in: 10%.
    ///
    /// <para>It is declared here, in purchasing guidance, and it deliberately calculates nothing in
    /// the accounting sense: <c>Inventory.Domain.Purchases.PurchaseGstPolicy</c> remains the one
    /// authoritative GST calculation over recorded purchases, and this constant never reaches it.
    /// Converting an observed shelf price between bases is a display conversion over a price nobody
    /// has paid.</para>
    /// </summary>
    public const decimal StandardGstRate = 0.10m;

    /// <summary>
    /// The rate a product's own GST rule implies: the standard rate for a taxable product, nothing at
    /// all for a GST-free one, and <see langword="null"/> when no rule is configured.
    ///
    /// <para><c>GstRules.None</c> is a real state (issue #430) and stays one here: a product nobody
    /// has classified is not a GST-free product, and reading it as one would compare a taxable
    /// product's ex-GST price with another's inclusive price.</para>
    /// </summary>
    public static decimal? RateForProductRule(GstClassification productGstRule) => productGstRule switch
    {
        GstClassification.Taxable => StandardGstRate,
        GstClassification.GstFree => 0m,
        _ => null,
    };

    /// <summary>
    /// The basis a business is shown, and therefore compared on: ex-GST for a GST-registered business,
    /// inc-GST otherwise (the owner's decision of 27 October 2026).
    ///
    /// <para>A registered business claims the GST back, so what it pays for a can is the ex-GST price;
    /// an unregistered one pays the shelf price. Both figures are always published - this chooses which
    /// one the comparison is decided on.</para>
    /// </summary>
    public static SupplierPriceBasis BasisFor(bool businessIsGstRegistered) =>
        businessIsGstRegistered
            ? SupplierPriceBasis.ExclusiveOfGst
            : SupplierPriceBasis.InclusiveOfGst;

    /// <summary>
    /// Compares <paramref name="offers"/> for one product as at <paramref name="asOfUtc"/>, on
    /// <paramref name="comparisonBasis"/>, with <paramref name="productGstRate"/> as the product's
    /// own rate where an offer does not carry one.
    ///
    /// <para>Every offer is answered - with both unit prices where they are knowable, and with the
    /// one reason it could not be compared where it could not - and the cheapest comparable offers are
    /// returned together, ties included.</para>
    ///
    /// <para>Candidacy itself is the caller's: only the business's own linked, not-hidden suppliers
    /// take part (issue #934), and this policy compares whatever it is handed.</para>
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// A rate that is not a proportion between 0 and 1, a basis that is not declared, or a freshness
    /// limit outside the accepted range. Nothing is calculated.
    /// </exception>
    public static CheapestUnitOffers Compare(
        IReadOnlyList<SupplierUnitOffer> offers,
        decimal? productGstRate,
        SupplierPriceBasis comparisonBasis,
        DateTime asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(offers);

        if (!SupplierPriceObservationPolicy.IsDeclaredBasis(comparisonBasis))
        {
            throw new DomainValidationException(SupplierPriceObservationPolicy.UnknownPriceBasisMessage);
        }

        SupplierPriceObservationPolicy.NormaliseGstRate(productGstRate);

        var priced = offers.Select(offer => Price(offer, productGstRate, asOfUtc)).ToList();

        var comparable = priced
            .Where(offer => offer.IsComparable)
            .OrderBy(offer => offer.UnitPriceOn(comparisonBasis))
            .ThenBy(offer => offer.SupplierListingId)
            .ToList();

        var ineligible = priced
            .Where(offer => !offer.IsComparable)
            .OrderBy(offer => offer.SupplierListingId)
            .ToList();

        var cheapestUnitPrice = comparable.Count == 0
            ? (decimal?)null
            : comparable[0].UnitPriceOn(comparisonBasis);

        // Every tie, in listing order. A decimal comparison treats 0.50 and 0.5 as one value, so two
        // suppliers at one price tie however their prices were scaled.
        var cheapest = comparable
            .Where(offer => offer.UnitPriceOn(comparisonBasis) == cheapestUnitPrice)
            .OrderBy(offer => offer.SupplierListingId)
            .ToList();

        return new CheapestUnitOffers(
            comparisonBasis,
            [.. comparable, .. ineligible],
            cheapest,
            cheapestUnitPrice);
    }

    private static SupplierUnitOfferPrice Price(
        SupplierUnitOffer offer,
        decimal? productGstRate,
        DateTime asOfUtc)
    {
        if (!SupplierPriceObservationPolicy.IsDeclaredBasis(offer.PriceBasis))
        {
            throw new DomainValidationException(SupplierPriceObservationPolicy.UnknownPriceBasisMessage);
        }

        SupplierPriceObservationPolicy.NormaliseGstRate(offer.SupplierGstRate);

        // The supplier's own rate for this product first, then the product's rule. Neither is a
        // default for the other: "unknown" survives both.
        var rate = offer.SupplierGstRate ?? productGstRate;

        var unitPriceAsObserved = offer.UnitsPerPack is > 0
            ? offer.PackPrice / offer.UnitsPerPack.Value
            : (decimal?)null;

        var (exGst, incGst) = Convert(unitPriceAsObserved, offer.PriceBasis, rate);

        return new SupplierUnitOfferPrice(
            offer.SupplierListingId,
            exGst,
            incGst,
            rate,
            IneligibilityOf(offer, rate, asOfUtc));
    }

    /// <summary>
    /// The two bases of one unit price: the observed one as it is, and the other one converted with
    /// <paramref name="rate"/>. When the rate is unknown only the observed basis is published, and
    /// when there is no unit price at all neither is.
    /// </summary>
    private static (decimal? ExGst, decimal? IncGst) Convert(
        decimal? unitPriceAsObserved,
        SupplierPriceBasis observedBasis,
        decimal? rate)
    {
        if (unitPriceAsObserved is null)
        {
            return (null, null);
        }

        var observed = unitPriceAsObserved.Value;

        if (rate is null)
        {
            return observedBasis == SupplierPriceBasis.ExclusiveOfGst
                ? (observed, null)
                : (null, observed);
        }

        return observedBasis == SupplierPriceBasis.ExclusiveOfGst
            ? (observed, observed * (1m + rate.Value))
            : (observed / (1m + rate.Value), observed);
    }

    /// <summary>
    /// The one reason <paramref name="offer"/> cannot be compared, in the order issue #925 states
    /// them, or <see langword="null"/> when it can.
    ///
    /// <para>The order is the issue's own, not a severity ranking: more than one reason may hold at
    /// once, and reporting the first in a fixed order keeps the answer deterministic and keeps a page
    /// from arguing with itself about which of two true statements to show.</para>
    /// </summary>
    private static SupplierOfferIneligibility? IneligibilityOf(
        SupplierUnitOffer offer,
        decimal? rate,
        DateTime asOfUtc)
    {
        if (offer.UnitsPerPack is not > 0)
        {
            return SupplierOfferIneligibility.PackSizeUnknown;
        }

        if (!offer.ListingConfirmed)
        {
            return SupplierOfferIneligibility.ListingNotConfirmed;
        }

        if (rate is null)
        {
            return SupplierOfferIneligibility.GstRateUnknown;
        }

        if (offer.StockStatus == SupplierStockStatus.OutOfStock)
        {
            return SupplierOfferIneligibility.OutOfStock;
        }

        // At the stated end instant the promotion has finished: a special that runs "until Sunday" is
        // not a price on Sunday evening. A promotion with no stated end never expires on its own.
        if (offer.PromotionEndsAtUtc is not null && offer.PromotionEndsAtUtc <= asOfUtc)
        {
            return SupplierOfferIneligibility.PromotionEnded;
        }

        return SupplierPriceFreshnessPolicy.IsFresh(offer.ObservedAtUtc, asOfUtc, offer.FreshnessLimitHours)
            ? null
            : SupplierOfferIneligibility.Stale;
    }
}
