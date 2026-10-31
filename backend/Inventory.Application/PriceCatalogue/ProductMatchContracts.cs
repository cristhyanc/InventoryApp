using Inventory.Domain.PriceCatalogue;

namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// One match proposal a collector is submitting (issue #941, parent #817): the waiting key it is
/// about, what the matcher thinks that key is, how sure it is, and why.
///
/// <para><strong>It names a waiting key and no product of anybody's.</strong> The key is the folded
/// search name and unit label the work list published (#924), which several businesses' spellings of
/// one product fold to - so a proposal says something about a <em>product description</em> and nothing
/// about whose product produced it. There is no business id, no product id and no mapping id here, and
/// there is nowhere for one to travel.</para>
///
/// <para><strong>Exactly one proposal per row</strong>, which is <c>ProductMatchSuggestion</c>'s own
/// rule: either <see cref="MasterProductId"/> names a master product that already exists, or the four
/// <c>Suggested*</c> values describe a new one for the Super Admin to decide on (#928). A suggested
/// new master product is never created automatically - that is this issue's own exclusion.</para>
/// </summary>
/// <param name="WaitingKey">The waiting key, as the work list published it.</param>
/// <param name="MasterProductId">The existing master product proposed, or <see langword="null"/>.</param>
/// <param name="SuggestedBrand">The brand of a new master product being suggested, or <see langword="null"/>.</param>
/// <param name="SuggestedName">Its name or variant, or <see langword="null"/>.</param>
/// <param name="SuggestedSizeValue">The size of one item, or <see langword="null"/>.</param>
/// <param name="SuggestedSizeUnit">What that size is measured in, or <see langword="null"/>.</param>
/// <param name="Confidence">How sure the matcher is: high, medium or low, and nothing else.</param>
/// <param name="Reason">
/// The matcher's own short reason, which is the evidence #928's reviewer reads. Required, because a
/// suggestion nobody can account for is one a reviewer can only guess at.
/// </param>
public sealed record ProductMatchProposal(
    string? WaitingKey,
    long? MasterProductId,
    string? SuggestedBrand,
    string? SuggestedName,
    decimal? SuggestedSizeValue,
    MasterProductSizeUnit? SuggestedSizeUnit,
    ProductMatchConfidence Confidence,
    string? Reason);

/// <summary>
/// One batch of match proposals for one run (issue #941): at most
/// <see cref="AutomaticProductMatchPolicy.MaxProposalsPerBatch"/> of them, one per waiting key.
/// </summary>
public sealed record ProductMatchBatch(long RunId, IReadOnlyList<ProductMatchProposal> Proposals);

/// <summary>
/// What became of one submitted proposal (issue #941).
///
/// <para>Three outcomes, because "we applied it", "a person will look at it" and "we could not use
/// this at all" are three different facts, and a collector reads them for three different purposes.
/// </para>
/// </summary>
public enum ProductMatchProposalOutcome
{
    /// <summary>
    /// Applied automatically: the mappings still awaiting a match under this key now name the master
    /// product, and the suggestion is recorded as auto-applied for a person to undo (#928).
    /// </summary>
    AutoMatched = 1,

    /// <summary>
    /// Stored as a tentative match for the Super Admin - an existing master product, or a suggested
    /// new one. The mappings stay awaiting a match and nothing shared was linked.
    /// </summary>
    Tentative = 2,

    /// <summary>
    /// Not usable, with a reason: an unknown run key, an unknown master product, a pair somebody
    /// already refused, or a proposal this catalogue cannot store. Nothing was written.
    /// </summary>
    Rejected = 3,
}

/// <summary>What became of one proposal, as the response reports it (issue #941).</summary>
/// <param name="WaitingKey">The key the proposal was about, as submitted.</param>
/// <param name="Outcome">What was done with it.</param>
/// <param name="Reason">
/// Why, for everything but a fresh automatic match - the first condition that refused it, which is
/// what a collector reads to understand what the backend decided.
/// </param>
/// <param name="MasterProductId">
/// The master product involved, when the proposal named one: applied for an automatic match, proposed
/// for a tentative one, and <see langword="null"/> for a suggested new master product.
/// </param>
/// <param name="SuggestionId">
/// The suggestion row this proposal is recorded as, when one was recorded or already existed. A
/// platform row key, which is a fact about a product description and about no business.
/// </param>
public sealed record ProductMatchProposalResult(
    string WaitingKey,
    ProductMatchProposalOutcome Outcome,
    string? Reason,
    long? MasterProductId,
    long? SuggestionId);

/// <summary>
/// The answer to one submitted batch of proposals (issue #941): its proposals in three lists, which
/// are the three outcomes.
///
/// <para><strong>Nothing in it says anything about a business</strong>, which is the issue's own
/// criterion stated as a type. In particular it carries <em>no count of the mappings an automatic
/// match changed</em>: that count is the number of businesses tracking the product, which is exactly
/// what the shared catalogue exists to avoid telling anybody.</para>
/// </summary>
public sealed record ProductMatchReceipt(
    long RunId,
    IReadOnlyList<ProductMatchProposalResult> AutoMatched,
    IReadOnlyList<ProductMatchProposalResult> Tentative,
    IReadOnlyList<ProductMatchProposalResult> Rejected);
