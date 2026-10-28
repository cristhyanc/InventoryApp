using Inventory.Domain.Exceptions;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// What an observed or calculated supplier price is a price <em>of</em> (issue #925, parent #817):
/// a price that includes GST, or one that does not.
///
/// <para>The basis is stored beside every observed price rather than assumed, because the suppliers
/// disagree: the supermarkets, Kmart and BIG W publish GST-inclusive prices and JB Metro publishes
/// ex-GST ones (<c>SupplierPreset.PricesIncludeGst</c>, issue #931). Comparing one with the other
/// without saying which is which is off by the GST rate - about a tenth of the answer - and it is the
/// one mistake a price comparison must not be able to make.</para>
///
/// <para>Numbered from 1, so a stored <c>0</c> is never read as the first declared basis.</para>
/// </summary>
public enum SupplierPriceBasis
{
    /// <summary>The price without GST.</summary>
    ExclusiveOfGst = 1,

    /// <summary>The price with GST included.</summary>
    InclusiveOfGst = 2,
}

/// <summary>
/// Whether the supplier said the listing could be bought when its price was observed (issue #925).
///
/// <para><see cref="Unknown"/> is a real answer and is deliberately not <see cref="InStock"/>: a
/// supplier that publishes a price and says nothing about availability has not said the product is
/// there, and a page that silently read "no answer" as "in stock" would send somebody to a shop for
/// nothing. It is also not <see cref="OutOfStock"/>, which is why only that one makes an offer
/// uncomparable - see <see cref="CheapestUnitOfferPolicy"/>.</para>
///
/// <para>Numbered from 1, for the reason every other persisted enum in this application is.</para>
/// </summary>
public enum SupplierStockStatus
{
    /// <summary>The supplier said it was available.</summary>
    InStock = 1,

    /// <summary>The supplier said it was not available.</summary>
    OutOfStock = 2,

    /// <summary>The supplier published a price and said nothing about availability.</summary>
    Unknown = 3,
}

/// <summary>
/// The rules of one observed supplier price (issue #925, parent #817): what the supplier published
/// for one listing at one instant, stored exactly as it was published.
///
/// <para><strong>A raw observation and a derived comparison are kept apart</strong>, the way
/// AGENTS.md § Nayax integration keeps imported facts apart from accounting values. This policy owns
/// what may be <em>stored</em>: the currency, the amounts, the basis the supplier publishes on, the
/// supplier's own GST rate when it gives one, the promotion and the stock answer. What any of it
/// means per vending unit is <see cref="CheapestUnitOfferPolicy"/>, which stores nothing.</para>
///
/// <para><strong>None of it is a cost.</strong> An observed supplier price is purchasing guidance: it
/// never becomes a purchase, a unit cost, an average cost, an inventory valuation, a GST figure or a
/// line of any report, and no policy here reads or writes one. The boundary is pinned by
/// <c>SupplierPriceObservationBoundaryTests</c>.</para>
/// </summary>
public static class SupplierPriceObservationPolicy
{
    /// <summary>
    /// The one currency this application stores an observed price in. Australian dollars, written as
    /// the ISO code, because every supplier in the master list publishes in it.
    ///
    /// <para>It is a stored column rather than an assumption so that a price whose currency is
    /// something else can be refused on arrival instead of being compared as though it were dollars.
    /// Accepting a second currency is a decision about conversion - which rate, as at when, and who
    /// decides - and nothing here invents one.</para>
    /// </summary>
    public const string Currency = "AUD";

    /// <summary>The longest accepted source reference: the collector's own id for the result it read.</summary>
    public const int MaxSourceReferenceLength = 200;

    /// <summary>
    /// The longest accepted source link. The same bound a supplier's website and a listing's URL
    /// carry, for the same reason: it is an address, not a document, and nothing fetches it.
    /// </summary>
    public const int MaxSourceUrlLength = SupplierListingPolicy.MaxUrlLength;

    /// <summary>What a caller who submitted a price in some other currency is told.</summary>
    public const string CurrencyNotSupportedMessage =
        "An observed supplier price must be in AUD.";

    /// <summary>
    /// What a caller who submitted a pack price that is not above zero is told. Zero is refused
    /// rather than stored, because a zero pack price divides into a zero unit price and compares as
    /// cheaper than every real offer.
    /// </summary>
    public const string PackPriceNotPositiveMessage =
        "An observed pack price must be greater than zero.";

    /// <summary>What a caller who submitted a regular price that is not above zero is told.</summary>
    public const string RegularPriceNotPositiveMessage =
        "An observed regular price must be greater than zero, or unknown.";

    /// <summary>
    /// What a caller who submitted a GST rate outside the accepted range is told. A rate is a
    /// proportion, not a percentage: 10% GST is <c>0.10</c>.
    /// </summary>
    public const string GstRateOutOfRangeMessage =
        "A supplier's GST rate must be a proportion between 0 and 1, or unknown.";

    /// <summary>
    /// What a caller who recorded a promotion end date on an observation that is not a promotion is
    /// told. An end date on an ordinary price describes nothing, and an offer whose promotion has
    /// ended is refused a comparison - so a stray date would silently withdraw a real price.
    /// </summary>
    public const string PromotionEndWithoutPromotionMessage =
        "A promotion end date belongs to a promotional price.";

    /// <summary>What a caller who submitted a source reference that is too long is told.</summary>
    public const string SourceReferenceTooLongMessage =
        "A source reference must be 200 characters or fewer.";

    /// <summary>What a caller who submitted a source link that is too long is told.</summary>
    public const string SourceUrlTooLongMessage =
        "A source link must be 2048 characters or fewer.";

    /// <summary>
    /// What a caller who chose a price basis that is not a declared one is told. Built from the
    /// vocabulary itself so it cannot drift from what is accepted.
    /// </summary>
    public static string UnknownPriceBasisMessage { get; } =
        "An observed price basis must be one of "
            + string.Join(", ", Enum.GetNames<SupplierPriceBasis>())
            + ".";

    /// <summary>What a caller who chose a stock status that is not a declared one is told.</summary>
    public static string UnknownStockStatusMessage { get; } =
        "An observed stock status must be one of "
            + string.Join(", ", Enum.GetNames<SupplierStockStatus>())
            + ".";

    /// <summary>Whether <paramref name="basis"/> is one of the declared bases.</summary>
    public static bool IsDeclaredBasis(SupplierPriceBasis basis) => Enum.IsDefined(basis);

    /// <summary>Whether <paramref name="status"/> is one of the declared stock answers.</summary>
    public static bool IsDeclaredStockStatus(SupplierStockStatus status) => Enum.IsDefined(status);

    /// <summary>
    /// The currency as it is to be stored: <see cref="Currency"/>, and nothing else.
    ///
    /// <para>A blank value is refused rather than defaulted to dollars. The collector knows what it
    /// read, and a price whose currency nobody recorded is not evidence that it was in AUD.</para>
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The currency is blank or is not <see cref="Currency"/>. Nothing is stored.
    /// </exception>
    public static string NormaliseCurrency(string? currency)
    {
        var trimmed = (currency ?? string.Empty).Trim();

        return string.Equals(trimmed, Currency, StringComparison.OrdinalIgnoreCase)
            ? Currency
            : throw new DomainValidationException(CurrencyNotSupportedMessage);
    }

    /// <summary>The observed pack price as it is to be stored: above zero, and otherwise untouched.</summary>
    /// <exception cref="DomainValidationException">The price is not above zero.</exception>
    public static decimal RequirePackPrice(decimal packPrice) =>
        packPrice > 0m
            ? packPrice
            : throw new DomainValidationException(PackPriceNotPositiveMessage);

    /// <summary>
    /// The regular (non-promotional) price as it is to be stored: above zero, or
    /// <see langword="null"/> when the supplier publishes only one price.
    ///
    /// <para>It is deliberately not defaulted to the pack price. "This is the normal price" and "we
    /// do not know what the normal price is" are different facts, and a promotion's saving cannot be
    /// read off a column that was filled in with the promotional price.</para>
    /// </summary>
    /// <exception cref="DomainValidationException">A value was given and it is not above zero.</exception>
    public static decimal? NormaliseRegularPrice(decimal? regularPrice) => regularPrice switch
    {
        null => null,
        > 0m => regularPrice,
        _ => throw new DomainValidationException(RegularPriceNotPositiveMessage),
    };

    /// <summary>
    /// The supplier's own GST rate as it is to be stored: a proportion between 0 and 1, or
    /// <see langword="null"/> when the supplier does not publish one.
    ///
    /// <para><strong>Unknown stays unknown.</strong> It is not defaulted to the standard rate and not
    /// defaulted to zero: an offer whose rate nobody knows is shown and not compared
    /// (<see cref="CheapestUnitOfferPolicy"/>), which is a real answer, while either default would be
    /// this application inventing a tax basis - exactly what AGENTS.md § Product GST rules and
    /// supplier defaults refuses to do from a supplier's registration or its invoice.</para>
    /// </summary>
    /// <exception cref="DomainValidationException">A value was given and it is not a proportion.</exception>
    public static decimal? NormaliseGstRate(decimal? gstRate) => gstRate switch
    {
        null => null,
        >= 0m and <= 1m => gstRate,
        _ => throw new DomainValidationException(GstRateOutOfRangeMessage),
    };

    /// <summary>
    /// The source reference as it is to be stored - the collector's own id for the result it read -
    /// trimmed, with blank stored as <see langword="null"/> so "no reference" has one spelling.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The reference is longer than <see cref="MaxSourceReferenceLength"/> once trimmed.
    /// </exception>
    public static string? NormaliseSourceReference(string? sourceReference) =>
        NormaliseOptionalText(
            sourceReference,
            MaxSourceReferenceLength,
            SourceReferenceTooLongMessage);

    /// <summary>
    /// The source link as it is to be stored: trimmed, with blank stored as <see langword="null"/>.
    /// Nothing fetches or resolves it; it is what a person follows to see the price for themselves.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The link is longer than <see cref="MaxSourceUrlLength"/> once trimmed.
    /// </exception>
    public static string? NormaliseSourceUrl(string? sourceUrl) =>
        NormaliseOptionalText(sourceUrl, MaxSourceUrlLength, SourceUrlTooLongMessage);

    /// <summary>
    /// Refuses an observation whose promotion flag and end date disagree: an end date belongs to a
    /// promotion. A promotion with no end date is accepted - a supplier that does not say when a
    /// special finishes is the ordinary case, and it is never treated as ended.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// An end date was recorded on an observation that is not a promotion. Nothing is stored.
    /// </exception>
    public static void RequireConsistentPromotion(bool isPromotion, DateTime? promotionEndsAtUtc)
    {
        if (!isPromotion && promotionEndsAtUtc is not null)
        {
            throw new DomainValidationException(PromotionEndWithoutPromotionMessage);
        }
    }

    /// <summary>
    /// Refuses a basis or a stock status that is not a declared one. Both arrive from a JSON body as
    /// arbitrary integers, so the vocabulary is a validation boundary and not only a compile-time
    /// constraint - the rule <c>Inventory.Domain.Gst.GstClassifications</c> states for a
    /// classification.
    /// </summary>
    /// <exception cref="DomainValidationException">Either value is outside its vocabulary.</exception>
    public static void RequireDeclaredVocabulary(SupplierPriceBasis basis, SupplierStockStatus stockStatus)
    {
        if (!IsDeclaredBasis(basis))
        {
            throw new DomainValidationException(UnknownPriceBasisMessage);
        }

        if (!IsDeclaredStockStatus(stockStatus))
        {
            throw new DomainValidationException(UnknownStockStatusMessage);
        }
    }

    private static string? NormaliseOptionalText(string? value, int maxLength, string tooLongMessage)
    {
        var trimmed = (value ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return null;
        }

        return trimmed.Length <= maxLength
            ? trimmed
            : throw new DomainValidationException(tooLongMessage);
    }
}
