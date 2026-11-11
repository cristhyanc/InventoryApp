using Inventory.Domain.ReceiptScanning;
using Xunit;

namespace InventoryApi.Tests.Domain.ReceiptScanning;

/// <summary>
/// The pack arithmetic and GST conversion of one scanned receipt line (issue #1163), stated as the
/// issue's own worked examples.
///
/// <para>Each figure is pinned by value rather than recomputed, for the reason
/// <c>PurchaseGstPolicyTests</c> pins its GST amounts: a cost that feeds AVCO and every profit
/// figure is a change somebody has to see in a diff.</para>
/// </summary>
public class ReceiptLineArithmeticPolicyTests
{
    /// <summary>
    /// The issue's first criterion: "2 packs x 24, $30.00" is 48 units at $0.625 each, and the
    /// quotient is exact.
    /// </summary>
    [Fact]
    public void Two_packs_of_twenty_four_at_thirty_dollars_is_forty_eight_units_at_sixty_two_and_a_half_cents()
    {
        var line = Evaluate(30.00m, packs: 2, unitsPerPack: 24);

        Assert.Equal(30.00m, line.Amount);
        Assert.Equal(48, line.Units);
        Assert.Equal(0.625m, line.UnitCost);
        Assert.Empty(line.Missing);
    }

    /// <summary>
    /// The issue's GST criterion: a taxable line printed ex GST at $26.33 is recorded at $28.96 -
    /// 26.33 x 1.1 = 28.963, rounded to the cent away from zero - and the unit cost is that amount
    /// over the carton's 24 units.
    /// </summary>
    [Fact]
    public void A_taxable_ex_gst_line_has_ten_percent_added_and_is_rounded_to_the_cent()
    {
        var line = Evaluate(
            26.33m,
            packs: 1,
            unitsPerPack: 24,
            basis: ReceiptPriceGstBasis.ExcludesGst,
            taxability: ReceiptLineTaxability.Taxable);

        Assert.Equal(28.96m, line.Amount);
        Assert.Equal(24, line.Units);
        Assert.Equal(28.96m / 24m, line.UnitCost);
        Assert.Empty(line.Missing);
    }

    /// <summary>
    /// A price read from an inc-GST column has nothing added to it, whatever the line's own GST
    /// marker says. The whole point of the basis being stated per line is that a tax invoice printing
    /// both columns may be read from either.
    /// </summary>
    [Theory]
    [InlineData(ReceiptLineTaxability.Taxable)]
    [InlineData(ReceiptLineTaxability.GstFree)]
    [InlineData(ReceiptLineTaxability.Unknown)]
    public void An_inc_gst_line_has_nothing_added(ReceiptLineTaxability taxability)
    {
        var line = Evaluate(
            26.33m,
            packs: 1,
            unitsPerPack: 24,
            basis: ReceiptPriceGstBasis.IncludesGst,
            taxability: taxability);

        Assert.Equal(26.33m, line.Amount);
        Assert.Empty(line.Missing);
    }

    /// <summary>
    /// A GST-free line printed ex GST is already inclusive of the no GST it carries, so nothing is
    /// added. Never inferring GST from a price is the standing rule (AGENTS.md § Purchase GST
    /// classification); this is its scanning half.
    /// </summary>
    [Fact]
    public void A_gst_free_ex_gst_line_has_nothing_added()
    {
        var line = Evaluate(
            26.33m,
            packs: 1,
            unitsPerPack: 24,
            basis: ReceiptPriceGstBasis.ExcludesGst,
            taxability: ReceiptLineTaxability.GstFree);

        Assert.Equal(26.33m, line.Amount);
        Assert.Empty(line.Missing);
    }

    /// <summary>
    /// The issue's criterion for an unstated basis: no amount, no unit cost, and the field the
    /// operator has to answer named in <c>missing</c>. A 10% guess either way is a 10% error in a
    /// historical cost.
    /// </summary>
    [Fact]
    public void An_unstated_gst_basis_leaves_the_amount_and_the_unit_cost_absent()
    {
        var line = Evaluate(
            26.33m,
            packs: 1,
            unitsPerPack: 24,
            basis: ReceiptPriceGstBasis.Unknown,
            taxability: ReceiptLineTaxability.Unknown);

        Assert.Null(line.Amount);
        Assert.Null(line.UnitCost);
        Assert.Equal(24, line.Units);
        Assert.Equal(
            [ReceiptLineMissingValue.PriceGstBasis, ReceiptLineMissingValue.UnitCost],
            line.Missing);
    }

    /// <summary>
    /// An ex-GST price whose taxability the receipt does not state cannot be converted either: the
    /// unresolved field is the taxability, not the basis, because the receipt did say the basis.
    /// </summary>
    [Fact]
    public void An_ex_gst_line_of_unstated_taxability_leaves_the_amount_absent()
    {
        var line = Evaluate(
            26.33m,
            packs: 1,
            unitsPerPack: 24,
            basis: ReceiptPriceGstBasis.ExcludesGst,
            taxability: ReceiptLineTaxability.Unknown);

        Assert.Null(line.Amount);
        Assert.Null(line.UnitCost);
        Assert.Equal(
            [ReceiptLineMissingValue.Taxability, ReceiptLineMissingValue.UnitCost],
            line.Missing);
    }

    /// <summary>
    /// The issue's pack-size criterion: a carton whose units per pack is not printed has no units,
    /// never <c>1</c>. One is only ever a genuine single item, which the model has to say
    /// explicitly - the failure the model test of 8 November 2026 found.
    /// </summary>
    [Fact]
    public void An_unprinted_pack_size_leaves_the_units_absent_rather_than_one()
    {
        var line = Evaluate(30.00m, packs: 2, unitsPerPack: null);

        Assert.Null(line.Units);
        Assert.Null(line.UnitCost);
        Assert.Equal(30.00m, line.Amount);
        Assert.Equal(
            [ReceiptLineMissingValue.UnitsPerPack, ReceiptLineMissingValue.UnitCost],
            line.Missing);
    }

    /// <summary>A genuine single item is one pack of one unit, and costs what the line costs.</summary>
    [Fact]
    public void A_genuine_single_item_is_one_unit_at_the_line_price()
    {
        var line = Evaluate(4.50m, packs: 1, unitsPerPack: 1);

        Assert.Equal(1, line.Units);
        Assert.Equal(4.50m, line.UnitCost);
        Assert.Empty(line.Missing);
    }

    /// <summary>
    /// The issue's missing-values criterion, both halves: an absent pack count and an absent pack
    /// size each leave units and unit cost absent and are each named.
    /// </summary>
    [Theory]
    [InlineData(null, 24, ReceiptLineMissingValue.Packs)]
    [InlineData(2, null, ReceiptLineMissingValue.UnitsPerPack)]
    [InlineData(0, 24, ReceiptLineMissingValue.Packs)]
    [InlineData(2, 0, ReceiptLineMissingValue.UnitsPerPack)]
    public void An_absent_pack_count_or_pack_size_leaves_the_units_and_unit_cost_absent(
        int? packs,
        int? unitsPerPack,
        ReceiptLineMissingValue expected)
    {
        var line = Evaluate(30.00m, packs, unitsPerPack);

        Assert.Null(line.Units);
        Assert.Null(line.UnitCost);
        Assert.Contains(expected, line.Missing);
        Assert.Contains(ReceiptLineMissingValue.UnitCost, line.Missing);
    }

    /// <summary>
    /// The issue's discount criterion: a discount line of -1.50 under an item of 28.99 leaves that
    /// item at 27.49.
    /// </summary>
    [Fact]
    public void An_attached_discount_is_subtracted_from_the_line()
    {
        var line = Evaluate(28.99m, packs: 1, unitsPerPack: 1, discountTotal: 1.50m);

        Assert.Equal(27.49m, line.Amount);
        Assert.Equal(27.49m, line.UnitCost);
    }

    /// <summary>
    /// Discounts larger than the line they are attached to are not a reading of a receipt. The line
    /// comes back with no amount rather than with a free or negative cost, which would reach AVCO.
    /// </summary>
    [Fact]
    public void A_discount_larger_than_the_line_leaves_no_amount()
    {
        var line = Evaluate(5.00m, packs: 1, unitsPerPack: 1, discountTotal: 7.50m);

        Assert.Null(line.Amount);
        Assert.Null(line.UnitCost);
        Assert.Contains(ReceiptLineMissingValue.LineTotal, line.Missing);
    }

    /// <summary>A line with no printed total has no amount and says so.</summary>
    [Fact]
    public void A_line_with_no_printed_total_has_no_amount()
    {
        var line = Evaluate(null, packs: 2, unitsPerPack: 24);

        Assert.Null(line.Amount);
        Assert.Null(line.UnitCost);
        Assert.Equal(48, line.Units);
        Assert.Equal(
            [ReceiptLineMissingValue.LineTotal, ReceiptLineMissingValue.UnitCost],
            line.Missing);
    }

    /// <summary>
    /// The unit cost is the unrounded quotient, exactly as the purchase form's own calculated unit
    /// price is: it is what makes the saved line's <c>round(quantity x unitCost, 2)</c> come back to
    /// the amount the receipt printed, which a quotient rounded for display would not.
    /// </summary>
    [Fact]
    public void The_unit_cost_is_the_unrounded_quotient()
    {
        var line = Evaluate(100.00m, packs: 1, unitsPerPack: 300);

        Assert.Equal(100.00m / 300m, line.UnitCost);
        Assert.Equal(100.00m, Math.Round(line.Units!.Value * line.UnitCost!.Value, 2, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Both conversions, in both directions, including the two states that cannot be converted at
    /// all. The ex-GST direction exists for the levy reconciliation, which has to compare every
    /// amount on one basis.
    /// </summary>
    [Theory]
    [InlineData(ReceiptPriceGstBasis.IncludesGst, ReceiptLineTaxability.Taxable, 26.33, 26.33, 23.94)]
    [InlineData(ReceiptPriceGstBasis.IncludesGst, ReceiptLineTaxability.GstFree, 26.33, 26.33, 26.33)]
    [InlineData(ReceiptPriceGstBasis.ExcludesGst, ReceiptLineTaxability.Taxable, 26.33, 28.96, 26.33)]
    [InlineData(ReceiptPriceGstBasis.ExcludesGst, ReceiptLineTaxability.GstFree, 26.33, 26.33, 26.33)]
    public void An_amount_converts_onto_either_basis(
        ReceiptPriceGstBasis basis,
        ReceiptLineTaxability taxability,
        decimal amount,
        decimal inclusive,
        decimal exclusive)
    {
        Assert.Equal(inclusive, ReceiptLineArithmeticPolicy.ToGstInclusive(amount, basis, taxability));
        Assert.Equal(exclusive, ReceiptLineArithmeticPolicy.ToGstExclusive(amount, basis, taxability));
    }

    /// <summary>
    /// Neither conversion invents an answer: an unstated basis converts to nothing on either side,
    /// and so does an ex-GST or inc-GST price whose taxability the receipt does not state.
    /// </summary>
    [Theory]
    [InlineData(ReceiptPriceGstBasis.Unknown, ReceiptLineTaxability.Taxable)]
    [InlineData(ReceiptPriceGstBasis.Unknown, ReceiptLineTaxability.Unknown)]
    [InlineData(ReceiptPriceGstBasis.ExcludesGst, ReceiptLineTaxability.Unknown)]
    [InlineData(ReceiptPriceGstBasis.IncludesGst, ReceiptLineTaxability.Unknown)]
    public void An_unconvertible_amount_converts_to_nothing(
        ReceiptPriceGstBasis basis,
        ReceiptLineTaxability taxability)
    {
        Assert.Null(ReceiptLineArithmeticPolicy.ToGstExclusive(26.33m, basis, taxability));

        // The inc-GST side is the one asymmetry, and it is deliberate: a price that already includes
        // GST needs no taxability to stay as it is, so only the ex-GST and unstated bases fail there.
        if (basis is not ReceiptPriceGstBasis.IncludesGst)
        {
            Assert.Null(ReceiptLineArithmeticPolicy.ToGstInclusive(26.33m, basis, taxability));
        }
    }

    private static ReceiptLineArithmetic Evaluate(
        decimal? printedLineTotal,
        int? packs,
        int? unitsPerPack,
        decimal discountTotal = 0m,
        ReceiptPriceGstBasis basis = ReceiptPriceGstBasis.IncludesGst,
        ReceiptLineTaxability taxability = ReceiptLineTaxability.Taxable) =>
        ReceiptLineArithmeticPolicy.Evaluate(
            printedLineTotal, discountTotal, packs, unitsPerPack, basis, taxability);
}
