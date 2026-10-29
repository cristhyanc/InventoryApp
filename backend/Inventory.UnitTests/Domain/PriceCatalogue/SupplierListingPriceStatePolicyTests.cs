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

    /// <summary>
    /// The chronological rule (issue #937's repair): a listing holding no reading at all takes the
    /// first one, a later reading replaces what is recorded, and an earlier one does not - the
    /// order readings are delivered in says nothing about the order they were read in.
    /// </summary>
    [Theory]
    [InlineData(null, 0, true)]
    [InlineData(0, 1, true)]
    [InlineData(0, -1, false)]
    public void Only_a_later_reading_replaces_the_one_a_listing_holds(
        int? currentOffsetMinutes,
        int candidateOffsetMinutes,
        bool replaces)
    {
        var current = currentOffsetMinutes is { } offset
            ? Checked.AddMinutes(offset)
            : (DateTime?)null;

        Assert.Equal(
            replaces,
            SupplierListingPriceStatePolicy.ReplacesCurrentReading(
                current,
                Checked.AddMinutes(candidateOffsetMinutes)));
    }

    /// <summary>
    /// And two readings bearing the same observed instant are the same check as far as this
    /// catalogue can tell, so the one already recorded stands: equal is not later, stated once here
    /// rather than decided by whichever row was processed last.
    /// </summary>
    [Fact]
    public void A_reading_observed_at_the_same_instant_does_not_replace_the_recorded_one()
    {
        Assert.False(SupplierListingPriceStatePolicy.ReplacesCurrentReading(Checked, Checked));
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

    /// <summary>
    /// A run's accepted readings are replayed in the order they were observed, so the state they
    /// leave is the same whichever order they were delivered in: the latest observed is current and
    /// the change is the first reading of the trailing stretch at that price.
    /// </summary>
    [Fact]
    public void A_runs_readings_are_replayed_in_observed_order_whatever_order_they_arrive_in()
    {
        AcceptedSupplierReading earlier = new(7, Checked, 20m);
        AcceptedSupplierReading later = new(3, Checked.AddHours(1), 50m);

        var forwards = SupplierListingPriceStatePolicy.StateAfterRun(30m, Checked.AddDays(-1), [earlier, later]);
        var backwards = SupplierListingPriceStatePolicy.StateAfterRun(30m, Checked.AddDays(-1), [later, earlier]);

        Assert.Equal(forwards, backwards);
        Assert.Equal(new SupplierListingPriceState(3, Checked.AddHours(1), Checked.AddHours(1)), forwards);
    }

    /// <summary>A price moved and then re-read unchanged changed at the first of the two instants.</summary>
    [Fact]
    public void A_price_moved_then_re_read_changed_at_its_first_reading()
    {
        var state = SupplierListingPriceStatePolicy.StateAfterRun(
            30m,
            Checked.AddDays(-1),
            [new(2, Checked.AddHours(1), 33m), new(1, Checked, 33m)]);

        Assert.Equal(new SupplierListingPriceState(2, Checked.AddHours(1), Checked), state);
    }

    /// <summary>A run that only re-read the price it started with leaves the change where it was.</summary>
    [Fact]
    public void A_run_that_found_the_same_price_leaves_the_change_where_it_was()
    {
        var state = SupplierListingPriceStatePolicy.StateAfterRun(
            30m,
            Checked.AddDays(-3),
            [new(4, Checked, 30m)]);

        Assert.Equal(new SupplierListingPriceState(4, Checked, Checked.AddDays(-3)), state);
    }

    /// <summary>A listing's first accepted price is its first change.</summary>
    [Fact]
    public void A_first_accepted_price_is_a_change()
    {
        var state = SupplierListingPriceStatePolicy.StateAfterRun(null, null, [new(9, Checked, 30m)]);

        Assert.Equal(new SupplierListingPriceState(9, Checked, Checked), state);
    }

    /// <summary>Of two readings bearing the same instant, the one recorded first stands.</summary>
    [Fact]
    public void Of_two_readings_at_one_instant_the_one_recorded_first_is_current()
    {
        var state = SupplierListingPriceStatePolicy.StateAfterRun(
            30m,
            Checked.AddDays(-1),
            [new(6, Checked, 31m), new(5, Checked, 32m)]);

        Assert.Equal(5, state.LatestObservationId);
    }

    /// <summary>
    /// A reading that lost its instant to the one recorded first never was the price, so it does
    /// not move the change instant: 30 before the run, then 30 (recorded first) and 40 at one
    /// instant, leaves the price unchanged since before the run.
    /// </summary>
    [Fact]
    public void A_reading_that_lost_a_tied_instant_does_not_change_the_price()
    {
        var before = Checked.AddDays(-1);

        var state = SupplierListingPriceStatePolicy.StateAfterRun(
            30m,
            before,
            [new(12, Checked, 40m), new(11, Checked, 30m)]);

        Assert.Equal(new SupplierListingPriceState(11, Checked, before), state);
    }

    /// <summary>And a genuine change after the tied instant is still the change.</summary>
    [Fact]
    public void A_change_after_a_tied_instant_is_still_recorded()
    {
        var state = SupplierListingPriceStatePolicy.StateAfterRun(
            30m,
            Checked.AddDays(-1),
            [new(12, Checked, 40m), new(11, Checked, 30m), new(13, Checked.AddMinutes(30), 35m)]);

        Assert.Equal(
            new SupplierListingPriceState(13, Checked.AddMinutes(30), Checked.AddMinutes(30)),
            state);
    }
}
