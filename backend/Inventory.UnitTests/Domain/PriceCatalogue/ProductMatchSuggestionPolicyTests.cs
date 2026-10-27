using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace Inventory.UnitTests.Domain.PriceCatalogue;

/// <summary>
/// The rules of one suggested match (issue #923, for the matching of #941 and the review of #928):
/// one proposal per row, a confidence that is a proportion, and the two states that must never be
/// acted on again.
/// </summary>
public class ProductMatchSuggestionPolicyTests
{
    /// <summary>
    /// The criterion this table exists for: a rejected or undone pair is never auto-applied again.
    /// Asserted per state, so a sixth status cannot quietly join the group that is still fair game.
    /// </summary>
    [Theory]
    [InlineData(ProductMatchSuggestionStatus.Tentative, false)]
    [InlineData(ProductMatchSuggestionStatus.AutoApplied, false)]
    [InlineData(ProductMatchSuggestionStatus.Accepted, false)]
    [InlineData(ProductMatchSuggestionStatus.Rejected, true)]
    [InlineData(ProductMatchSuggestionStatus.Undone, true)]
    public void A_rejected_or_undone_suggestion_blocks_automatic_matching(
        ProductMatchSuggestionStatus status,
        bool blocks) =>
        Assert.Equal(blocks, ProductMatchSuggestionPolicy.BlocksAutomaticMatching(status));

    #region One proposal

    [Fact]
    public void A_suggestion_may_name_an_existing_master_product() =>
        ProductMatchSuggestionPolicy.RequireExactlyOneProposal(
            masterProductId: 7L,
            suggestedBrand: null,
            suggestedName: null,
            suggestedSize: null,
            suggestedSizeUnit: null);

    [Fact]
    public void A_suggestion_may_describe_a_new_master_product() =>
        ProductMatchSuggestionPolicy.RequireExactlyOneProposal(
            masterProductId: null,
            "Coca-Cola",
            "Classic",
            375m,
            MasterProductSizeUnit.Millilitre);

    /// <summary>
    /// Both at once is two different things to do about one waiting key, and the reviewer of #928
    /// would have no way to know which was meant.
    /// </summary>
    [Fact]
    public void A_suggestion_that_does_both_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => ProductMatchSuggestionPolicy.RequireExactlyOneProposal(
                masterProductId: 7L,
                "Coca-Cola",
                "Classic",
                375m,
                MasterProductSizeUnit.Millilitre));

        Assert.Equal(ProductMatchSuggestionPolicy.OneProposalRequiredMessage, refusal.Message);
    }

    [Fact]
    public void A_suggestion_that_does_neither_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => ProductMatchSuggestionPolicy.RequireExactlyOneProposal(
                masterProductId: null,
                suggestedBrand: null,
                suggestedName: null,
                suggestedSize: null,
                suggestedSizeUnit: null));

        Assert.Equal(ProductMatchSuggestionPolicy.OneProposalRequiredMessage, refusal.Message);
    }

    /// <summary>
    /// Half a description is refused with its own sentence, because "both or neither" and "you left
    /// the size out" are different mistakes and a reviewer needs to know which they made.
    /// </summary>
    [Theory]
    [InlineData(null, "Classic", 375d, MasterProductSizeUnit.Millilitre)]
    [InlineData("Coca-Cola", null, 375d, MasterProductSizeUnit.Millilitre)]
    [InlineData("Coca-Cola", "Classic", null, MasterProductSizeUnit.Millilitre)]
    [InlineData("Coca-Cola", "Classic", 375d, null)]
    public void An_incompletely_described_new_master_product_is_refused(
        string? suggestedBrand,
        string? suggestedName,
        double? suggestedSize,
        MasterProductSizeUnit? suggestedSizeUnit)
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => ProductMatchSuggestionPolicy.RequireExactlyOneProposal(
                masterProductId: null,
                suggestedBrand,
                suggestedName,
                (decimal?)suggestedSize,
                suggestedSizeUnit));

        Assert.Equal(ProductMatchSuggestionPolicy.IncompleteSuggestedMasterMessage, refusal.Message);
    }

    #endregion

    #region Confidence, reason and collector run

    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(0.8742)]
    [InlineData(1)]
    public void A_confidence_in_the_unit_interval_is_stored_as_submitted(decimal confidence) =>
        Assert.Equal(confidence, ProductMatchSuggestionPolicy.NormaliseConfidence(confidence));

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void A_confidence_outside_the_unit_interval_is_refused(decimal confidence)
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => ProductMatchSuggestionPolicy.NormaliseConfidence(confidence));

        Assert.Equal(ProductMatchSuggestionPolicy.ConfidenceOutOfRangeMessage, refusal.Message);
    }

    [Fact]
    public void A_confidence_finer_than_the_stored_scale_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => ProductMatchSuggestionPolicy.NormaliseConfidence(0.87425m));

        Assert.Equal(ProductMatchSuggestionPolicy.ConfidenceTooPreciseMessage, refusal.Message);
    }

    [Fact]
    public void A_reason_and_a_collector_run_are_trimmed()
    {
        Assert.Equal(
            "Exact brand, name and size",
            ProductMatchSuggestionPolicy.NormaliseReason("  Exact brand, name and size "));
        Assert.Equal(
            "run-2026-10-27-01",
            ProductMatchSuggestionPolicy.NormaliseCollectorRun(" run-2026-10-27-01 "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void A_suggestion_without_a_reason_is_refused(string? reason)
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => ProductMatchSuggestionPolicy.NormaliseReason(reason));

        Assert.Equal(ProductMatchSuggestionPolicy.ReasonRequiredMessage, refusal.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void A_suggestion_without_a_collector_run_is_refused(string? collectorRun)
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => ProductMatchSuggestionPolicy.NormaliseCollectorRun(collectorRun));

        Assert.Equal(ProductMatchSuggestionPolicy.CollectorRunRequiredMessage, refusal.Message);
    }

    [Fact]
    public void A_reason_longer_than_the_limit_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => ProductMatchSuggestionPolicy.NormaliseReason(
                new string('r', ProductMatchSuggestionPolicy.MaxReasonLength + 1)));

        Assert.Equal(ProductMatchSuggestionPolicy.ReasonTooLongMessage, refusal.Message);
    }

    [Fact]
    public void A_collector_run_longer_than_the_limit_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => ProductMatchSuggestionPolicy.NormaliseCollectorRun(
                new string('c', ProductMatchSuggestionPolicy.MaxCollectorRunLength + 1)));

        Assert.Equal(ProductMatchSuggestionPolicy.CollectorRunTooLongMessage, refusal.Message);
    }

    #endregion
}
