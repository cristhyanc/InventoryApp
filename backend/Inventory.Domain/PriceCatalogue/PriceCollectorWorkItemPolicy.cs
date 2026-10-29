using Inventory.Domain.Exceptions;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// What became of one issued piece of collector work (issue #924, parent #817).
///
/// <para><strong>Empty is a state, and it is the one a work item is issued in.</strong> A work item
/// with no outcome at all is work that has been handed out and not yet reported on, which is
/// different from every value below - including <see cref="NotSearched"/>, which is a collector
/// saying it chose not to look. The column is therefore nullable, and no number here means "not
/// reported": that distinction is what lets an expired run be told from a run that reported
/// nothing to do (<see cref="PriceCollectorRunStatus.Expired"/>).</para>
///
/// <para>Numbered from 1, so a stored <c>0</c> is never read as the first declared outcome. The
/// results endpoint of #937 is what sets one; this issue only ever writes the absence.</para>
/// </summary>
public enum PriceCollectorWorkItemOutcome
{
    /// <summary>Searched, and everything the supplier publishes for this product was accounted for.</summary>
    Complete = 1,

    /// <summary>
    /// Searched, and the results were cut short - too many matches, a paginated listing the collector
    /// stopped reading. What it did report is real; what it did not reach is not a statement that the
    /// supplier has nothing.
    /// </summary>
    Truncated = 2,

    /// <summary>
    /// Searched, and this supplier publishes nothing that matches. A recorded answer, deliberately
    /// not a failure: a product this retailer does not stock is a fact worth keeping.
    /// </summary>
    NoMatches = 3,

    /// <summary>
    /// Attempted and could not be completed - the site was unreachable, the page unreadable, the
    /// response unparseable. It is never read as "no matches": the listings it did not reach keep
    /// their last accepted observation.
    /// </summary>
    Failed = 4,

    /// <summary>
    /// Issued and deliberately not searched - the run ran out of its own budget, or the collector
    /// skipped this supplier. Distinct from <see cref="Failed"/>, because nothing went wrong, and
    /// from an empty outcome, because the collector did report back about it.
    /// </summary>
    NotSearched = 5,
}

/// <summary>
/// Why a search could not be completed (issue #937, parent #817), recorded beside
/// <see cref="PriceCollectorWorkItemOutcome.Failed"/> and only there.
///
/// <para>A closed vocabulary rather than free text, because the reason is what decides whether a
/// failing search is somebody's problem or nobody's: an actor error is a collector defect, a
/// provider limit is a quota that will clear on its own, and a timeout is a site that was slow. A
/// sentence would have to be read to tell those apart. Numbered from 1.</para>
/// </summary>
public enum PriceCollectorSearchFailureReason
{
    /// <summary>The collector itself could not do the search - an unreadable page, an unparseable response.</summary>
    ActorError = 1,

    /// <summary>The provider refused to serve more - a rate limit, a quota, a block.</summary>
    ProviderLimit = 2,

    /// <summary>The search did not finish in time.</summary>
    Timeout = 3,
}

/// <summary>
/// The rules of one issued piece of collector work (issue #924, parent #817): which product and
/// which supplier it is for, and what the collector may later say about it.
///
/// <para><strong>A work item names exactly one product, in exactly one of the two ways a product can
/// be named.</strong> Either a master product the catalogue already holds, or the waiting key of a
/// product nothing has matched yet - never both and never neither. Both at once would be a search
/// claiming to be about two products; neither would be work no result could ever be attributed to,
/// which is precisely what the results endpoint of #937 needs the work item for.</para>
///
/// <para><strong>It carries no business identity, and there is none to carry.</strong> A work item is
/// a product description and a supplier, which are facts about products and shelves - the same
/// reason <see cref="MasterProductPolicy"/>'s rows and the match suggestions are platform data. No
/// business id, no product id and no count of how many businesses wanted this search reach it.</para>
///
/// <para><strong>An outcome is written once, by the collector reporting back.</strong> This policy
/// refuses an undeclared value for the reason every vocabulary here is checked - an enum is a
/// compile-time constraint, not a validation, and a JSON number binds whatever integer it likes.
/// </para>
/// </summary>
public static class PriceCollectorWorkItemPolicy
{
    /// <summary>What a caller whose work item names both a master product and a waiting key, or neither, is told.</summary>
    public const string ExactlyOneProductMessage =
        "A price collector work item names either a master product or a waiting key, never both and "
            + "never neither.";

    /// <summary>What a caller who submitted a work-item outcome that is not a declared one is told.</summary>
    public static string UnknownOutcomeMessage { get; } =
        "A price collector work item's outcome must be empty or one of "
            + string.Join(", ", Enum.GetNames<PriceCollectorWorkItemOutcome>())
            + ".";

    /// <summary>
    /// Whether <paramref name="masterProductId"/> and <paramref name="waitingKey"/> name exactly one
    /// product between them. A blank waiting key is no waiting key, for the reason
    /// <see cref="SupplierListingPolicy"/> treats one as absent: a stored empty string would be a key
    /// nothing can be found under.
    /// </summary>
    public static bool HasExactlyOneProduct(long? masterProductId, string? waitingKey) =>
        masterProductId.HasValue != !string.IsNullOrWhiteSpace(waitingKey);

    /// <summary>
    /// What a collector that tried to answer a search with <see cref="PriceCollectorWorkItemOutcome.NotSearched"/>
    /// is told. That outcome is the run completion's own answer for work nobody reported on
    /// (issue #937), so letting a collector claim it would make "we did not look" and "nobody told
    /// us" one fact again - which is exactly the distinction the empty outcome exists for.
    /// </summary>
    public static string UnreportableOutcomeMessage { get; } =
        "A collector answers a search with "
            + string.Join(
                ", ",
                Enum.GetValues<PriceCollectorWorkItemOutcome>()
                    .Where(IsCollectorReportable)
                    .Select(outcome => outcome.ToString()))
            + ".";

    /// <summary>What a caller whose failed search carries no reason, or whose successful one does, is told.</summary>
    public const string FailureReasonMismatchMessage =
        "A failed search records why it failed, and a search that did not fail records no reason.";

    /// <summary>What a caller whose failure reason is not a declared one is told.</summary>
    public static string UnknownFailureReasonMessage { get; } =
        "A search failure reason must be one of "
            + string.Join(", ", Enum.GetNames<PriceCollectorSearchFailureReason>())
            + ".";

    /// <summary>
    /// What a caller who answered a search with no matches, or with a failure, after submitting rows
    /// for it is told. "This supplier publishes nothing that matches" and "here are three packs" are
    /// contradictory statements about one search, and the first is what records a known listing as
    /// not found.
    /// </summary>
    public const string OutcomeRequiresNoRowsMessage =
        "A search answered as no matches or as failed carries no result rows.";

    /// <summary>
    /// What a caller whose declared total does not match the rows this search actually received is
    /// told. The search stays unanswered, which is the point: a batch that never arrived must leave
    /// the search unfinished rather than finish it short.
    /// </summary>
    public const string SubmittedTotalMismatchMessage =
        "The declared number of submitted supplier product ids does not match the rows received for "
            + "this search, so it stays unanswered.";

    /// <summary>
    /// What a caller who answers an already-answered search with something else is told. An
    /// identical second answer is accepted and changes nothing, because a retry of a call whose
    /// response was lost must not be a conflict.
    /// </summary>
    public const string AlreadyAnsweredDifferentlyMessage =
        "This search has already been answered, and the answer does not match.";

    /// <summary>Whether <paramref name="outcome"/> is empty or one of the declared outcomes.</summary>
    public static bool IsDeclaredOutcome(PriceCollectorWorkItemOutcome? outcome) =>
        outcome is null || Enum.IsDefined(outcome.Value);

    /// <summary>
    /// Whether a collector may answer a search with <paramref name="outcome"/>: everything except
    /// <see cref="PriceCollectorWorkItemOutcome.NotSearched"/>, which only closing a run writes.
    /// </summary>
    public static bool IsCollectorReportable(PriceCollectorWorkItemOutcome outcome) =>
        Enum.IsDefined(outcome) && outcome != PriceCollectorWorkItemOutcome.NotSearched;

    /// <summary>
    /// Whether <paramref name="outcome"/> is one a search may only reach having submitted no rows at
    /// all: it found nothing, or it did not finish.
    /// </summary>
    public static bool RequiresNoRows(PriceCollectorWorkItemOutcome outcome) =>
        outcome is PriceCollectorWorkItemOutcome.NoMatches or PriceCollectorWorkItemOutcome.Failed;

    /// <summary>
    /// Whether <paramref name="outcome"/> is evidence that a known listing this search did not
    /// report is <strong>not there</strong>: a complete search, or one that found no match at all.
    ///
    /// <para><see cref="PriceCollectorWorkItemOutcome.Truncated"/>,
    /// <see cref="PriceCollectorWorkItemOutcome.Failed"/> and
    /// <see cref="PriceCollectorWorkItemOutcome.NotSearched"/> are deliberately not: each of them
    /// stopped before it had seen everything, so a listing missing from their results is a listing
    /// nobody looked for. That is the same rule the
    /// <see cref="SupplierPriceFreshnessPolicy">freshness</see> model and the report availability
    /// model both state - an absence is only evidence when the search that produced it was
    /// complete.</para>
    /// </summary>
    public static bool RecordsListingsNotFound(PriceCollectorWorkItemOutcome outcome) =>
        outcome is PriceCollectorWorkItemOutcome.Complete or PriceCollectorWorkItemOutcome.NoMatches;

    /// <summary>
    /// Refuses an answer a collector may not give about one search: an undeclared outcome, the
    /// completion's own <see cref="PriceCollectorWorkItemOutcome.NotSearched"/>, a failure reason
    /// that does not belong with the outcome, and a "nothing found" answer that contradicts the rows
    /// already submitted.
    /// </summary>
    /// <exception cref="DomainValidationException">The answer is not one this search may be given.</exception>
    public static void RequireReportableAnswer(
        PriceCollectorWorkItemOutcome outcome,
        PriceCollectorSearchFailureReason? failureReason,
        int submittedRowCount)
    {
        if (!IsCollectorReportable(outcome))
        {
            throw new DomainValidationException(UnreportableOutcomeMessage);
        }

        if ((outcome == PriceCollectorWorkItemOutcome.Failed) != failureReason.HasValue)
        {
            throw new DomainValidationException(FailureReasonMismatchMessage);
        }

        if (failureReason is { } reason && !Enum.IsDefined(reason))
        {
            throw new DomainValidationException(UnknownFailureReasonMessage);
        }

        if (submittedRowCount < 0)
        {
            throw new DomainValidationException(OutcomeRequiresNoRowsMessage);
        }

        if (RequiresNoRows(outcome) && submittedRowCount != 0)
        {
            throw new DomainValidationException(OutcomeRequiresNoRowsMessage);
        }
    }

    /// <summary>
    /// Refuses a work item that does not describe one piece of work: a product named twice or not at
    /// all, or an outcome that is not a declared one.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The work item is not coherent. Nothing is stored.
    /// </exception>
    public static void RequireConsistentWorkItem(
        long? masterProductId,
        string? waitingKey,
        PriceCollectorWorkItemOutcome? outcome)
    {
        if (!HasExactlyOneProduct(masterProductId, waitingKey))
        {
            throw new DomainValidationException(ExactlyOneProductMessage);
        }

        if (!IsDeclaredOutcome(outcome))
        {
            throw new DomainValidationException(UnknownOutcomeMessage);
        }
    }
}
