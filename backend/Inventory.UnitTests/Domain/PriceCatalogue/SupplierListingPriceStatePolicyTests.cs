using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace Inventory.UnitTests.Domain.PriceCatalogue;

/// <summary>
/// What one supplier listing records about its own price checks (issue #925): the latest accepted
/// observation, the last successful check and the last price change, which are three facts and not
/// one.
/// </summary>
public class SupplierListingPriceStatePolicyTests
{
    private static readonly DateTime Checked = new(2026, 10, 28, 5, 0, 0, DateTimeKind.Utc);

    /// <summary>Every listing as issue #923 shipped them: never checked, so none of the three is set.</summary>
    [Fact]
    public void A_listing_that_has_never_been_checked_holds_none_of_the_three()
    {
        Assert.True(SupplierListingPriceStatePolicy.IsConsistentState(null, null, null));
        Assert.Null(
            Record.Exception(
                () => SupplierListingPriceStatePolicy.RequireConsistentState(null, null, null)));
    }

    /// <summary>
    /// A checked listing holds an observation and a check instant, and its last price change is a
    /// separate fact: the same price read again moves the check instant and leaves the change where
    /// it was, which is why the two columns exist.
    /// </summary>
    [Fact]
    public void A_checked_listing_may_have_changed_price_or_not()
    {
        Assert.True(SupplierListingPriceStatePolicy.IsConsistentState(5L, Checked, null));
        Assert.True(
            SupplierListingPriceStatePolicy.IsConsistentState(5L, Checked, Checked.AddDays(-3)));
    }

    /// <summary>
    /// An observation with no check instant, or a check instant with no observation, is refused: each
    /// half is evidence about the other, and a listing in either condition cannot say how old its
    /// price is.
    /// </summary>
    [Theory]
    [InlineData(5L, false)]
    [InlineData(null, true)]
    public void An_observation_and_a_check_instant_are_recorded_together(
        long? latestObservationId,
        bool hasCheckInstant)
    {
        var lastChecked = hasCheckInstant ? Checked : (DateTime?)null;

        Assert.False(
            SupplierListingPriceStatePolicy.IsConsistentState(latestObservationId, lastChecked, null));

        var refusal = Assert.Throws<DomainValidationException>(
            () => SupplierListingPriceStatePolicy.RequireConsistentState(
                latestObservationId,
                lastChecked,
                null));

        Assert.Equal(
            SupplierListingPriceStatePolicy.CheckAndObservationTogetherMessage,
            refusal.Message);
    }

    /// <summary>A price that was never observed cannot have changed.</summary>
    [Fact]
    public void A_price_change_with_no_observation_is_refused()
    {
        Assert.False(SupplierListingPriceStatePolicy.IsConsistentState(null, null, Checked));

        var refusal = Assert.Throws<DomainValidationException>(
            () => SupplierListingPriceStatePolicy.RequireConsistentState(null, null, Checked));

        Assert.Equal(
            SupplierListingPriceStatePolicy.PriceChangeNeedsObservationMessage,
            refusal.Message);
    }
}
