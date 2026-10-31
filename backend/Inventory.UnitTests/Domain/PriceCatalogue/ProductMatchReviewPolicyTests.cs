using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace Inventory.UnitTests.Domain.PriceCatalogue;

/// <summary>
/// The rules of the Super Admin's product matching and automatic-match review (issue #928, parent
/// #817): which suggestions may be decided, which mappings a decision reaches, and what an undo puts
/// back.
///
/// <para>Pure functions over statuses and keys, which is the point: there is no business anywhere in
/// this policy to leak, because a match is decided for a folded product description.</para>
/// </summary>
public class ProductMatchReviewPolicyTests
{
    [Theory]
    [InlineData(ProductMatchReviewDecision.AcceptSuggestion, true)]
    [InlineData(ProductMatchReviewDecision.ChooseMasterProduct, true)]
    [InlineData(ProductMatchReviewDecision.CreateMasterProduct, true)]
    [InlineData(ProductMatchReviewDecision.RejectSuggestion, false)]
    [InlineData(ProductMatchReviewDecision.MarkNotFound, false)]
    public void Three_of_the_five_decisions_apply_a_match(
        ProductMatchReviewDecision decision,
        bool applies)
    {
        Assert.True(ProductMatchReviewPolicy.IsDeclaredDecision(decision));
        Assert.Equal(applies, ProductMatchReviewPolicy.AppliesAMatch(decision));
    }

    /// <summary>
    /// An undeclared decision is not a decision: a deserialized JSON number binds an arbitrary
    /// integer, so the vocabulary is checked rather than assumed.
    /// </summary>
    [Fact]
    public void An_undeclared_decision_is_not_declared()
    {
        Assert.False(ProductMatchReviewPolicy.IsDeclaredDecision((ProductMatchReviewDecision)9));
        Assert.False(ProductMatchReviewPolicy.IsDeclaredDecision((AutomaticMatchReviewDecision)9));
    }

    /// <summary>
    /// Only a tentative suggestion is one a reviewer may accept or refuse, which is exactly what
    /// <see cref="ProductMatchSuggestionPolicy.AwaitsReview"/> says is waiting for them - the record a
    /// run leaves of having repeated somebody else's proposal is nobody's decision to make.
    /// </summary>
    [Theory]
    [InlineData(ProductMatchSuggestionStatus.Tentative, true)]
    [InlineData(ProductMatchSuggestionStatus.AutoApplied, false)]
    [InlineData(ProductMatchSuggestionStatus.Accepted, false)]
    [InlineData(ProductMatchSuggestionStatus.Rejected, false)]
    [InlineData(ProductMatchSuggestionStatus.Undone, false)]
    [InlineData(ProductMatchSuggestionStatus.Repeated, false)]
    public void Only_a_tentative_suggestion_may_be_accepted_or_refused(
        ProductMatchSuggestionStatus status,
        bool mayDecide)
    {
        Assert.Equal(mayDecide, ProductMatchReviewPolicy.MayDecide(status));

        if (mayDecide)
        {
            ProductMatchReviewPolicy.RequireMayDecide(status);
            return;
        }

        var refusal = Assert.Throws<DomainConflictException>(() =>
            ProductMatchReviewPolicy.RequireMayDecide(status));
        Assert.Equal(ProductMatchReviewPolicy.SuggestionNotAwaitingReviewMessage, refusal.Message);
    }

    /// <summary>
    /// Only an applied automatic match may be confirmed or undone. An accepted one is already a
    /// person's decision, and an undone or refused one has already been taken back.
    /// </summary>
    [Theory]
    [InlineData(ProductMatchSuggestionStatus.AutoApplied, true)]
    [InlineData(ProductMatchSuggestionStatus.Tentative, false)]
    [InlineData(ProductMatchSuggestionStatus.Accepted, false)]
    [InlineData(ProductMatchSuggestionStatus.Rejected, false)]
    [InlineData(ProductMatchSuggestionStatus.Undone, false)]
    [InlineData(ProductMatchSuggestionStatus.Repeated, false)]
    public void Only_an_applied_automatic_match_may_be_confirmed_or_undone(
        ProductMatchSuggestionStatus status,
        bool mayDecide)
    {
        Assert.Equal(mayDecide, ProductMatchReviewPolicy.MayConfirmOrUndo(status));

        if (mayDecide)
        {
            ProductMatchReviewPolicy.RequireMayConfirmOrUndo(status);
            return;
        }

        var refusal = Assert.Throws<DomainConflictException>(() =>
            ProductMatchReviewPolicy.RequireMayConfirmOrUndo(status));
        Assert.Equal(ProductMatchReviewPolicy.NotAnAutomaticMatchMessage, refusal.Message);
    }

    /// <summary>
    /// A match reaches only the mappings still awaiting one - the same set an automatic match may
    /// change (#941). A mapping a person settled, one a run matched, one already recorded as not found
    /// and one suspended on its own product's details are all left exactly as they are.
    /// </summary>
    [Theory]
    [InlineData(ProductPriceTrackingStatus.Requested, true)]
    [InlineData(ProductPriceTrackingStatus.AutoMatched, false)]
    [InlineData(ProductPriceTrackingStatus.Matched, false)]
    [InlineData(ProductPriceTrackingStatus.NotFound, false)]
    [InlineData(ProductPriceTrackingStatus.AwaitingProductDetails, false)]
    public void Only_a_mapping_awaiting_a_match_is_changed_by_one(
        ProductPriceTrackingStatus status,
        bool mayMatch) =>
        Assert.Equal(mayMatch, ProductMatchReviewPolicy.MayMatch(status));

    /// <summary>
    /// An undo returns the mappings the automatic match itself applied to - auto-matched, and naming
    /// that very master product - and nothing else. A mapping a person confirmed is
    /// <see cref="ProductPriceTrackingStatus.Matched"/>, and an undo of a matcher's guess must not
    /// take a person's decision back; a mapping auto-matched to something else was never this
    /// suggestion's to move.
    /// </summary>
    [Theory]
    [InlineData(ProductPriceTrackingStatus.AutoMatched, 7L, true)]
    [InlineData(ProductPriceTrackingStatus.AutoMatched, 8L, false)]
    [InlineData(ProductPriceTrackingStatus.Matched, 7L, false)]
    [InlineData(ProductPriceTrackingStatus.Requested, null, false)]
    [InlineData(ProductPriceTrackingStatus.NotFound, null, false)]
    public void An_undo_returns_only_what_the_automatic_match_applied_to(
        ProductPriceTrackingStatus status,
        long? masterProductId,
        bool returns) =>
        Assert.Equal(
            returns,
            ProductMatchReviewPolicy.ReturnsToWaiting(status, masterProductId, 7L));

    /// <summary>
    /// An undo moves back the listings that are still only proposals. A confirmed listing is a
    /// person's own decision about that pack, a flagged one is a confirmed listing whose shelf moved,
    /// and a refused one is a refusal - none of them is a guess for an undo to reverse.
    /// </summary>
    [Theory]
    [InlineData(SupplierListingMatchStatus.Proposed, true)]
    [InlineData(SupplierListingMatchStatus.Confirmed, false)]
    [InlineData(SupplierListingMatchStatus.NeedsReview, false)]
    [InlineData(SupplierListingMatchStatus.Rejected, false)]
    public void An_undo_moves_back_only_the_proposals(
        SupplierListingMatchStatus status,
        bool movesBack) =>
        Assert.Equal(movesBack, ProductMatchReviewPolicy.MovesBackOnUndo(status));

    /// <summary>
    /// The label the review's own suggestion rows carry is stored text a suggestion accepts, so the
    /// record of a reviewer's decision cannot be refused by the column that holds it.
    /// </summary>
    [Fact]
    public void The_reviews_own_label_and_reasons_are_values_a_suggestion_row_accepts()
    {
        Assert.Equal(
            ProductMatchReviewPolicy.ReviewCollectorRun,
            ProductMatchSuggestionPolicy.NormaliseCollectorRun(
                ProductMatchReviewPolicy.ReviewCollectorRun));

        Assert.Equal(
            ProductMatchReviewPolicy.ChosenByReviewerReason,
            ProductMatchSuggestionPolicy.NormaliseReason(
                ProductMatchReviewPolicy.ChosenByReviewerReason));

        Assert.Equal(
            ProductMatchReviewPolicy.CreatedByReviewerReason,
            ProductMatchSuggestionPolicy.NormaliseReason(
                ProductMatchReviewPolicy.CreatedByReviewerReason));

        Assert.Equal(1m, ProductMatchSuggestionPolicy.NormaliseConfidence(1m));
    }

    /// <summary>
    /// A refused or undone pair is what <see cref="ProductMatchSuggestionPolicy.BlocksAutomaticMatching"/>
    /// reads, which is why the review writes those two statuses rather than removing the row: the
    /// evidence that a pair was refused has to outlive the refusal, or the next run repeats it.
    /// </summary>
    [Fact]
    public void The_two_refusing_statuses_are_the_ones_that_block_the_matcher()
    {
        Assert.True(ProductMatchSuggestionPolicy.BlocksAutomaticMatching(
            ProductMatchSuggestionStatus.Rejected));
        Assert.True(ProductMatchSuggestionPolicy.BlocksAutomaticMatching(
            ProductMatchSuggestionStatus.Undone));
        Assert.False(ProductMatchSuggestionPolicy.BlocksAutomaticMatching(
            ProductMatchSuggestionStatus.Accepted));
    }
}
