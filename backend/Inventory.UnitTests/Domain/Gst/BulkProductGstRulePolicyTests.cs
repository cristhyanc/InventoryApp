using Inventory.Domain.Exceptions;
using Inventory.Domain.Gst;
using Xunit;

namespace InventoryApi.Tests.Domain.Gst;

/// <summary>
/// The rules of setting a GST rule on every product that has none (issue #1076): which product is
/// eligible, which target rule may be asked for, and when a confirmed bulk change is refused
/// because the set it was confirmed against has moved.
///
/// The target check is the one that cannot be left to the type system. The rule arrives from a JSON
/// body where any integer binds to a <see cref="GstClassification"/>, and <c>Unknown</c> - "no
/// rule" - is a declared value the single-product endpoint legitimately accepts. Here it is not a
/// target at all: "set every unconfigured product to unconfigured" is a write nobody asked for, and
/// it would make a bulk action that reported a changed count while configuring nothing.
/// </summary>
public class BulkProductGstRulePolicyTests
{
    [Theory]
    [InlineData(GstClassification.Taxable)]
    [InlineData(GstClassification.GstFree)]
    public void Taxable_and_gst_free_are_the_two_targets(GstClassification target)
    {
        Assert.Equal(target, BulkProductGstRulePolicy.RequireTarget(target));
    }

    [Fact]
    public void No_rule_is_not_a_target()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => BulkProductGstRulePolicy.RequireTarget(GstRules.None));

        Assert.Equal(BulkProductGstRulePolicy.UnsupportedTargetMessage, refusal.Message);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(999)]
    public void A_value_outside_the_vocabulary_is_not_a_target(int value)
    {
        Assert.Throws<DomainValidationException>(
            () => BulkProductGstRulePolicy.RequireTarget((GstClassification)value));
    }

    /// <summary>
    /// Eligibility is "carries no rule at all". An explicitly configured rule - including an
    /// explicit <c>GstFree</c>, which is a different statement from "no rule" - is somebody's
    /// decision and is never eligible to be replaced.
    /// </summary>
    [Fact]
    public void Only_a_product_with_no_rule_is_eligible()
    {
        Assert.True(BulkProductGstRulePolicy.IsEligible(GstRules.None));
        Assert.False(BulkProductGstRulePolicy.IsEligible(GstClassification.Taxable));
        Assert.False(BulkProductGstRulePolicy.IsEligible(GstClassification.GstFree));
    }

    [Fact]
    public void A_submitted_eligible_count_is_required()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => BulkProductGstRulePolicy.RequireSubmittedEligibleCount(null));

        Assert.Equal(BulkProductGstRulePolicy.ExpectedEligibleCountRequiredMessage, refusal.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(412)]
    public void A_submitted_eligible_count_is_read_as_it_arrived(int submitted)
    {
        Assert.Equal(submitted, BulkProductGstRulePolicy.RequireSubmittedEligibleCount(submitted));
    }

    [Fact]
    public void The_confirmed_count_matching_the_stored_count_is_what_allows_the_write()
    {
        BulkProductGstRulePolicy.RequireUnchangedEligibleCount(expectedCount: 4, actualCount: 4);
    }

    [Theory]
    [InlineData(4, 5)]
    [InlineData(4, 3)]
    [InlineData(0, 1)]
    [InlineData(-1, 0)]
    public void A_count_that_moved_since_the_confirmation_is_a_conflict(int expected, int actual)
    {
        var refusal = Assert.Throws<DomainConflictException>(
            () => BulkProductGstRulePolicy.RequireUnchangedEligibleCount(expected, actual));

        Assert.Equal(BulkProductGstRulePolicy.EligibleCountChangedMessage, refusal.Message);
    }

    /// <summary>
    /// The refusal has to tell the operator what to do next, because the one thing they cannot see
    /// is what somebody else changed.
    /// </summary>
    [Fact]
    public void The_conflict_asks_for_a_fresh_confirmation()
    {
        Assert.Contains("again", BulkProductGstRulePolicy.EligibleCountChangedMessage, StringComparison.OrdinalIgnoreCase);
    }
}
