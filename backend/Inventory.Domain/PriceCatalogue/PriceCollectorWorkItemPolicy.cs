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

    /// <summary>Whether <paramref name="outcome"/> is empty or one of the declared outcomes.</summary>
    public static bool IsDeclaredOutcome(PriceCollectorWorkItemOutcome? outcome) =>
        outcome is null || Enum.IsDefined(outcome.Value);

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
