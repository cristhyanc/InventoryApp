using Inventory.Domain.Exceptions;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// What a Super Admin decided about one waiting product (issue #928, parent #817): which master
/// product it is, that it is none of them, or that a suggestion was wrong.
///
/// <para>Five answers, because they are five different things to do and a surface that merged any two
/// of them would leave a reviewer unable to say which they meant. Numbered from 1; the decision is
/// reported and acted on rather than stored - what the database keeps is the mappings' own
/// <see cref="ProductPriceTrackingStatus"/> and the suggestion rows' own
/// <see cref="ProductMatchSuggestionStatus"/>.</para>
/// </summary>
public enum ProductMatchReviewDecision
{
    /// <summary>
    /// Accept the matcher's own tentative suggestion: either the existing master product it named, or
    /// the new one it described, which is created from that description and then matched.
    /// </summary>
    AcceptSuggestion = 1,

    /// <summary>
    /// Match the key to a different existing master product. Every tentative suggestion of that key
    /// naming another master product is refused by the same decision, because choosing one product is
    /// saying the others were wrong.
    /// </summary>
    ChooseMasterProduct = 2,

    /// <summary>
    /// Create a master product from values the reviewer submitted - prefilled from a suggestion on
    /// the page, but the reviewer's own - and match the key to it.
    /// </summary>
    CreateMasterProduct = 3,

    /// <summary>
    /// Refuse one tentative suggestion and leave the key waiting. Kept for ever, which is what stops
    /// the matcher applying that pair again (<see cref="ProductMatchSuggestionPolicy.BlocksAutomaticMatching"/>).
    /// </summary>
    RejectSuggestion = 4,

    /// <summary>
    /// Record that nothing in the catalogue is this product: every mapping still awaiting a match
    /// under the key becomes <see cref="ProductPriceTrackingStatus.NotFound"/>, which is a recorded
    /// answer rather than a gap and is what stops the same search running every run.
    /// </summary>
    MarkNotFound = 5,
}

/// <summary>
/// What a Super Admin decided about one automatic match (issue #928): that it was right, or that it
/// was not.
/// </summary>
public enum AutomaticMatchReviewDecision
{
    /// <summary>
    /// Confirm it. The mappings it applied to become <see cref="ProductPriceTrackingStatus.Matched"/>
    /// and the suggestion becomes <see cref="ProductMatchSuggestionStatus.Accepted"/>.
    /// </summary>
    Confirm = 1,

    /// <summary>
    /// Undo it. The mappings return to awaiting a match, the listings the key had collected move back
    /// to it, and the pair is recorded <see cref="ProductMatchSuggestionStatus.Undone"/> so it is
    /// never applied automatically again.
    /// </summary>
    Undo = 2,
}

/// <summary>
/// The rules of the Super Admin's product matching and automatic-match review (issue #928, parent
/// #817): which suggestions may be decided, which mappings a decision reaches, and what an undo puts
/// back.
///
/// <para><strong>A match is decided for a waiting key, not for a business.</strong> The key is the
/// folded search name and unit label several businesses' products share, which is what makes one
/// product one search (#923), so one decision necessarily moves every mapping that carries that key -
/// and the reviewer is never told whose they are. That is why this policy speaks only of statuses and
/// keys: there is no business anywhere in it to leak.</para>
///
/// <para><strong>An undo is narrower than the match it reverses, deliberately.</strong> It returns the
/// mappings the automatic match itself moved and the listings that are still only proposals; a
/// listing somebody confirmed, a mapping a person decided and a mapping a later run settled are left
/// exactly as they are, because an undo of a matcher's guess must not undo a decision a person made.
/// </para>
///
/// <para><strong>It stores nothing and reads nothing</strong>, like every policy in this folder.</para>
/// </summary>
public static class ProductMatchReviewPolicy
{
    /// <summary>
    /// The label a suggestion row written by this review carries in
    /// <c>ProductMatchSuggestion.CollectorRun</c>.
    ///
    /// <para>That column is opaque text nothing parses, and it says <em>who</em> recorded the row
    /// rather than which collector run produced it - the results endpoint writes its own label on the
    /// evidence it records, and this is the Super Admin's. It matters because a decision has to leave
    /// a row that says "this key is that master product": the results endpoint resolves a waiting key
    /// to its master product through exactly those <see cref="ProductMatchSuggestionStatus.Accepted"/>
    /// and <see cref="ProductMatchSuggestionStatus.AutoApplied"/> rows (#937), so a match that left
    /// none would be invisible to the run that is searching while it is made.</para>
    /// </summary>
    public const string ReviewCollectorRun = "super-admin-review";

    /// <summary>The reason recorded on the suggestion row a reviewer's own match writes.</summary>
    public const string ChosenByReviewerReason =
        "A Super Admin matched this waiting key to this master product.";

    /// <summary>The reason recorded when a reviewer created the master product they matched to.</summary>
    public const string CreatedByReviewerReason =
        "A Super Admin created this master product and matched this waiting key to it.";

    /// <summary>
    /// The reason recorded on a suggestion refused because the reviewer matched the key to another
    /// master product instead. It is a refusal of the pair and nothing else - the key is still
    /// matched, to something else.
    /// </summary>
    public const string SupersededByAnotherMatchReason =
        "A Super Admin matched this waiting key to another master product.";

    /// <summary>What a reviewer who named a waiting key nothing is waiting under is told.</summary>
    public const string NothingAwaitingKeyMessage =
        "No product is awaiting a match under this waiting key. Re-read the review list: it has been "
            + "matched, recorded as not found, or withdrawn since.";

    /// <summary>What a reviewer who named a suggestion that belongs to another waiting key is told.</summary>
    public const string SuggestionBelongsToAnotherKeyMessage =
        "That match suggestion is about another waiting key.";

    /// <summary>
    /// What a reviewer who tried to accept or refuse a suggestion that is not waiting for a decision
    /// is told. A refusal rather than a repeated write, for the reason a decided listing's is: the
    /// row has moved since the reviewer read it.
    /// </summary>
    public const string SuggestionNotAwaitingReviewMessage =
        "That match suggestion is not waiting for a decision. Re-read the review list: it has been "
            + "accepted, refused or undone since.";

    /// <summary>
    /// What a reviewer who tried to confirm or undo something that is not an automatic match is told.
    /// </summary>
    public const string NotAnAutomaticMatchMessage =
        "That match is not an automatic match waiting to be checked. Re-read the review list: it has "
            + "been confirmed or undone since.";

    /// <summary>What a reviewer who named a master product this catalogue does not hold is told.</summary>
    public const string UnknownMasterProductMessage = "No such master product.";

    /// <summary>
    /// What a reviewer who tried to create a master product another row already describes is told.
    ///
    /// <para>One description is one master product - a unique index over
    /// <c>MasterProduct.NormalisedKey</c>, because two master products for one product is what the
    /// parent issue exists to prevent - so the answer is to match this key to the row that already
    /// exists, which is what the sentence says.</para>
    /// </summary>
    public const string DuplicateMasterProductMessage =
        "Another master product already describes this brand, name and size. Match this product to "
            + "that one instead of creating a second.";

    /// <summary>
    /// What a reviewer who submitted a decision whose values do not go together is told - a chosen
    /// master product with no id, a created one with no description, or a suggestion decision naming
    /// no suggestion.
    /// </summary>
    public const string IncompleteDecisionMessage =
        "A match decision carries exactly what it decides: a suggestion to accept or refuse, an "
            + "existing master product to match, or a new master product to create.";

    /// <summary>Whether <paramref name="decision"/> is one of the declared decisions.</summary>
    public static bool IsDeclaredDecision(ProductMatchReviewDecision decision) =>
        Enum.IsDefined(decision);

    /// <summary>Whether <paramref name="decision"/> is one of the declared automatic-match decisions.</summary>
    public static bool IsDeclaredDecision(AutomaticMatchReviewDecision decision) =>
        Enum.IsDefined(decision);

    /// <summary>
    /// Whether <paramref name="decision"/> ends with the key matched to a master product - the three
    /// that do, as against refusing a suggestion and recording "not found".
    /// </summary>
    public static bool AppliesAMatch(ProductMatchReviewDecision decision) =>
        decision is ProductMatchReviewDecision.AcceptSuggestion
            or ProductMatchReviewDecision.ChooseMasterProduct
            or ProductMatchReviewDecision.CreateMasterProduct;

    /// <summary>
    /// Whether a suggestion in <paramref name="status"/> may be accepted or refused by a reviewer:
    /// <see cref="ProductMatchSuggestionStatus.Tentative"/> alone, which is exactly what
    /// <see cref="ProductMatchSuggestionPolicy.AwaitsReview"/> says is waiting for them.
    /// </summary>
    public static bool MayDecide(ProductMatchSuggestionStatus status) =>
        ProductMatchSuggestionPolicy.AwaitsReview(status);

    /// <summary>
    /// Whether a suggestion in <paramref name="status"/> may be confirmed or undone:
    /// <see cref="ProductMatchSuggestionStatus.AutoApplied"/> alone. An accepted one is already a
    /// person's decision, and an undone or refused one has already been taken back.
    /// </summary>
    public static bool MayConfirmOrUndo(ProductMatchSuggestionStatus status) =>
        status is ProductMatchSuggestionStatus.AutoApplied;

    /// <summary>
    /// Whether a mapping in <paramref name="status"/> is one a reviewer's match or "not found" may
    /// change: <see cref="ProductPriceTrackingStatus.Requested"/> alone, which is the same set an
    /// automatic match may change (#941).
    ///
    /// <para>A mapping a person already decided, one a run matched, one already recorded as not found
    /// and one suspended on its own product's details are all left exactly as they are - and a mapping
    /// whose product has been renamed or relabelled since carries another key now, so it is not in the
    /// set at all.</para>
    /// </summary>
    public static bool MayMatch(ProductPriceTrackingStatus status) =>
        status is ProductPriceTrackingStatus.Requested;

    /// <summary>
    /// Whether an undo of an automatic match to <paramref name="undoneMasterProductId"/> returns this
    /// mapping to awaiting a match: it is <see cref="ProductPriceTrackingStatus.AutoMatched"/> and it
    /// names that master product.
    ///
    /// <para>Both halves matter. The status, because a mapping a person confirmed is
    /// <see cref="ProductPriceTrackingStatus.Matched"/> and an undo of a matcher's guess must not take
    /// a person's decision back. The master product, because a mapping auto-matched to something else
    /// - by a later run, under the same key - was never this suggestion's to move.</para>
    /// </summary>
    public static bool ReturnsToWaiting(
        ProductPriceTrackingStatus status,
        long? masterProductId,
        long undoneMasterProductId) =>
        status is ProductPriceTrackingStatus.AutoMatched && masterProductId == undoneMasterProductId;

    /// <summary>
    /// Whether an undo moves this listing back to the waiting key it was discovered under:
    /// <see cref="SupplierListingMatchStatus.Proposed"/> alone, which is the issue's own wording
    /// ("proposed listings that came from that key move back to it, confirmed listings are
    /// untouched").
    ///
    /// <para>A <see cref="SupplierListingMatchStatus.Confirmed"/> listing is a person's own decision
    /// about that pack and is left where it is, and so is a
    /// <see cref="SupplierListingMatchStatus.NeedsReview"/> one, which is a confirmed listing whose
    /// shelf moved rather than a proposal. A <see cref="SupplierListingMatchStatus.Rejected"/> one is
    /// a person's refusal, kept for the same reason. The cost is stated plainly: a confirmed listing
    /// stays on a master product the key no longer matches, which is correct - somebody decided that
    /// listing <em>is</em> that product, and undoing a guess about which product a business's
    /// <em>search</em> was for says nothing about that.</para>
    /// </summary>
    public static bool MovesBackOnUndo(SupplierListingMatchStatus status) =>
        status is SupplierListingMatchStatus.Proposed;

    /// <summary>Refuses a decision about a suggestion that is not waiting for one.</summary>
    /// <exception cref="DomainConflictException">
    /// The suggestion has already been accepted, refused, undone or applied. Nothing is written.
    /// </exception>
    public static void RequireMayDecide(ProductMatchSuggestionStatus status)
    {
        if (!MayDecide(status))
        {
            throw new DomainConflictException(SuggestionNotAwaitingReviewMessage);
        }
    }

    /// <summary>Refuses a confirm or undo of something that is not an automatic match.</summary>
    /// <exception cref="DomainConflictException">
    /// The suggestion is not <see cref="ProductMatchSuggestionStatus.AutoApplied"/>. Nothing is
    /// written.
    /// </exception>
    public static void RequireMayConfirmOrUndo(ProductMatchSuggestionStatus status)
    {
        if (!MayConfirmOrUndo(status))
        {
            throw new DomainConflictException(NotAnAutomaticMatchMessage);
        }
    }
}
