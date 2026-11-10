using Inventory.Domain.ReceiptScanning;
using Xunit;

namespace InventoryApi.Tests.Domain.ReceiptScanning;

/// <summary>
/// The three levy outcomes (issue #1163): already inside the drink prices, a real extra cost of the
/// drinks, or unresolved - and unresolved changes nothing.
///
/// <para>Every receipt here is invented. Real receipts are never committed as fixtures (issue
/// #1163's known constraints), and these figures are chosen to make one arithmetic question at a
/// time decidable.</para>
/// </summary>
public class ReceiptLevyPolicyTests
{
    /// <summary>
    /// The issue's "already included" criterion, in JB Metro's shape: the drink line, the shipping
    /// charge and the printed total already agree, so the separately printed levy is a disclosure and
    /// nothing is added to any line.
    /// </summary>
    [Fact]
    public void A_levy_the_line_prices_already_carry_is_not_added_again()
    {
        var result = ReceiptLevyPolicy.Reconcile(
            levyAmount: 1.10m,
            printedTotal: 55.00m,
            comparableAmounts: [45.00m, 10.00m],
            drinkLines: [new ReceiptLevyDrinkLine(LineNumber: 1, Units: 10)]);

        Assert.Equal(ReceiptLevyOutcome.AlreadyIncluded, result.Outcome);
        Assert.Empty(result.Allocations);
    }

    /// <summary>
    /// The issue's "not included" criterion: $1.10 that the line prices do not carry, and one drink
    /// line of 10 bottles, so the whole $1.10 becomes part of that line's cost - and not part of the
    /// delivery cost, which is what a reader that treated every extra charge alike would do.
    /// </summary>
    [Fact]
    public void A_levy_the_line_prices_do_not_carry_is_shared_across_the_drink_lines()
    {
        var result = ReceiptLevyPolicy.Reconcile(
            levyAmount: 1.10m,
            printedTotal: 56.10m,
            comparableAmounts: [45.00m, 10.00m],
            drinkLines: [new ReceiptLevyDrinkLine(LineNumber: 1, Units: 10)]);

        Assert.Equal(ReceiptLevyOutcome.AddedToDrinkLines, result.Outcome);
        Assert.Equal([new ReceiptLevyAllocation(1, 1.10m)], result.Allocations);
    }

    /// <summary>
    /// The issue's criterion about an <c>other</c> line: an office item's amount counts towards the
    /// reconciliation, which is what lets the levy be found to be already included on a receipt that
    /// also holds one.
    /// </summary>
    [Fact]
    public void A_non_stock_line_counts_towards_the_reconciliation()
    {
        var withOfficeItem = ReceiptLevyPolicy.Reconcile(
            levyAmount: 1.10m,
            printedTotal: 67.50m,
            comparableAmounts: [45.00m, 10.00m, 12.50m],
            drinkLines: [new ReceiptLevyDrinkLine(1, 10)]);

        Assert.Equal(ReceiptLevyOutcome.AlreadyIncluded, withOfficeItem.Outcome);

        // And the same receipt with that line left out of the sum no longer reconciles, which is
        // what makes the assertion above about the office item rather than about the tolerance.
        var withoutOfficeItem = ReceiptLevyPolicy.Reconcile(
            levyAmount: 1.10m,
            printedTotal: 67.50m,
            comparableAmounts: [45.00m, 10.00m],
            drinkLines: [new ReceiptLevyDrinkLine(1, 10)]);

        Assert.Equal(ReceiptLevyOutcome.Unresolved, withoutOfficeItem.Outcome);
    }

    /// <summary>
    /// The issue's "unresolved" criterion: the totals match neither with the levy nor without it - a
    /// discount the reader missed, a line it could not read - so no line's cost changes.
    /// </summary>
    [Fact]
    public void A_levy_the_totals_cannot_place_is_left_unresolved()
    {
        var result = ReceiptLevyPolicy.Reconcile(
            levyAmount: 1.10m,
            printedTotal: 60.00m,
            comparableAmounts: [45.00m, 10.00m],
            drinkLines: [new ReceiptLevyDrinkLine(1, 10)]);

        Assert.Equal(ReceiptLevyOutcome.Unresolved, result.Outcome);
        Assert.Empty(result.Allocations);
    }

    /// <summary>
    /// The issue's last levy criterion: with no line marked as a drink there is nobody to charge the
    /// levy to, so even totals that say it is an extra cost leave it a separate flagged line.
    /// </summary>
    [Fact]
    public void A_levy_with_no_drink_line_to_belong_to_is_left_unresolved()
    {
        var result = ReceiptLevyPolicy.Reconcile(
            levyAmount: 1.10m,
            printedTotal: 56.10m,
            comparableAmounts: [45.00m, 10.00m],
            drinkLines: []);

        Assert.Equal(ReceiptLevyOutcome.Unresolved, result.Outcome);
        Assert.Empty(result.Allocations);
    }

    /// <summary>
    /// A mixed-GST receipt: one amount could not be put on the comparison basis, so the comparison
    /// cannot be made at all. One unconvertible amount is enough, however well the rest add up.
    /// </summary>
    [Fact]
    public void One_unconvertible_amount_leaves_the_levy_unresolved()
    {
        var result = ReceiptLevyPolicy.Reconcile(
            levyAmount: 1.10m,
            printedTotal: 55.00m,
            comparableAmounts: [45.00m, null],
            drinkLines: [new ReceiptLevyDrinkLine(1, 10)]);

        Assert.Equal(ReceiptLevyOutcome.Unresolved, result.Outcome);
    }

    /// <summary>With no printed total there is nothing to compare the lines against.</summary>
    [Fact]
    public void A_receipt_with_no_printed_total_leaves_the_levy_unresolved() =>
        Assert.Equal(
            ReceiptLevyOutcome.Unresolved,
            ReceiptLevyPolicy.Reconcile(
                1.10m,
                printedTotal: null,
                comparableAmounts: [45.00m, 10.00m],
                drinkLines: [new ReceiptLevyDrinkLine(1, 10)]).Outcome);

    /// <summary>
    /// A levy of nothing is nothing to reconcile, and is never reported as "already included": that
    /// answer is a statement about the drink prices, and this one is a statement about the levy line.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1.10)]
    public void A_levy_of_nothing_is_unresolved_rather_than_included(decimal levyAmount) =>
        Assert.Equal(
            ReceiptLevyOutcome.Unresolved,
            ReceiptLevyPolicy.Reconcile(
                levyAmount,
                printedTotal: 55.00m,
                comparableAmounts: [45.00m, 10.00m],
                drinkLines: [new ReceiptLevyDrinkLine(1, 10)]).Outcome);

    /// <summary>
    /// The tolerance is one cent, because a converted amount carries the rounding of its conversion.
    /// Two cents out is a real difference and is not reconciled away.
    /// </summary>
    [Theory]
    [InlineData(55.01, ReceiptLevyOutcome.AlreadyIncluded)]
    [InlineData(54.99, ReceiptLevyOutcome.AlreadyIncluded)]
    [InlineData(55.02, ReceiptLevyOutcome.Unresolved)]
    [InlineData(54.98, ReceiptLevyOutcome.Unresolved)]
    public void The_comparison_tolerates_exactly_one_cent(
        decimal printedTotal,
        ReceiptLevyOutcome expected) =>
        Assert.Equal(
            expected,
            ReceiptLevyPolicy.Reconcile(
                1.10m,
                printedTotal,
                comparableAmounts: [45.00m, 10.00m],
                drinkLines: [new ReceiptLevyDrinkLine(1, 10)]).Outcome);

    /// <summary>
    /// "Already included" is tested before "not included", so a receipt both readings would fit -
    /// one whose levy happens to equal its rounding - keeps the reading that changes no line cost.
    /// </summary>
    [Fact]
    public void A_receipt_both_readings_fit_is_read_as_already_included()
    {
        var result = ReceiptLevyPolicy.Reconcile(
            levyAmount: 0.01m,
            printedTotal: 55.00m,
            comparableAmounts: [45.00m, 10.00m],
            drinkLines: [new ReceiptLevyDrinkLine(1, 10)]);

        Assert.Equal(ReceiptLevyOutcome.AlreadyIncluded, result.Outcome);
    }

    /// <summary>
    /// The share is by units, to the cent, and the remainder cents land on the last line so the
    /// allocations sum to exactly the levy. $1.00 over 3 and 4 units is 42c and 58c, not 42c and 57c
    /// with a cent lost.
    /// </summary>
    [Fact]
    public void A_shared_levy_is_split_by_units_with_the_remainder_on_the_last_line()
    {
        var allocations = ReceiptLevyPolicy.Share(
            1.00m,
            [new ReceiptLevyDrinkLine(7, 3), new ReceiptLevyDrinkLine(9, 4)]);

        Assert.Equal(
            [new ReceiptLevyAllocation(7, 0.42m), new ReceiptLevyAllocation(9, 0.58m)],
            allocations);
        Assert.Equal(1.00m, allocations.Sum(allocation => allocation.Amount));
    }

    /// <summary>
    /// The share over several lines of equal units, where the remainder is what makes the total come
    /// out: 10c over three lines of 1 unit is 3c, 3c and 4c.
    /// </summary>
    [Fact]
    public void A_shared_levy_never_loses_or_invents_a_cent()
    {
        var allocations = ReceiptLevyPolicy.Share(
            0.10m,
            [new ReceiptLevyDrinkLine(1, 1), new ReceiptLevyDrinkLine(2, 1), new ReceiptLevyDrinkLine(3, 1)]);

        Assert.Equal(
            [
                new ReceiptLevyAllocation(1, 0.03m),
                new ReceiptLevyAllocation(2, 0.03m),
                new ReceiptLevyAllocation(3, 0.04m),
            ],
            allocations);
    }

    /// <summary>
    /// A drink line with no readable unit count cannot take a share by units, so the levy is left
    /// unresolved rather than shared on a count of nothing.
    /// </summary>
    [Fact]
    public void A_drink_line_with_no_units_leaves_the_levy_unresolved()
    {
        var result = ReceiptLevyPolicy.Reconcile(
            levyAmount: 1.10m,
            printedTotal: 56.10m,
            comparableAmounts: [45.00m, 10.00m],
            drinkLines: [new ReceiptLevyDrinkLine(1, 0)]);

        Assert.Equal(ReceiptLevyOutcome.Unresolved, result.Outcome);
        Assert.Empty(ReceiptLevyPolicy.Share(1.10m, [new ReceiptLevyDrinkLine(1, 0)]));
    }

    /// <summary>The three outcomes are the complete vocabulary.</summary>
    [Fact]
    public void Exactly_three_outcomes_are_declared() =>
        Assert.Equal(
            [
                ReceiptLevyOutcome.AlreadyIncluded,
                ReceiptLevyOutcome.AddedToDrinkLines,
                ReceiptLevyOutcome.Unresolved,
            ],
            Enum.GetValues<ReceiptLevyOutcome>());
}
