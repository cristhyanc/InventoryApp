using Inventory.Domain.Exceptions;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// What has happened to one suggested match (issue #923, parent #817, for the automatic matching of
/// #941 and the review of #928).
///
/// <para><strong>The two refusing states are the point of the table.</strong> A suggestion that was
/// <see cref="Rejected"/> by a person, or <see cref="Undone"/> after it had been applied, is kept
/// forever so the matcher never proposes or applies that pair again: a matcher that forgot a
/// rejection would re-apply it on its next run, and the person who rejected it would have to reject
/// it again every run. Nothing in this catalogue deletes a suggestion.</para>
///
/// <para>Numbered from 1, so a stored <c>0</c> is never read as <see cref="Tentative"/> by accident.</para>
/// </summary>
public enum ProductMatchSuggestionStatus
{
    /// <summary>Proposed and waiting for a decision - the collector's answer, not yet acted on.</summary>
    Tentative = 1,

    /// <summary>Applied automatically by the matcher (#941), which a person may still undo.</summary>
    AutoApplied = 2,

    /// <summary>Accepted by a person (#928).</summary>
    Accepted = 3,

    /// <summary>
    /// Refused by a person. Kept, and never proposed or auto-applied again - see
    /// <see cref="ProductMatchSuggestionPolicy.BlocksAutomaticMatching"/>.
    /// </summary>
    Rejected = 4,

    /// <summary>
    /// Applied and then reversed. Kept on the same terms as <see cref="Rejected"/>: an automatic
    /// match a person took back is a match that was wrong, whatever its confidence said.
    /// </summary>
    Undone = 5,
}

/// <summary>
/// The rules of one suggested match (issue #923, parent #817): what the matcher thinks a waiting
/// product is, how sure it is, why, which run said so, and what was then done about it.
///
/// <para><strong>A suggestion is about a waiting key, not about a business.</strong> It names the key
/// a search was run for and never the business whose product produced it - which is what makes this a
/// platform table with no business id to carry, and what keeps one business from being able to learn
/// that another is tracking anything (issue #923's third known constraint).</para>
///
/// <para><strong>One proposal per row.</strong> A suggestion either names an existing master product
/// or describes a new one to create, never both: those are two different things to do about one
/// waiting key, and a row holding both would leave the reviewer of #928 with no way to know which was
/// being suggested. <see cref="RequireExactlyOneProposal"/> is that rule, and the database holds it
/// too.</para>
///
/// <para><strong>Confidence is a number the matcher reports, and it decides nothing here.</strong>
/// Whether a confidence is high enough to apply automatically is #941's threshold, deliberately not
/// this policy's: this one only refuses a value that is not a proportion at all.</para>
/// </summary>
public static class ProductMatchSuggestionPolicy
{
    /// <summary>The longest accepted reason, measured on the trimmed value.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>
    /// The longest accepted collector-run identifier. Whatever the collector of #924 names its runs -
    /// an id, a timestamped label - it is opaque text here and nothing parses it.
    /// </summary>
    public const int MaxCollectorRunLength = 100;

    /// <summary>
    /// The number of decimal places a confidence is recorded to, and the scale of the stored column.
    /// Four is finer than any matcher's threshold needs and is a bound rather than a rounding rule.
    /// </summary>
    public const int ConfidenceDecimals = 4;

    /// <summary>What a caller who submitted no reason, or only whitespace, is told.</summary>
    public const string ReasonRequiredMessage = "A match suggestion needs a reason.";

    /// <summary>What a caller who submitted a reason longer than the limit is told.</summary>
    public const string ReasonTooLongMessage = "A match suggestion reason must be 500 characters or fewer.";

    /// <summary>What a caller who submitted no collector run is told.</summary>
    public const string CollectorRunRequiredMessage =
        "A match suggestion needs the collector run that produced it.";

    /// <summary>What a caller who submitted a collector run longer than the limit is told.</summary>
    public const string CollectorRunTooLongMessage =
        "A collector run must be 100 characters or fewer.";

    /// <summary>
    /// What a caller who submitted a confidence outside the unit interval is told. It is a proportion
    /// of certainty, so <c>0</c> and <c>1</c> are both accepted and anything outside them is not a
    /// confidence at all.
    /// </summary>
    public const string ConfidenceOutOfRangeMessage =
        "A match confidence must be between 0 and 1.";

    /// <summary>What a caller who submitted a confidence with more precision than the stored scale is told.</summary>
    public const string ConfidenceTooPreciseMessage =
        "A match confidence is recorded to at most four decimal places.";

    /// <summary>
    /// What a caller who both named a master product and described a new one, or did neither, is told.
    /// </summary>
    public const string OneProposalRequiredMessage =
        "A match suggestion proposes an existing master product or a new one to create, not both and "
            + "not neither.";

    /// <summary>What a caller who described a new master product incompletely is told.</summary>
    public const string IncompleteSuggestedMasterMessage =
        "A suggested new master product needs a brand, a name, a size and a size unit.";

    /// <summary>Whether <paramref name="status"/> is one of the declared states.</summary>
    public static bool IsDeclaredStatus(ProductMatchSuggestionStatus status) => Enum.IsDefined(status);

    /// <summary>
    /// Whether a suggestion in <paramref name="status"/> is one the matcher must never act on again
    /// for that (waiting key, master product) pair: a rejection, and an application a person undid.
    ///
    /// <para>It is a state the row keeps rather than a row that is removed, which is the whole reason
    /// the table never deletes: the evidence that a pair was refused has to outlive the refusal, or
    /// the next run repeats it.</para>
    /// </summary>
    public static bool BlocksAutomaticMatching(ProductMatchSuggestionStatus status) =>
        status is ProductMatchSuggestionStatus.Rejected or ProductMatchSuggestionStatus.Undone;

    /// <summary>The reason as it is to be stored: trimmed, never empty.</summary>
    /// <exception cref="DomainValidationException">
    /// The reason is blank, or longer than <see cref="MaxReasonLength"/> once trimmed.
    /// </exception>
    public static string NormaliseReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainValidationException(ReasonRequiredMessage);
        }

        var trimmed = reason.Trim();

        return trimmed.Length <= MaxReasonLength
            ? trimmed
            : throw new DomainValidationException(ReasonTooLongMessage);
    }

    /// <summary>The collector run as it is to be stored: trimmed, never empty.</summary>
    /// <exception cref="DomainValidationException">
    /// The run is blank, or longer than <see cref="MaxCollectorRunLength"/> once trimmed.
    /// </exception>
    public static string NormaliseCollectorRun(string? collectorRun)
    {
        if (string.IsNullOrWhiteSpace(collectorRun))
        {
            throw new DomainValidationException(CollectorRunRequiredMessage);
        }

        var trimmed = collectorRun.Trim();

        return trimmed.Length <= MaxCollectorRunLength
            ? trimmed
            : throw new DomainValidationException(CollectorRunTooLongMessage);
    }

    /// <summary>
    /// The confidence as it is to be stored: a proportion between 0 and 1 inclusive, of at most
    /// <see cref="ConfidenceDecimals"/> decimal places, returned unchanged rather than rounded.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The value is outside the unit interval or finer than the stored scale. Nothing is stored.
    /// </exception>
    public static decimal NormaliseConfidence(decimal confidence)
    {
        if (confidence is < 0m or > 1m)
        {
            throw new DomainValidationException(ConfidenceOutOfRangeMessage);
        }

        return decimal.Round(confidence, ConfidenceDecimals) == confidence
            ? confidence
            : throw new DomainValidationException(ConfidenceTooPreciseMessage);
    }

    /// <summary>
    /// Whether a suggestion carries exactly one proposal: an existing master product, or a complete
    /// description of a new one, never both and never neither.
    /// </summary>
    public static bool HasExactlyOneProposal(
        long? masterProductId,
        string? suggestedBrand,
        string? suggestedName,
        decimal? suggestedSize,
        MasterProductSizeUnit? suggestedSizeUnit)
    {
        var hasMasterProduct = masterProductId is not null;

        var describedParts = new[]
        {
            !string.IsNullOrWhiteSpace(suggestedBrand),
            !string.IsNullOrWhiteSpace(suggestedName),
            suggestedSize is not null,
            suggestedSizeUnit is not null,
        };

        var describesNewMaster = Array.TrueForAll(describedParts, part => part);
        var describesNothing = Array.TrueForAll(describedParts, part => !part);

        return hasMasterProduct
            ? describesNothing
            : describesNewMaster;
    }

    /// <summary>
    /// Refuses a suggestion that proposes both an existing master product and a new one, neither, or
    /// a new one described only in part.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The suggestion does not carry exactly one complete proposal. Nothing is stored.
    /// </exception>
    public static void RequireExactlyOneProposal(
        long? masterProductId,
        string? suggestedBrand,
        string? suggestedName,
        decimal? suggestedSize,
        MasterProductSizeUnit? suggestedSizeUnit)
    {
        if (HasExactlyOneProposal(
            masterProductId,
            suggestedBrand,
            suggestedName,
            suggestedSize,
            suggestedSizeUnit))
        {
            return;
        }

        // The two refusals are deliberately different sentences: "both or neither" and "half a
        // description" are different mistakes, and telling a reviewer which one they made is the
        // difference between a message that helps and one that only says no.
        var namesSomething = masterProductId is not null
            || !string.IsNullOrWhiteSpace(suggestedBrand)
            || !string.IsNullOrWhiteSpace(suggestedName)
            || suggestedSize is not null
            || suggestedSizeUnit is not null;

        throw new DomainValidationException(
            masterProductId is null && namesSomething
                ? IncompleteSuggestedMasterMessage
                : OneProposalRequiredMessage);
    }
}
