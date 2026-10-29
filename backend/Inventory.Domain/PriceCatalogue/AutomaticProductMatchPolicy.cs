using System.Globalization;
using System.Text;

using Inventory.Domain.Exceptions;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// How sure the matcher says it is about one proposal (issue #941, parent #817): a closed vocabulary
/// of three levels, deliberately not a percentage.
///
/// <para><strong>Claude's own confidence is not calibrated</strong> (the owner, 27 October 2026), so
/// "obvious" is decided by the backend checks of <see cref="AutomaticProductMatchPolicy"/> and the
/// level is only ever one more condition among them: nothing is applied below
/// <see cref="High"/>, and <see cref="High"/> on its own applies nothing. A number would have invited
/// a threshold to be tuned, which is precisely the decision this vocabulary takes away.</para>
///
/// <para>Numbered from 1, like every other persisted enum in this application.</para>
/// </summary>
public enum ProductMatchConfidence
{
    /// <summary>The matcher is sure. A necessary condition of an automatic match, never a sufficient one.</summary>
    High = 1,

    /// <summary>The matcher thinks so. A tentative match for the Super Admin (#928).</summary>
    Medium = 2,

    /// <summary>The matcher is guessing. A tentative match as well, and marked as the guess it is.</summary>
    Low = 3,
}

/// <summary>
/// What a size is measured <em>in</em>, once the unit it was written in has been converted away
/// (issue #941): volume, weight or a count of things.
///
/// <para>It exists because "375 ml" and "0.375 l" are the same size and "375 ml" and "375 g" are not.
/// Comparing the stored unit codes would have made the first pair differ; comparing only the numbers
/// would have made the second pair agree, which would price a can of drink as a bag of lollies.</para>
/// </summary>
public enum ProductSizeDimension
{
    /// <summary>Millilitres and litres.</summary>
    Volume = 1,

    /// <summary>Grams and kilograms.</summary>
    Weight = 2,

    /// <summary>A number of countable things.</summary>
    Count = 3,
}

/// <summary>
/// One size in its canonical unit (issue #941): millilitres for a volume, grams for a weight, and the
/// count itself for something counted.
///
/// <para>Produced only by <see cref="AutomaticProductMatchPolicy.SizeOf"/> and
/// <see cref="AutomaticProductMatchPolicy.TryParseSize"/>, so two sizes are only ever compared after
/// both have been converted by the same rule.</para>
/// </summary>
/// <param name="CanonicalValue">The size in the canonical unit of <paramref name="Dimension"/>.</param>
/// <param name="Dimension">What it measures.</param>
public sealed record ProductSizeMeasurement(decimal CanonicalValue, ProductSizeDimension Dimension);

/// <summary>
/// One master product the backend checks a waiting key against (issue #941): its id and its
/// description, and nothing else - no listing, no price and nothing about any business.
/// </summary>
public sealed record ProductMatchCandidate(
    long MasterProductId,
    string Brand,
    string Name,
    decimal SizeValue,
    MasterProductSizeUnit SizeUnit);

/// <summary>
/// One descriptor that changes which product a description is about, with every spelling that means
/// it (issue #941).
///
/// <para>The spellings are a synonym group rather than separate descriptors so that <c>"Light"</c> and
/// <c>"Lite"</c> - the issue's own <c>light/lite</c> entry - are not read as two different descriptors
/// that each conflict with the other.</para>
/// </summary>
/// <param name="Name">What the descriptor is called, which is what a reason names.</param>
/// <param name="Spellings">
/// Every spelling of it, each a word or a sequence of words. A spelling matches a description only as
/// whole words, in order and next to each other.
/// </param>
public sealed record ProductDescriptor(string Name, IReadOnlyList<string> Spellings);

/// <summary>
/// What is to be done about one match proposal (issue #941): the master product to apply, or the
/// reason nothing is applied.
/// </summary>
/// <param name="MasterProductId">
/// The master product the mappings with this key are to be matched to, or <see langword="null"/> when
/// the proposal is stored for the Super Admin instead.
/// </param>
/// <param name="Refusal">
/// Why no automatic match was made, and <see langword="null"/> for exactly the decisions that apply
/// one. It is the first condition that failed, so a collector is told what to fix rather than that
/// something was wrong.
/// </param>
public sealed record AutomaticProductMatchDecision(long? MasterProductId, string? Refusal)
{
    /// <summary>Whether this decision applies an automatic match.</summary>
    public bool IsApplied => MasterProductId is not null;
}

/// <summary>
/// When a product waiting for a match is matched to an existing master product <strong>automatically</strong>
/// (issue #941, parent #817), under the owner's decision of 27 October 2026: the obvious matches are
/// applied and flagged as automatic, and everything else goes to the Super Admin (#928).
///
/// <para><strong>"Obvious" is this policy's answer and never the matcher's.</strong> Claude's own
/// confidence is not calibrated, so a percentage it reports decides nothing: the level is one
/// condition - it must be <see cref="ProductMatchConfidence.High"/> - and every other condition is
/// checked here, against the description the business's own product carries and against the
/// <em>whole</em> master catalogue. A match is applied only when exactly one master product passes
/// every check and that one is the master product the proposal named.</para>
///
/// <para><strong>The conditions, in the order they are asked</strong> (which is the order a refusal
/// reports):</para>
/// <list type="number">
/// <item><description>The unit label parses to a size and a unit at all - <c>"375mL"</c> is 375 ml and
/// <c>"1.25L"</c> is 1250 ml.</description></item>
/// <item><description>That size equals the master product's size once both are in their canonical
/// unit, so 375 ml and 0.375 l agree and 375 ml and 355 ml do not.</description></item>
/// <item><description>The master product's brand appears in the search name, word for
/// word.</description></item>
/// <item><description><strong>The variant agrees</strong>: every word of the master product's own name
/// appears in the search name.</description></item>
/// <item><description><strong>No conflicting descriptor</strong>: no descriptor in
/// <see cref="ConflictingDescriptors"/> appears in one of the two descriptions and not the other, so
/// <c>Coke Zero 375 mL</c> can never be matched to <c>Coke Classic 375 mL</c>.</description></item>
/// <item><description><strong>Exactly one suitable candidate</strong>, and it is the one proposed. Two
/// master products that both pass are an ambiguity a person resolves, not one a matcher may
/// guess at.</description></item>
/// <item><description>The <c>(waiting key, master product)</c> pair has not been rejected or undone
/// before - the refusals a Super Admin's decision leaves behind
/// (<see cref="ProductMatchSuggestionPolicy.BlocksAutomaticMatching"/>).</description></item>
/// </list>
///
/// <para><strong>Every method here is a pure function of the values it is handed.</strong> It stores
/// nothing, reads nothing and knows no business: which master products exist, which pairs were
/// refused and which mappings carry a key are the adapter's reads, answered before this policy is
/// asked anything. A description it is handed is a product description - a name, a unit label, a brand
/// - and never anything that says whose product it is.</para>
///
/// <para><strong>Nothing here creates a master product.</strong> A proposal that describes a new one
/// is refused an automatic match by construction (it names no master product to apply) and is stored
/// as a suggestion for #928; that is the issue's own exclusion, and it is why
/// <see cref="Decide"/> answers a proposal with no master product with
/// <see cref="SuggestedNewMasterReason"/> rather than examining it.</para>
/// </summary>
public static class AutomaticProductMatchPolicy
{
    /// <summary>
    /// The largest batch of proposals one request may carry: two hundred, the owner's figure and the
    /// same bound a batch of results carries (<c>PriceCollectorResultPolicy.MaxRowsPerBatch</c>).
    /// </summary>
    public const int MaxProposalsPerBatch = 200;

    /// <summary>The stored proportion of <see cref="ProductMatchConfidence.High"/>.</summary>
    public const decimal HighConfidenceProportion = 1m;

    /// <summary>The stored proportion of <see cref="ProductMatchConfidence.Medium"/>.</summary>
    public const decimal MediumConfidenceProportion = 0.6m;

    /// <summary>
    /// The stored proportion of <see cref="ProductMatchConfidence.Low"/>. Not zero, which is what the
    /// results endpoint writes on the evidence it records itself (issue #937), so a level a matcher
    /// reported and a row nobody reported a level for stay distinguishable.
    /// </summary>
    public const decimal LowConfidenceProportion = 0.3m;

    /// <summary>What a caller who submitted more proposals than one request may carry is told.</summary>
    public static string BatchTooLargeMessage { get; } =
        $"A price collector matches batch carries at most {MaxProposalsPerBatch} proposals.";

    /// <summary>What a proposal that named no waiting key the run issued is told.</summary>
    public const string UnknownWaitingKeyReason = "unknown waiting key";

    /// <summary>What a proposal that named a master product this catalogue does not hold is told.</summary>
    public const string UnknownMasterProductReason = "unknown master product";

    /// <summary>
    /// What a proposal about a key no mapping is awaiting a match under any more is told.
    ///
    /// <para>The key is a <em>folded</em> search name and unit label, so the only statement of what it
    /// was a search for is the text the mappings that carry it hold. With none of them still awaiting a
    /// match - every business turned tracking off, renamed its product or had the key decided already -
    /// there is no description to check a master product against and nothing for a decision to change,
    /// so the proposal is refused rather than recorded as a decision nobody needs to make.</para>
    /// </summary>
    public const string NothingAwaitingThisKeyReason =
        "nothing is awaiting a match under this waiting key";

    /// <summary>What a second proposal for one key in one batch is told.</summary>
    public const string DuplicateProposalInBatchReason = "duplicate waiting key in this batch";

    /// <summary>
    /// What a proposal for a key this run has already decided is answered with. It is reported beside
    /// the outcome that <em>was</em> recorded, and nothing is written: re-submitting one run's batch is
    /// idempotent, and re-deciding the key would answer against a catalogue that may have moved since.
    /// </summary>
    public const string AlreadyDecidedInThisRunReason =
        "this waiting key was already decided in this run";

    /// <summary>
    /// What a proposal whose pair - or whose suggested description - a suggestion already records is
    /// answered with. The existing row is reported and no second one is written: a reviewer looking at
    /// one decision must not be handed the same evidence once per run.
    /// </summary>
    public const string AlreadySuggestedReason = "this proposal is already recorded as a suggestion";

    /// <summary>
    /// Why a proposal that describes a new master product is never applied. A new master product is
    /// created by a person and by nobody else, which is this issue's own exclusion.
    /// </summary>
    public const string SuggestedNewMasterReason =
        "a suggested new master product is stored for review and never created automatically";

    /// <summary>Why a proposal below <see cref="ProductMatchConfidence.High"/> is never applied.</summary>
    public const string ConfidenceBelowHighReason = "confidence is not high";

    /// <summary>
    /// Why a product whose unit label says no size is never matched automatically. The label is the
    /// only statement of what size the product comes in, and a match that cannot compare sizes is a
    /// match on a name.
    /// </summary>
    public const string UnitLabelNotParsedReason =
        "the unit label does not parse to a size and a unit";

    /// <summary>Why a master product of another size or another kind of size is not suitable.</summary>
    public const string SizeDiffersReason =
        "the master product's size differs from the unit label's size";

    /// <summary>Why a master product whose brand the search name does not contain is not suitable.</summary>
    public const string BrandMissingReason =
        "the master product's brand does not appear in the search name";

    /// <summary>Why a master product whose variant the search name does not contain is not suitable.</summary>
    public const string VariantDiffersReason =
        "a word of the master product's variant does not appear in the search name";

    /// <summary>
    /// Why a descriptor on one side and not the other refuses the match. The one condition that
    /// refuses a pair whose brand, variant words and size all agree - <c>Coke Zero</c> and
    /// <c>Coke Classic</c> are the case it exists for.
    /// </summary>
    public const string ConflictingDescriptorReason =
        "a descriptor appears in one description and not the other";

    /// <summary>
    /// Why an ambiguity is never resolved automatically: two or more master products pass every check,
    /// so which one this product is is a person's decision.
    /// </summary>
    public const string SeveralSuitableCandidatesReason =
        "more than one master product passes the automatic match checks";

    /// <summary>
    /// Why a pair a Super Admin rejected, or undid after it had been applied, is never applied again.
    /// A person who refused a match once must not have to refuse it every run.
    /// </summary>
    public const string PairRefusedReason =
        "this waiting key and master product were rejected or undone before";

    /// <summary>
    /// The descriptors that decide which product a description is about, and the whole list of them
    /// (issue #941's own list, in its own order).
    ///
    /// <para><strong>A fixed, tested list rather than a rule.</strong> Nothing infers that a word is a
    /// descriptor: a list is what makes the behaviour testable and what keeps a brand that happens to
    /// contain a word from being read as a variant of something else. A descriptor that appears on one
    /// side and not the other refuses the match, in <em>either</em> direction - a search for a plain
    /// cola must not be matched to a diet one, and a search for a diet cola must not be matched to the
    /// plain one.</para>
    ///
    /// <para>Each entry holds every spelling that means the same descriptor, so a difference of
    /// spelling is not read as a difference of product.</para>
    /// </summary>
    public static IReadOnlyList<ProductDescriptor> ConflictingDescriptors { get; } =
    [
        new("zero", ["zero"]),
        new("no sugar", ["no sugar"]),
        new("sugar free", ["sugar free", "sugarfree"]),
        new("diet", ["diet"]),
        new("max", ["max"]),
        new("light", ["light", "lite"]),
        new("caffeine free", ["caffeine free", "caffeinefree"]),
        new("decaf", ["decaf", "decaffeinated"]),
        new("vanilla", ["vanilla"]),
        new("cherry", ["cherry"]),
        new("lime", ["lime"]),
        new("original", ["original"]),
        new("classic", ["classic"]),
    ];

    /// <summary>
    /// Whether a proposal refused for <paramref name="refusal"/> is nevertheless kept as a tentative
    /// match for the Super Admin (#928), which is every refusal but two.
    ///
    /// <para><see cref="UnknownMasterProductReason"/> names nothing that could be stored - there is no
    /// master product for a suggestion to point at. <see cref="PairRefusedReason"/> is <em>already</em>
    /// recorded, as the rejection or the undoing that refused it, and the whole reason those rows are
    /// kept for ever is so that pair is never proposed again: writing a fresh tentative row for it
    /// would hand the person who refused it the same decision to make every run. Everything else is a
    /// match this backend could not call obvious, which is exactly what a person is asked to
    /// decide.</para>
    /// </summary>
    public static bool StoresTentativeMatch(string? refusal) =>
        refusal is not null
            && refusal != UnknownMasterProductReason
            && refusal != PairRefusedReason;

    /// <summary>Whether <paramref name="proposalCount"/> is a batch this application accepts.</summary>
    public static bool IsAcceptedBatchSize(int proposalCount) => proposalCount <= MaxProposalsPerBatch;

    /// <summary>Refuses a batch larger than one request may carry. Nothing is stored.</summary>
    /// <exception cref="DomainValidationException">
    /// The batch holds more than <see cref="MaxProposalsPerBatch"/> proposals.
    /// </exception>
    public static void RequireAcceptedBatchSize(int proposalCount)
    {
        if (!IsAcceptedBatchSize(proposalCount))
        {
            throw new DomainValidationException(BatchTooLargeMessage);
        }
    }

    /// <summary>Whether <paramref name="confidence"/> is one of the three declared levels.</summary>
    public static bool IsDeclaredConfidence(ProductMatchConfidence confidence) =>
        Enum.IsDefined(confidence);

    /// <summary>
    /// What a caller who chose a confidence that is not one of the declared levels is told. Built from
    /// the vocabulary itself so it cannot drift from what is accepted.
    /// </summary>
    public static string UnknownConfidenceMessage { get; } =
        "A match confidence must be one of "
            + string.Join(", ", Enum.GetNames<ProductMatchConfidence>()).ToLowerInvariant()
            + ".";

    /// <summary>
    /// The proportion one level is stored as on the suggestion row (issue #923's
    /// <c>Confidence</c> column), so the three levels are three exact, documented values rather than
    /// a number anybody rounded.
    /// </summary>
    /// <exception cref="DomainValidationException">The level is not a declared one.</exception>
    public static decimal ConfidenceProportion(ProductMatchConfidence confidence) => confidence switch
    {
        ProductMatchConfidence.High => HighConfidenceProportion,
        ProductMatchConfidence.Medium => MediumConfidenceProportion,
        ProductMatchConfidence.Low => LowConfidenceProportion,
        _ => throw new DomainValidationException(UnknownConfidenceMessage),
    };

    /// <summary>
    /// Whether a proposal of this confidence may be applied automatically at all: only
    /// <see cref="ProductMatchConfidence.High"/>, and only together with every other condition.
    /// </summary>
    public static bool IsAutomaticConfidence(ProductMatchConfidence confidence) =>
        confidence == ProductMatchConfidence.High;

    /// <summary>
    /// One master product's own size in its canonical unit: millilitres, grams, or the count itself.
    /// </summary>
    /// <exception cref="DomainValidationException">The unit is not a declared one.</exception>
    public static ProductSizeMeasurement SizeOf(decimal sizeValue, MasterProductSizeUnit sizeUnit) =>
        sizeUnit switch
        {
            MasterProductSizeUnit.Millilitre => new(sizeValue, ProductSizeDimension.Volume),
            MasterProductSizeUnit.Litre => new(sizeValue * 1000m, ProductSizeDimension.Volume),
            MasterProductSizeUnit.Gram => new(sizeValue, ProductSizeDimension.Weight),
            MasterProductSizeUnit.Kilogram => new(sizeValue * 1000m, ProductSizeDimension.Weight),
            MasterProductSizeUnit.Each => new(sizeValue, ProductSizeDimension.Count),
            _ => throw new DomainValidationException(MasterProductPolicy.UnknownSizeUnitMessage),
        };

    /// <summary>
    /// The size a product's own unit label states, or <see langword="null"/> when it states none.
    ///
    /// <para><strong>A number and then a unit, and nothing else.</strong> Whitespace is dropped, the
    /// unit is read without regard to case, and the accepted unit words are the declared units'
    /// ordinary spellings - so <c>"375mL"</c>, <c>"375 ML"</c> and <c>"375 millilitres"</c> are one
    /// size, and <c>"1.25L"</c> is 1250 ml. A label this rule cannot read - <c>"can"</c>, <c>"375ml
    /// bottle"</c>, <c>"each"</c> with no number - parses to nothing, which refuses an automatic match
    /// and sends the proposal to the Super Admin instead. <strong>Failing to parse is always the safe
    /// answer</strong>, which is why this rule is deliberately strict rather than clever: a label it
    /// guessed at would be a size nobody wrote, and a per-unit price divides by it.</para>
    /// </summary>
    public static bool TryParseSize(string? unitLabel, out ProductSizeMeasurement? size)
    {
        size = null;

        var compact = Compact(unitLabel);

        if (compact.Length == 0)
        {
            return false;
        }

        var digits = 0;
        var points = 0;

        while (digits + points < compact.Length)
        {
            var character = compact[digits + points];

            if (char.IsAsciiDigit(character))
            {
                digits++;
                continue;
            }

            if (character == '.' && points == 0 && digits > 0)
            {
                points++;
                continue;
            }

            break;
        }

        var numberLength = digits + points;

        if (digits == 0 || numberLength == compact.Length)
        {
            // No number at all, or no unit after it. Either way the label does not say a size.
            return false;
        }

        if (!decimal.TryParse(
            compact.AsSpan(0, numberLength),
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out var value) || value <= 0m)
        {
            return false;
        }

        if (UnitWords.TryGetValue(compact[numberLength..], out var unit))
        {
            size = SizeOf(value, unit);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the size a unit label states is the same size the master product records, once both are
    /// in their canonical unit: 375 ml and 0.375 l agree, 375 ml and 355 ml do not, and 375 ml and
    /// 375 g do not either.
    /// </summary>
    public static bool SizesAgree(ProductSizeMeasurement labelSize, ProductMatchCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(labelSize);
        ArgumentNullException.ThrowIfNull(candidate);

        var candidateSize = SizeOf(candidate.SizeValue, candidate.SizeUnit);

        return labelSize.Dimension == candidateSize.Dimension
            && labelSize.CanonicalValue == candidateSize.CanonicalValue;
    }

    /// <summary>
    /// Whether every word of <paramref name="brand"/> appears in <paramref name="searchName"/>.
    ///
    /// <para>Word for word rather than as a substring, because a substring match would read
    /// <c>"Cola"</c> as <c>"Coca-Cola"</c>. Words are compared with case and punctuation folded away,
    /// so <c>"Coca-Cola"</c> in a brand and <c>"coca cola"</c> in a product name are the same two
    /// words.</para>
    /// </summary>
    public static bool BrandAppearsIn(string? brand, string? searchName) =>
        ContainsEveryWord(Words(searchName), Words(brand));

    /// <summary>
    /// Whether every word of the master product's own name - its variant - appears in
    /// <paramref name="searchName"/>.
    ///
    /// <para>This is the issue's "the variant agrees" condition, and it is deliberately one-directional:
    /// a product called <c>"Coca-Cola Classic 375mL can"</c> may be <c>Coca-Cola Classic</c>, but a
    /// product called <c>"Coca-Cola"</c> is not, because the master product says something about it
    /// that its own name does not. A word the search name has and the master product does not is left
    /// to the descriptor rule, which is what knows which of those words change the product.</para>
    /// </summary>
    public static bool VariantAgrees(string? masterName, string? searchName) =>
        ContainsEveryWord(Words(searchName), Words(masterName));

    /// <summary>
    /// Whether any descriptor in <see cref="ConflictingDescriptors"/> appears in one of the two
    /// descriptions and not the other.
    ///
    /// <para>Symmetric on purpose: <c>Coke Zero</c> against <c>Coke Classic</c> conflicts twice over -
    /// each side carries a descriptor the other does not - and either alone is enough. This is the
    /// condition that makes the brand, variant and size checks safe to state as loosely as they are: a
    /// search name may carry words the master product does not, unless one of them is a word that
    /// changes which product it is.</para>
    /// </summary>
    public static bool HasConflictingDescriptor(string? searchText, string? masterDescription)
    {
        var searchWords = Words(searchText);
        var masterWords = Words(masterDescription);

        return ConflictingDescriptors.Any(descriptor =>
            Mentions(searchWords, descriptor) != Mentions(masterWords, descriptor));
    }

    /// <summary>
    /// Why <paramref name="candidate"/> is not a master product this product may be matched to
    /// automatically, or <see langword="null"/> when it is.
    ///
    /// <para>The order is the issue's own order, and the first failing condition is the answer: a
    /// collector that is told "the size differs" can fix its search, and one told only "no" cannot. It
    /// is a function of two descriptions, so the same question can be asked of every master product in
    /// the catalogue, which is what "exactly one suitable candidate" is counted over.</para>
    /// </summary>
    public static string? Unsuitability(
        string? searchName,
        ProductSizeMeasurement labelSize,
        ProductMatchCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (!SizesAgree(labelSize, candidate))
        {
            return SizeDiffersReason;
        }

        if (!BrandAppearsIn(candidate.Brand, searchName))
        {
            return BrandMissingReason;
        }

        if (!VariantAgrees(candidate.Name, searchName))
        {
            return VariantDiffersReason;
        }

        return HasConflictingDescriptor(searchName, candidate.Brand + " " + candidate.Name)
            ? ConflictingDescriptorReason
            : null;
    }

    /// <summary>
    /// What is to be done about one proposal: the master product to apply automatically, or the first
    /// condition that refused it.
    ///
    /// <para><strong>The candidates are the whole master catalogue, not the proposal's own
    /// suggestion.</strong> That is the issue's "exactly one suitable candidate" condition and the
    /// reason this policy is handed a list: the backend runs the checks itself, and the proposal is
    /// accepted only when its master product is the single one that passes. A proposal naming a
    /// perfectly plausible product is refused while a second plausible one exists, because which of
    /// two it is is exactly what nobody has decided.</para>
    /// </summary>
    /// <param name="searchName">The search name the mappings with this key carry.</param>
    /// <param name="unitLabel">The unit label beside it.</param>
    /// <param name="proposedMasterProductId">
    /// The master product proposed, or <see langword="null"/> when a new one was described instead.
    /// </param>
    /// <param name="confidence">How sure the matcher said it was.</param>
    /// <param name="candidates">Every master product the catalogue holds.</param>
    /// <param name="refusedMasterProductIds">
    /// The master products rejected or undone for this key, which may never be applied to it again and
    /// are not counted as suitable candidates either.
    /// </param>
    public static AutomaticProductMatchDecision Decide(
        string? searchName,
        string? unitLabel,
        long? proposedMasterProductId,
        ProductMatchConfidence confidence,
        IReadOnlyCollection<ProductMatchCandidate> candidates,
        IReadOnlySet<long> refusedMasterProductIds)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(refusedMasterProductIds);

        if (proposedMasterProductId is not { } proposedId)
        {
            return Refused(SuggestedNewMasterReason);
        }

        var proposed = candidates.FirstOrDefault(
            candidate => candidate.MasterProductId == proposedId);

        if (proposed is null)
        {
            return Refused(UnknownMasterProductReason);
        }

        if (refusedMasterProductIds.Contains(proposedId))
        {
            return Refused(PairRefusedReason);
        }

        if (!IsAutomaticConfidence(confidence))
        {
            return Refused(ConfidenceBelowHighReason);
        }

        if (!TryParseSize(unitLabel, out var labelSize))
        {
            return Refused(UnitLabelNotParsedReason);
        }

        if (Unsuitability(searchName, labelSize!, proposed) is { } unsuitable)
        {
            return Refused(unsuitable);
        }

        // The proposal passes every check, so it is one of the suitable candidates; a second suitable
        // one therefore means the catalogue cannot say which of the two this product is.
        var suitable = candidates.Count(candidate =>
            !refusedMasterProductIds.Contains(candidate.MasterProductId)
            && Unsuitability(searchName, labelSize!, candidate) is null);

        return suitable == 1
            ? new AutomaticProductMatchDecision(proposedId, Refusal: null)
            : Refused(SeveralSuitableCandidatesReason);
    }

    private static AutomaticProductMatchDecision Refused(string reason) => new(null, reason);

    /// <summary>
    /// The unit words a label may be written with, folded the way <see cref="Compact"/> folds a label.
    /// The declared unit codes and their ordinary spellings, and deliberately nothing clever: a word
    /// that is not here parses to no size, which refuses an automatic match rather than guessing one.
    /// </summary>
    private static readonly Dictionary<string, MasterProductSizeUnit> UnitWords = new(StringComparer.Ordinal)
    {
        ["ML"] = MasterProductSizeUnit.Millilitre,
        ["MLS"] = MasterProductSizeUnit.Millilitre,
        ["MILLILITRE"] = MasterProductSizeUnit.Millilitre,
        ["MILLILITRES"] = MasterProductSizeUnit.Millilitre,
        ["MILLILITER"] = MasterProductSizeUnit.Millilitre,
        ["MILLILITERS"] = MasterProductSizeUnit.Millilitre,
        ["L"] = MasterProductSizeUnit.Litre,
        ["LT"] = MasterProductSizeUnit.Litre,
        ["LTR"] = MasterProductSizeUnit.Litre,
        ["LITRE"] = MasterProductSizeUnit.Litre,
        ["LITRES"] = MasterProductSizeUnit.Litre,
        ["LITER"] = MasterProductSizeUnit.Litre,
        ["LITERS"] = MasterProductSizeUnit.Litre,
        ["G"] = MasterProductSizeUnit.Gram,
        ["GM"] = MasterProductSizeUnit.Gram,
        ["GRAM"] = MasterProductSizeUnit.Gram,
        ["GRAMS"] = MasterProductSizeUnit.Gram,
        ["KG"] = MasterProductSizeUnit.Kilogram,
        ["KGS"] = MasterProductSizeUnit.Kilogram,
        ["KILO"] = MasterProductSizeUnit.Kilogram,
        ["KILOS"] = MasterProductSizeUnit.Kilogram,
        ["KILOGRAM"] = MasterProductSizeUnit.Kilogram,
        ["KILOGRAMS"] = MasterProductSizeUnit.Kilogram,
        ["EACH"] = MasterProductSizeUnit.Each,
        ["PACK"] = MasterProductSizeUnit.Each,
        ["PK"] = MasterProductSizeUnit.Each,
    };

    /// <summary>
    /// One label with its whitespace dropped and its letters upper-cased invariantly - the fold
    /// <c>PriceCatalogueKey</c> applies, for the reason it applies it: the same label must mean the
    /// same thing on every machine that reads one.
    /// </summary>
    private static string Compact(string? value)
    {
        var compact = new StringBuilder();

        foreach (var character in value ?? string.Empty)
        {
            if (!char.IsWhiteSpace(character))
            {
                compact.Append(char.ToUpperInvariant(character));
            }
        }

        return compact.ToString();
    }

    /// <summary>
    /// One description as its words: runs of letters and digits, upper-cased invariantly, with every
    /// other character read as a boundary.
    ///
    /// <para>Punctuation is a boundary rather than a character here - unlike in
    /// <c>PriceCatalogueKey</c>, which folds a key and must not merge two descriptions - because
    /// <c>"Coca-Cola"</c> and <c>"Coca Cola"</c> are the same brand written two ways, and a brand that
    /// matched only one of them would send every second product to the Super Admin.</para>
    /// </summary>
    private static List<string> Words(string? value)
    {
        List<string> words = [];
        var word = new StringBuilder();

        foreach (var character in value ?? string.Empty)
        {
            if (char.IsLetterOrDigit(character))
            {
                word.Append(char.ToUpperInvariant(character));
                continue;
            }

            if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }

        return words;
    }

    /// <summary>Whether every word of <paramref name="required"/> appears in <paramref name="words"/>.</summary>
    private static bool ContainsEveryWord(List<string> words, List<string> required) =>
        required.Count > 0 && required.TrueForAll(word => words.Contains(word, StringComparer.Ordinal));

    /// <summary>
    /// Whether <paramref name="words"/> mentions <paramref name="descriptor"/>: any of its spellings,
    /// as whole words, in order and next to each other.
    /// </summary>
    private static bool Mentions(List<string> words, ProductDescriptor descriptor) =>
        descriptor.Spellings.Any(spelling => ContainsPhrase(words, Words(spelling)));

    private static bool ContainsPhrase(List<string> words, List<string> phrase)
    {
        if (phrase.Count == 0 || phrase.Count > words.Count)
        {
            return false;
        }

        for (var start = 0; start <= words.Count - phrase.Count; start++)
        {
            var matches = true;

            for (var offset = 0; offset < phrase.Count && matches; offset++)
            {
                matches = string.Equals(words[start + offset], phrase[offset], StringComparison.Ordinal);
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }
}
