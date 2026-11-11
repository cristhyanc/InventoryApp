using Inventory.Domain.ReceiptScanning;
using Xunit;

namespace InventoryApi.Tests.Domain.ReceiptScanning;

/// <summary>
/// The ratio rule that suggests a missing pack size from what the business paid last time (issue
/// #1165, sub-issue E of #975).
///
/// <para>Every figure here is invented, and the one carton it keeps coming back to is the issue's own
/// worked example: a carton of 24 at $28.96 GST-inclusive against a last unit cost of $1.2067.</para>
/// </summary>
public class ReceiptPackSizeSuggestionPolicyTests
{
    /// <summary>The issue's last unit cost, which is what makes 24 the answer.</summary>
    private const decimal LastUnitCost = 1.2067m;

    /// <summary>
    /// The issue's first criterion: one carton at $28.96 against a last unit cost of $1.2067 is 24
    /// units a pack - 28.96 / 1.2067 is 23.9994, which is 24 to well inside the tolerance.
    /// </summary>
    [Fact]
    public void One_carton_at_the_price_it_was_last_bought_at_is_its_last_pack_size()
    {
        Assert.Equal(24, ReceiptPackSizeSuggestionPolicy.Suggest(28.96m, 1, LastUnitCost));
    }

    /// <summary>
    /// The issue's second criterion, and the reason the packs are divided out: two cartons for $57.92
    /// are 24 units a pack, never 48 - the units the line delivered are 48, and multiplying a
    /// per-line unit count by the packs again would record twice the stock.
    /// </summary>
    [Fact]
    public void Two_cartons_on_one_line_are_still_the_pack_size_of_one()
    {
        Assert.Equal(24, ReceiptPackSizeSuggestionPolicy.Suggest(57.92m, 2, LastUnitCost));
    }

    /// <summary>
    /// The issue's unknown-packs criterion: with no readable pack count the quotient would be the
    /// units on the whole line rather than the units in one pack, so nothing is suggested at all.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_line_whose_pack_count_is_unknown_gets_no_suggestion(int? packs)
    {
        Assert.Null(ReceiptPackSizeSuggestionPolicy.Suggest(28.96m, packs, LastUnitCost));
    }

    /// <summary>
    /// The issue's tolerance criterion, at the boundary from both sides. A ratio of 24.7 is a carton
    /// whose price has changed and is refused; 23.9 is the same carton at essentially the same price
    /// and is 24. The edge itself - exactly a quarter of a unit out - is accepted, which is what
    /// "within 0.25" means, and anything past it is not.
    /// </summary>
    [Theory]
    [InlineData(24.7, null)]
    [InlineData(23.9, 24)]
    [InlineData(24.0, 24)]
    [InlineData(24.25, 24)]
    [InlineData(23.75, 24)]
    [InlineData(24.26, null)]
    [InlineData(23.74, null)]
    public void Only_a_ratio_within_a_quarter_of_a_whole_number_is_a_pack_size(double ratio, int? expected)
    {
        // The amount that produces exactly this ratio against a unit cost of $1.00 on a single pack,
        // so the test states the ratio the rule is about rather than an amount to be divided back out.
        var amount = (decimal)ratio;

        Assert.Equal(expected, ReceiptPackSizeSuggestionPolicy.Suggest(amount, 1, 1m));
    }

    /// <summary>
    /// The issue's "at least 1" criterion: a ratio that rounds to nothing is not a pack size. A line
    /// that cost less than one unit's last price is a price change, a different product or a
    /// misreading - never a pack of no units.
    /// </summary>
    [Theory]
    [InlineData(0.1)]
    [InlineData(0.49)]
    public void A_ratio_that_rounds_below_one_unit_is_not_a_pack_size(double ratio)
    {
        Assert.Null(ReceiptPackSizeSuggestionPolicy.Suggest((decimal)ratio, 1, 1m));
    }

    /// <summary>
    /// And its other side: a genuine single item at the price one unit last cost is a pack of one,
    /// which is the smallest suggestion the rule may make.
    /// </summary>
    [Fact]
    public void A_line_at_one_units_last_price_is_a_pack_of_one()
    {
        Assert.Equal(1, ReceiptPackSizeSuggestionPolicy.Suggest(1.2067m, 1, LastUnitCost));
    }

    /// <summary>
    /// Nothing is divided by nothing, and nothing is divided into nothing: an absent or non-positive
    /// amount and an absent or non-positive last unit cost each leave the pack size unknown. A zero
    /// unit cost is the case a division would throw on, and a recorded $0.00 purchase line is a real
    /// row in this application (a repair, a free case) rather than an impossible one.
    /// </summary>
    [Theory]
    [InlineData(null, 1.2067)]
    [InlineData(0.0, 1.2067)]
    [InlineData(-28.96, 1.2067)]
    [InlineData(28.96, null)]
    [InlineData(28.96, 0.0)]
    [InlineData(28.96, -1.2067)]
    public void An_absent_or_non_positive_amount_or_last_cost_suggests_nothing(
        double? amount,
        double? lastUnitCost)
    {
        Assert.Null(ReceiptPackSizeSuggestionPolicy.Suggest(
            (decimal?)amount, 1, (decimal?)lastUnitCost));
    }

    /// <summary>
    /// A last unit cost so small that the quotient is not a pack size anybody could have bought is
    /// refused rather than returned as an absurd number - and the arithmetic does not overflow on the
    /// way to refusing it.
    /// </summary>
    [Fact]
    public void A_quotient_beyond_any_real_pack_is_refused_rather_than_overflowing()
    {
        Assert.Null(ReceiptPackSizeSuggestionPolicy.Suggest(28.96m, 1, 0.0000000001m));
    }

    /// <summary>
    /// The tolerance is a declared quarter of a unit, not a percentage: a loose percentage would
    /// accept almost any ratio for a large carton, which is the failure the issue's rule exists to
    /// avoid (24.7 would round to 25 after a 3% price rise).
    /// </summary>
    [Fact]
    public void The_tolerance_is_a_quarter_of_a_unit()
    {
        Assert.Equal(0.25m, ReceiptPackSizeSuggestionPolicy.Tolerance);
    }
}
