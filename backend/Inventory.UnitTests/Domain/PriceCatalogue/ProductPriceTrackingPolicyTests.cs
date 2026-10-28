using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace Inventory.UnitTests.Domain.PriceCatalogue;

/// <summary>
/// The consistency rules of one business's price-tracking mapping (issue #923): a mapping names a
/// master product exactly when it is matched, and records how that was decided exactly when it names
/// one.
///
/// <para>Both rules are about combinations rather than values, so the cases are the combinations -
/// including the two that would otherwise be stored happily and read as nonsense: a matched mapping
/// pointing at nothing, and a mapping that records how an undecided match was made.</para>
/// </summary>
public class ProductPriceTrackingPolicyTests
{
    /// <summary>
    /// Exactly the two matched states are matched; the three unmatched ones - including
    /// <c>AwaitingProductDetails</c>, the suspension of issue #938's repair - are not. Asserted per
    /// state so a later status cannot quietly join the matched group, which is what decides whether a
    /// mapping may name a master product at all.
    /// </summary>
    [Theory]
    [InlineData(ProductPriceTrackingStatus.Requested, false)]
    [InlineData(ProductPriceTrackingStatus.NotFound, false)]
    [InlineData(ProductPriceTrackingStatus.AwaitingProductDetails, false)]
    [InlineData(ProductPriceTrackingStatus.AutoMatched, true)]
    [InlineData(ProductPriceTrackingStatus.Matched, true)]
    public void A_mapping_is_matched_in_exactly_the_two_matched_states(
        ProductPriceTrackingStatus status,
        bool matched) =>
        Assert.Equal(matched, ProductPriceTrackingPolicy.IsMatched(status));

    [Theory]
    [InlineData(ProductPriceTrackingStatus.Requested, null, null)]
    [InlineData(ProductPriceTrackingStatus.NotFound, null, null)]
    [InlineData(ProductPriceTrackingStatus.AwaitingProductDetails, null, null)]
    [InlineData(ProductPriceTrackingStatus.AutoMatched, 7L, ProductPriceTrackingMatchSource.CollectorAutomaticMatch)]
    [InlineData(ProductPriceTrackingStatus.Matched, 7L, ProductPriceTrackingMatchSource.SuperAdmin)]
    [InlineData(ProductPriceTrackingStatus.Matched, 7L, ProductPriceTrackingMatchSource.CollectorAutomaticMatch)]
    public void A_consistent_mapping_is_accepted(
        ProductPriceTrackingStatus status,
        long? masterProductId,
        ProductPriceTrackingMatchSource? matchSource)
    {
        var refusal = Record.Exception(
            () => ProductPriceTrackingPolicy.RequireConsistentMatch(status, masterProductId, matchSource));

        Assert.Null(refusal);
    }

    /// <summary>
    /// A matched mapping that names no master product is refused. It is the state that reads as
    /// "priced as something" while pointing at nothing, which a later price comparison would have to
    /// interpret.
    /// </summary>
    [Fact]
    public void A_matched_mapping_without_a_master_product_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => ProductPriceTrackingPolicy.RequireConsistentMatch(
                ProductPriceTrackingStatus.Matched,
                masterProductId: null,
                ProductPriceTrackingMatchSource.SuperAdmin));

        Assert.Equal(ProductPriceTrackingPolicy.MasterProductStatusMismatchMessage, refusal.Message);
    }

    /// <summary>And a waiting mapping that names one, which is a match nobody recorded making.</summary>
    [Fact]
    public void A_waiting_mapping_that_names_a_master_product_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => ProductPriceTrackingPolicy.RequireConsistentMatch(
                ProductPriceTrackingStatus.Requested,
                masterProductId: 7L,
                ProductPriceTrackingMatchSource.SuperAdmin));

        Assert.Equal(ProductPriceTrackingPolicy.MasterProductStatusMismatchMessage, refusal.Message);
    }

    /// <summary>
    /// And so is a <strong>suspended</strong> mapping that still names one (issue #938, repaired on
    /// review): a mapping whose product can no longer be searched for is matched to nothing, and this
    /// is the rule that stops an applier which forgot to clear the match from storing one that would
    /// still supply prices for the product's previous size.
    /// </summary>
    [Fact]
    public void A_suspended_mapping_that_names_a_master_product_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => ProductPriceTrackingPolicy.RequireConsistentMatch(
                ProductPriceTrackingStatus.AwaitingProductDetails,
                masterProductId: 7L,
                ProductPriceTrackingMatchSource.CollectorAutomaticMatch));

        Assert.Equal(ProductPriceTrackingPolicy.MasterProductStatusMismatchMessage, refusal.Message);
    }

    /// <summary>
    /// A match with no recorded source is refused: "who decided this?" is the first question asked of
    /// a product that is being priced as something else.
    /// </summary>
    [Fact]
    public void A_match_without_a_recorded_source_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => ProductPriceTrackingPolicy.RequireConsistentMatch(
                ProductPriceTrackingStatus.AutoMatched,
                masterProductId: 7L,
                matchSource: null));

        Assert.Equal(ProductPriceTrackingPolicy.MatchSourceMismatchMessage, refusal.Message);
    }

    /// <summary>And a recorded source with nothing to be the source of.</summary>
    [Fact]
    public void A_recorded_source_without_a_match_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => ProductPriceTrackingPolicy.RequireConsistentMatch(
                ProductPriceTrackingStatus.NotFound,
                masterProductId: null,
                ProductPriceTrackingMatchSource.CollectorAutomaticMatch));

        Assert.Equal(ProductPriceTrackingPolicy.MatchSourceMismatchMessage, refusal.Message);
    }

    /// <summary>
    /// Both vocabularies are closed and checked, for the reason every declared vocabulary in this
    /// application is: a deserialized JSON number binds an arbitrary integer.
    /// </summary>
    [Fact]
    public void An_undeclared_status_is_refused() =>
        Assert.Throws<DomainValidationException>(
            () => ProductPriceTrackingPolicy.RequireConsistentMatch(
                (ProductPriceTrackingStatus)9,
                masterProductId: null,
                matchSource: null));

    [Fact]
    public void An_undeclared_match_source_is_refused() =>
        Assert.Throws<DomainValidationException>(
            () => ProductPriceTrackingPolicy.RequireConsistentMatch(
                ProductPriceTrackingStatus.Matched,
                masterProductId: 7L,
                (ProductPriceTrackingMatchSource)9));
}
