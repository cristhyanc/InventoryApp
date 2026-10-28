using Inventory.Domain.Exceptions;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// Where one of a business's products has got to in the shared catalogue (issue #923, parent #817).
///
/// <para>The five states are answers, never labels on each other, and
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

    /// <summary>
    /// The business still wants this product priced, but the product's own name or unit label can no
    /// longer be searched for - today a unit label edited to blank or to the generic
    /// <c>"unit"</c>, or a name longer than the search text can hold (issue #938, repaired on review).
    ///
    /// <para><strong>It waits on the product, not on the catalogue, and nothing stands while it
    /// does.</strong> A mapping in this state names no master product and records no match source, so
    /// it supplies no price and is not eligible for a comparison; and it is not
    /// <see cref="Requested"/>, so the collector work list does not send its text - which would be the
    /// previous size, a search for a product the business no longer has. The mapping is kept rather
    /// than removed because the business never withdrew its request: correcting the product's own
    /// details captures them afresh and returns it to <see cref="Requested"/>, where matching may
    /// happen again.</para>
    /// </summary>
    AwaitingProductDetails = 5,
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
///
/// <para><strong>Since issue #938 this policy also holds the switch's own rules</strong>: which unit
/// labels a product may be tracked under at all, and what an edit of the product's name or unit label
/// does to a mapping that already exists. Both are here rather than in a use case or an adapter for
/// the reason the consistency rules are: a second place that decided which products may be tracked,
/// or what a rename means, would be a second answer.</para>
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
    /// The generic unit label every product the provider catalogue import creates carries, because
    /// <c>Product.Unit</c> defaults to it. It is a placeholder rather than a size, which is why
    /// <see cref="IsTrackableUnitLabel"/> treats it as no label at all.
    /// </summary>
    public const string GenericUnitLabel = "unit";

    /// <summary>
    /// What a caller whose product has no usable unit label is told - blank, or the generic default
    /// (issue #938).
    ///
    /// <para>One sentence for both cases, because they are one problem: the product does not say what
    /// size it comes in, so there is nothing to search a supplier's shelf for. "Coca-Cola Classic
    /// unit" matches nothing, and a mapping created from it would wait on a search that could never
    /// succeed - which is worse than refusing, because waiting looks like progress.</para>
    /// </summary>
    public const string UnitLabelNotSpecificMessage =
        "Supplier prices are searched for by the product's name and unit label, so this product needs "
            + "a real unit label such as \"375mL\" before tracking can be turned on.";

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
    /// do; <see cref="ProductPriceTrackingStatus.Requested"/>,
    /// <see cref="ProductPriceTrackingStatus.NotFound"/> and
    /// <see cref="ProductPriceTrackingStatus.AwaitingProductDetails"/> do not, and a mapping that is
    /// still <see cref="ProductPriceTrackingStatus.Requested"/> is exactly what the collector work
    /// list of #924 is a list of.
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

    /// <summary>
    /// Whether <paramref name="productUnit"/> says what size the product comes in, which is what
    /// decides whether it may be tracked at all (issue #938).
    ///
    /// <para>Two labels are refused and they are refused for one reason. A blank label says nothing,
    /// and <see cref="GenericUnitLabel"/> - the default every catalogue-imported product carries - says
    /// nothing either: it is a placeholder a person never chose. Everything else is accepted as typed,
    /// including <c>"can"</c> and <c>"units"</c>, because this rule is not in the business of deciding
    /// which real labels are good ones.</para>
    ///
    /// <para>The generic label is compared case-insensitively on the trimmed value, because
    /// <c>"Unit"</c> and <c>"  unit  "</c> are the same placeholder. The comparison is ordinal-ignore-case
    /// rather than culture-dependent, for the reason the key fold is invariant: the same label must mean
    /// the same thing on every machine that reads one.</para>
    /// </summary>
    public static bool IsTrackableUnitLabel(string? productUnit)
    {
        if (string.IsNullOrWhiteSpace(productUnit))
        {
            return false;
        }

        return !string.Equals(
            productUnit.Trim(),
            GenericUnitLabel,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Why this product may not be tracked, or <see langword="null"/> when it may (issue #938).
    ///
    /// <para>It is the one place the switch's eligibility is decided, so the sentence a refusal
    /// carries, the sentence the switch shows beside itself, and whether <see cref="Capture"/> accepts
    /// the product are all the same answer. The order is deliberate: a product with no name is told
    /// about its name, because "give it a unit label" is the wrong instruction for a product that has
    /// no name at all.</para>
    /// </summary>
    public static string? TrackingRefusal(string? productName, string? productUnit)
    {
        if (string.IsNullOrWhiteSpace(productName))
        {
            return SearchNameRequiredMessage;
        }

        if (productName.Trim().Length > MaxSearchNameLength)
        {
            return SearchNameTooLongMessage;
        }

        if (!IsTrackableUnitLabel(productUnit))
        {
            return UnitLabelNotSpecificMessage;
        }

        return productUnit!.Trim().Length > MaxUnitLabelLength ? UnitLabelTooLongMessage : null;
    }

    /// <summary>
    /// The search text and waiting key to store for one product: its own name and unit label,
    /// trimmed, with the key they fold to (issue #938, under the owner's decision of 27 October 2026).
    ///
    /// <para>This is the one way a mapping's text is built, which is what makes "the stored key is the
    /// fold of the stored text" true by construction rather than by every call site remembering to do
    /// it. A mapping whose key did not match its text would be searched for as one product and grouped
    /// as another.</para>
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// <see cref="TrackingRefusal"/> refuses the product, with that refusal's own sentence. Nothing is
    /// captured, so no path can store a mapping the switch itself would not have created.
    /// </exception>
    public static ProductPriceTrackingText Capture(string? productName, string? productUnit)
    {
        if (TrackingRefusal(productName, productUnit) is { } refusal)
        {
            throw new DomainValidationException(refusal);
        }

        var searchName = NormaliseSearchName(productName);
        var unitLabel = NormaliseUnitLabel(productUnit);

        return new ProductPriceTrackingText(
            searchName,
            unitLabel,
            PriceCatalogueWaitingKey.For(searchName, unitLabel));
    }

    /// <summary>
    /// <see cref="Capture"/> as an answer rather than an exception, for the one caller that has no
    /// refusal to report: the product edit, which refreshes a mapping as a side effect of a write it
    /// does not own (see <see cref="RefreshFor"/>).
    /// </summary>
    public static bool TryCapture(
        string? productName,
        string? productUnit,
        out ProductPriceTrackingText? text)
    {
        if (TrackingRefusal(productName, productUnit) is not null)
        {
            text = null;
            return false;
        }

        text = Capture(productName, productUnit);
        return true;
    }

    /// <summary>
    /// What an edit of a product's name or unit label does to the mapping it already has, or
    /// <see langword="null"/> when it does nothing at all (issue #938).
    ///
    /// <para><strong>One answer per state, and each of the first three is the issue's own
    /// criterion.</strong> A
    /// mapping still <see cref="ProductPriceTrackingStatus.Requested"/> follows the product: the
    /// search text and the waiting key both move, because the key is the fold of the text and a
    /// collector must look for what the business now calls the product. A
    /// <see cref="ProductPriceTrackingStatus.AutoMatched"/> or
    /// <see cref="ProductPriceTrackingStatus.Matched"/> mapping is returned to awaiting a match
    /// <em>only</em> by a change of the unit label, because the size the match rests on may no longer
    /// agree - a 375 ml can renamed is the same can, a 375 ml can that became a 1.25 l bottle is not.
    /// A name-only edit therefore keeps the match, and keeps the text the match was made from, which
    /// is what a reviewer of a wrong match needs.</para>
    ///
    /// <para><strong><see cref="ProductPriceTrackingStatus.NotFound"/> is left exactly as it is.</strong>
    /// It is a recorded answer rather than a gap (issue #923), and issue #938's criteria name only the
    /// awaiting and the matched states; re-running a search that already came back empty is precisely
    /// what recording that answer prevents. It names no master product and is not sent to the
    /// collector, so there is nothing stale for it to keep standing on either.</para>
    ///
    /// <para><strong>Text this policy cannot capture suspends the mapping; it never leaves a stale one
    /// standing</strong> (repaired on review of issue #938). A product renamed to something longer
    /// than <see cref="MaxSearchNameLength"/>, or relabelled to a blank or generic unit label, cannot
    /// be searched for at all - so the mapping goes to
    /// <see cref="ProductPriceTrackingStatus.AwaitingProductDetails"/>, its master product and match
    /// source are cleared by the applier, and the collector stops being sent its text. Keeping a
    /// match made on the <em>previous</em> size would let that master product's prices be read as this
    /// product's, which is the one thing a price comparison must never do, and keeping the row
    /// <see cref="ProductPriceTrackingStatus.Requested"/> would send the previous size's text to the
    /// collector - a search for a product the business no longer has.</para>
    ///
    /// <para><strong>The product edit is still never refused.</strong> Changing a product's own fields
    /// and their rules is issue #895's subject and explicitly outside issue #938, so this rule reports
    /// a state and refuses nothing: an otherwise valid edit of a tracked product succeeds exactly as
    /// it does for an untracked one. The stored text is left as it is while a mapping is suspended -
    /// inert, because nothing reads it in that state, and the row still records what was last searched
    /// for. Correcting the product's name or unit label captures the <em>current</em> values and
    /// returns the mapping to <see cref="ProductPriceTrackingStatus.Requested"/>, even when the
    /// corrected text is the text it was suspended with, because the match it had was invalidated and
    /// deciding the next one is the matcher's (#941) work rather than this rule's.</para>
    /// </summary>
    /// <param name="status">The mapping's current status.</param>
    /// <param name="storedSearchName">The search name it currently holds.</param>
    /// <param name="storedUnitLabel">The unit label it currently holds.</param>
    /// <param name="productName">The product's name as the edit is storing it.</param>
    /// <param name="productUnit">The product's unit label as the edit is storing it.</param>
    public static ProductPriceTrackingRefresh? RefreshFor(
        ProductPriceTrackingStatus status,
        string storedSearchName,
        string storedUnitLabel,
        string? productName,
        string? productUnit)
    {
        if (status == ProductPriceTrackingStatus.NotFound)
        {
            return null;
        }

        if (!TryCapture(productName, productUnit, out var captured))
        {
            // The product can no longer be searched for. A mapping that was waiting, auto-matched or
            // matched is suspended - the applier clears the master product and the match source with
            // it - and one that is already suspended stays exactly as it is. No new text is carried:
            // there is none this policy would store, and the stored text is read by nothing in this
            // state.
            return status == ProductPriceTrackingStatus.AwaitingProductDetails
                ? null
                : new ProductPriceTrackingRefresh(
                    ProductPriceTrackingStatus.AwaitingProductDetails,
                    Text: null);
        }

        if (status == ProductPriceTrackingStatus.AwaitingProductDetails)
        {
            // Corrected: the product's current name and unit label are captured afresh and the
            // mapping waits for a match again. Unconditionally, including when the corrected text is
            // the text it was suspended with - the status itself is the change, and the match it once
            // had was invalidated rather than remembered.
            return new ProductPriceTrackingRefresh(ProductPriceTrackingStatus.Requested, captured);
        }

        var unitLabelChanged = !string.Equals(
            captured!.UnitLabel,
            storedUnitLabel,
            StringComparison.Ordinal);
        var searchNameChanged = !string.Equals(
            captured.SearchName,
            storedSearchName,
            StringComparison.Ordinal);

        if (IsMatched(status))
        {
            // A name-only edit keeps the match, and with it the text the match was decided from.
            return unitLabelChanged
                ? new ProductPriceTrackingRefresh(ProductPriceTrackingStatus.Requested, captured)
                : null;
        }

        return unitLabelChanged || searchNameChanged
            ? new ProductPriceTrackingRefresh(status, captured)
            : null;
    }
}

/// <summary>
/// The text one mapping is searched for by, and the key it folds to (issue #938): the product's own
/// name and unit label as they are to be stored, with
/// <c>PriceCatalogueWaitingKey.For</c> of exactly those two values.
///
/// <para>The three travel together so the stored key cannot disagree with the stored text. It is
/// produced only by <see cref="ProductPriceTrackingPolicy.Capture"/>, which is why there is no
/// construction path that could build one from a key somebody else computed.</para>
/// </summary>
/// <param name="SearchName">The validated, trimmed search name.</param>
/// <param name="UnitLabel">The validated, trimmed unit label.</param>
/// <param name="WaitingKey">The fold of the two values above.</param>
public sealed record ProductPriceTrackingText(string SearchName, string UnitLabel, string WaitingKey);

/// <summary>
/// What a product edit changes on the mapping it already has (issue #938): the status the mapping
/// ends in, and the text it ends with.
///
/// <para>A mapping that is returned to awaiting a match carries
/// <see cref="ProductPriceTrackingStatus.Requested"/> here, and the master product and match source
/// it named are cleared by whoever applies this - which
/// <c>ProductPriceTrackingPolicy.RequireConsistentMatch</c> then checks, so an applier that forgot
/// cannot store a waiting mapping still pointing at a master product.</para>
///
/// <para><see cref="Text"/> is <see langword="null"/> for exactly one answer: a mapping suspended
/// into <see cref="ProductPriceTrackingStatus.AwaitingProductDetails"/>, whose product can no longer
/// be searched for and for which there is therefore no text to capture. The mapping keeps the text it
/// holds, which nothing reads in that state. Every other answer carries text, because a mapping that
/// is still searched for must be searched for by what the product says now.</para>
/// </summary>
/// <param name="Status">The status to store.</param>
/// <param name="Text">
/// The search text and waiting key to store, or <see langword="null"/> to leave the stored text alone.
/// </param>
public sealed record ProductPriceTrackingRefresh(
    ProductPriceTrackingStatus Status,
    ProductPriceTrackingText? Text);
