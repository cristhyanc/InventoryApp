using Inventory.Domain.Exceptions;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// Where one of a business's products has got to in the shared catalogue (issue #923, parent #817).
///
/// <para>The four states are answers, never labels on each other, and
/// <see cref="ProductPriceTrackingStatus.NotFound"/> in particular is a recorded outcome rather than
/// a gap: a product nothing in the catalogue matches has been looked for and not found, which is what
/// stops it being searched for again every run. Numbered from 1.</para>
/// </summary>
public enum ProductPriceTrackingStatus
{
    /// <summary>
    /// The business asked for this product to be priced (#938) and nothing has matched it yet. The
    /// collector searches with the recorded search text while a mapping is in this state.
    /// </summary>
    Requested = 1,

    /// <summary>The automatic matcher (#941) chose a master product and nobody has reviewed it.</summary>
    AutoMatched = 2,

    /// <summary>The master product is settled - a person decided it, or an automatic match was accepted.</summary>
    Matched = 3,

    /// <summary>
    /// Looked for and not found: no master product describes this product. A real answer, and the
    /// reason the search text is not re-run forever.
    /// </summary>
    NotFound = 4,
}

/// <summary>
/// How a mapping's master product came to be chosen (issue #923, parent #817). Recorded because
/// "who decided this?" is the first question asked of a product that is being priced as something
/// else. Numbered from 1.
/// </summary>
public enum ProductPriceTrackingMatchSource
{
    /// <summary>The collector's automatic match (#941).</summary>
    CollectorAutomaticMatch = 1,

    /// <summary>A Super Admin, through the matching and review surface (#928).</summary>
    SuperAdmin = 2,
}

/// <summary>
/// The waiting key: what a business's product is searched for by, and what every row that is still
/// waiting on a match is grouped by (issue #923, parent #817).
///
/// <para><strong>It is the deduplication.</strong> The parent issue exists because one product must
/// not be searched for once per business - "we can't search Coca-Cola 375 five times because we have
/// five businesses" (the owner, 27 October 2026) - and this key is how five businesses' five rows
/// become one search. Two businesses whose product names and unit labels differ only in case or
/// spacing produce one key, so the collector work list of #924 holds one entry.</para>
///
/// <para><strong>It carries no business identity, by construction.</strong> The key is built from a
/// product's own name and unit label and from nothing else: no business id, no product id, no
/// membership, nothing a request supplied. That is what makes a work list of waiting keys something
/// the collector can be handed without being told whose products they are.</para>
/// </summary>
public static class PriceCatalogueWaitingKey
{
    /// <summary>
    /// The longest key <see cref="For"/> can produce, which is what bounds every column that stores
    /// one - the mapping's, the supplier listing's two, and the match suggestion's. Computed from the
    /// part limits rather than guessed; see <see cref="PriceCatalogueKey.MaxLengthOfParts"/>.
    /// </summary>
    public static int MaxLength { get; } = PriceCatalogueKey.MaxLengthOfParts(
        ProductPriceTrackingPolicy.MaxSearchNameLength,
        ProductPriceTrackingPolicy.MaxUnitLabelLength);

    /// <summary>
    /// The waiting key of one search text: the product's name and its unit label, folded by
    /// <see cref="PriceCatalogueKey"/> so case and spacing do not make two keys of one product.
    ///
    /// <para>A product with no unit label produces a key with an empty second part rather than a key
    /// of the name alone, so "no unit label" has exactly one spelling and cannot collide with a
    /// product whose unit label happens to be the next part's text.</para>
    /// </summary>
    public static string For(string? searchName, string? unitLabel) =>
        PriceCatalogueKey.Of(searchName, unitLabel);
}

/// <summary>
/// The rules of one business's price-tracking mapping (issue #923, parent #817): which of its own
/// products it wants priced, which shared master product each one is, and the search text the
/// collector uses until that is decided.
///
/// <para><strong>This is the one tenant-owned table in the shared catalogue, and it is the only
/// place a business appears in it at all.</strong> The master products, the supplier listings and the
/// match suggestions are platform data about products; the mapping is the business's own statement
/// that it wants one of <em>its</em> products priced. Which is why it is ordinary tenant-owned data -
/// stamped, filtered and enforced centrally like a purchase or a stock adjustment - rather than
/// anything new: see AGENTS.md § Tenant ownership and data isolation.</para>
///
/// <para><strong>The search text is captured, not derived on demand.</strong> It is taken from the
/// product's own <c>Name</c> and unit label when tracking is turned on (the owner's decision of
/// 27 October 2026), and stored as plain text beside the mapping. A product that is later renamed
/// therefore keeps being searched for under the text the business was shown, rather than silently
/// changing what a collector looks for - and the stored text is what makes a work list of searches
/// readable without reaching into anybody's catalogue.</para>
///
/// <para><strong>Nothing here reads or writes a price, a cost or a quantity.</strong> A mapping says
/// what a product <em>is</em>; what it costs at a supplier is #925's subject, and what the business
/// paid for it is its own purchase history, which this issue does not touch.</para>
/// </summary>
public static class ProductPriceTrackingPolicy
{
    /// <summary>
    /// The longest accepted search name, measured on the trimmed value. A product name longer than
    /// this is refused rather than truncated: a truncated search is a search for something else, and
    /// a silently shortened name is exactly the kind of quiet wrong answer a price comparison must
    /// not be built on.
    /// </summary>
    public const int MaxSearchNameLength = 400;

    /// <summary>The longest accepted unit label, measured on the trimmed value.</summary>
    public const int MaxUnitLabelLength = 100;

    /// <summary>What a caller whose product has no usable name is told.</summary>
    public const string SearchNameRequiredMessage =
        "A tracked product needs a name to search for.";

    /// <summary>What a caller whose product name is longer than the limit is told.</summary>
    public const string SearchNameTooLongMessage =
        "A tracked product's name must be 400 characters or fewer.";

    /// <summary>What a caller whose unit label is longer than the limit is told.</summary>
    public const string UnitLabelTooLongMessage =
        "A tracked product's unit label must be 100 characters or fewer.";

    /// <summary>
    /// What a caller is told when the status and the master product disagree: a matched mapping with
    /// no master product, or an unmatched one that names one.
    /// </summary>
    public const string MasterProductStatusMismatchMessage =
        "A price-tracking mapping names a master product exactly when it is matched.";

    /// <summary>
    /// What a caller is told when the master product and the match source disagree. How a match was
    /// made is recorded for every match and for nothing else - a source with no master product
    /// records how an undecided mapping was decided.
    /// </summary>
    public const string MatchSourceMismatchMessage =
        "A price-tracking mapping records how the match was made exactly when it names a master "
            + "product.";

    /// <summary>Whether <paramref name="status"/> is one of the declared states.</summary>
    public static bool IsDeclaredStatus(ProductPriceTrackingStatus status) => Enum.IsDefined(status);

    /// <summary>Whether <paramref name="source"/> is one of the declared sources, or none at all.</summary>
    public static bool IsDeclaredMatchSource(ProductPriceTrackingMatchSource? source) =>
        source is null || Enum.IsDefined(source.Value);

    /// <summary>
    /// Whether a mapping in <paramref name="status"/> names a master product. The two matched states
    /// do; <see cref="ProductPriceTrackingStatus.Requested"/> and
    /// <see cref="ProductPriceTrackingStatus.NotFound"/> do not, and a mapping that is still waiting
    /// is exactly what the collector work list of #924 is a list of.
    /// </summary>
    public static bool IsMatched(ProductPriceTrackingStatus status) =>
        status is ProductPriceTrackingStatus.AutoMatched or ProductPriceTrackingStatus.Matched;

    /// <summary>
    /// The search name as it is to be stored: the product's own name, trimmed.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The name is blank, or longer than <see cref="MaxSearchNameLength"/> once trimmed.
    /// </exception>
    public static string NormaliseSearchName(string? productName)
    {
        if (string.IsNullOrWhiteSpace(productName))
        {
            throw new DomainValidationException(SearchNameRequiredMessage);
        }

        var trimmed = productName.Trim();

        return trimmed.Length <= MaxSearchNameLength
            ? trimmed
            : throw new DomainValidationException(SearchNameTooLongMessage);
    }

    /// <summary>
    /// The unit label as it is to be stored: the product's own <c>Unit</c>, trimmed, and the empty
    /// string when it has none.
    ///
    /// <para>Empty rather than <see langword="null"/> on purpose. <c>Product.Unit</c> is nullable, and
    /// the waiting key is built from both parts, so "no unit label" must have exactly one spelling -
    /// otherwise a product with <see langword="null"/> and a product with <c>""</c> would be two
    /// searches for one thing, which is the duplication this whole key exists to prevent.</para>
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The label is longer than <see cref="MaxUnitLabelLength"/> once trimmed.
    /// </exception>
    public static string NormaliseUnitLabel(string? productUnit)
    {
        var trimmed = (productUnit ?? string.Empty).Trim();

        return trimmed.Length <= MaxUnitLabelLength
            ? trimmed
            : throw new DomainValidationException(UnitLabelTooLongMessage);
    }

    /// <summary>
    /// Refuses a mapping whose status, master product and match source do not agree with each other.
    ///
    /// <para>Two rules, and each exists because the alternative is a mapping nobody can read: a
    /// matched mapping must name the master product it was matched to, and a mapping that names one
    /// must record how that was decided. An undecided mapping names neither - it carries the waiting
    /// key and the search text instead, which is what the collector work list reads.</para>
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The status is not a declared one, the source is not a declared one, or the three disagree.
    /// Nothing is stored.
    /// </exception>
    public static void RequireConsistentMatch(
        ProductPriceTrackingStatus status,
        long? masterProductId,
        ProductPriceTrackingMatchSource? matchSource)
    {
        if (!IsDeclaredStatus(status))
        {
            throw new DomainValidationException(
                "A price-tracking status must be one of "
                    + string.Join(", ", Enum.GetNames<ProductPriceTrackingStatus>())
                    + ".");
        }

        if (!IsDeclaredMatchSource(matchSource))
        {
            throw new DomainValidationException(
                "A price-tracking match source must be one of "
                    + string.Join(", ", Enum.GetNames<ProductPriceTrackingMatchSource>())
                    + ", or none at all.");
        }

        if (IsMatched(status) != (masterProductId is not null))
        {
            throw new DomainValidationException(MasterProductStatusMismatchMessage);
        }

        if ((masterProductId is not null) != (matchSource is not null))
        {
            throw new DomainValidationException(MatchSourceMismatchMessage);
        }
    }
}
