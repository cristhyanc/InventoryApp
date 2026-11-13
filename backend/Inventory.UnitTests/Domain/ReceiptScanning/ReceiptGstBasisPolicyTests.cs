using Inventory.Domain.ReceiptScanning;
using Xunit;

namespace InventoryApi.Tests.Domain.ReceiptScanning;

/// <summary>
/// When a receipt's own totals settle the GST basis its lines leave unstated: only when the lines
/// add up, to the cent, to a total the receipt says includes GST.
///
/// <para>Every receipt here is invented. Real receipts are never committed as fixtures (issue
/// #1163's known constraints).</para>
/// </summary>
public class ReceiptGstBasisPolicyTests
{
    private static ReceiptGstBasisLine Unknown(decimal? total, decimal discount = 0m) =>
        new(total, discount, ReceiptPriceGstBasis.Unknown);

    /// <summary>
    /// The supermarket tax invoice shape: item lines with no basis beside them, a bag charge and a
    /// delivery fee, and a total "including GST" that is exactly their sum.
    /// </summary>
    [Fact]
    public void Unknown_lines_that_add_up_to_a_gst_inclusive_total_include_gst()
    {
        var lines = new[] { Unknown(32.00m), Unknown(8.00m), Unknown(10.00m), Unknown(2.00m), Unknown(2.00m) };

        Assert.True(ReceiptGstBasisPolicy.UnknownLinesIncludeGst(lines, 54.00m, ReceiptPriceGstBasis.IncludesGst));
    }

    /// <summary>Attached discounts are part of what the lines add up to.</summary>
    [Fact]
    public void Discounts_are_subtracted_before_the_lines_are_compared_with_the_total()
    {
        var lines = new[] { Unknown(28.99m, discount: 1.50m), Unknown(12.51m) };

        Assert.True(ReceiptGstBasisPolicy.UnknownLinesIncludeGst(lines, 40.00m, ReceiptPriceGstBasis.IncludesGst));
    }

    /// <summary>Lines the reader already said include GST count towards the total like any other.</summary>
    [Fact]
    public void Lines_already_stated_as_inc_gst_count_towards_the_total()
    {
        var lines = new[] { new ReceiptGstBasisLine(20.00m, 0m, ReceiptPriceGstBasis.IncludesGst), Unknown(5.50m) };

        Assert.True(ReceiptGstBasisPolicy.UnknownLinesIncludeGst(lines, 25.50m, ReceiptPriceGstBasis.IncludesGst));
    }

    /// <summary>
    /// Any difference at all - a cent, an unread line, a fee the reader missed - and the lines are
    /// left as the reader reported them.
    /// </summary>
    [Theory]
    [InlineData(54.01)]
    [InlineData(53.99)]
    [InlineData(59.40)]
    public void Lines_that_do_not_add_up_to_the_total_are_left_unknown(decimal total)
    {
        var lines = new[] { Unknown(32.00m), Unknown(8.00m), Unknown(10.00m), Unknown(2.00m), Unknown(2.00m) };

        Assert.False(ReceiptGstBasisPolicy.UnknownLinesIncludeGst(lines, total, ReceiptPriceGstBasis.IncludesGst));
    }

    /// <summary>A total that is not stated to include GST settles nothing, even when the lines match it.</summary>
    [Theory]
    [InlineData(ReceiptPriceGstBasis.Unknown)]
    [InlineData(ReceiptPriceGstBasis.ExcludesGst)]
    public void A_total_not_stated_to_include_gst_settles_nothing(ReceiptPriceGstBasis totalBasis)
    {
        var lines = new[] { Unknown(32.00m), Unknown(8.00m) };

        Assert.False(ReceiptGstBasisPolicy.UnknownLinesIncludeGst(lines, 40.00m, totalBasis));
    }

    /// <summary>No printed total, nothing to compare with.</summary>
    [Fact]
    public void A_receipt_with_no_total_settles_nothing()
    {
        Assert.False(ReceiptGstBasisPolicy.UnknownLinesIncludeGst(
            [Unknown(32.00m)], null, ReceiptPriceGstBasis.IncludesGst));
    }

    /// <summary>A line with no printed total cannot be shown to add up, so nothing is settled.</summary>
    [Fact]
    public void A_line_with_no_printed_total_settles_nothing()
    {
        var lines = new[] { Unknown(32.00m), Unknown(null) };

        Assert.False(ReceiptGstBasisPolicy.UnknownLinesIncludeGst(lines, 32.00m, ReceiptPriceGstBasis.IncludesGst));
    }

    /// <summary>
    /// A line the reader said is ex GST makes the receipt a mixed one, which the totals cannot
    /// untangle, so nothing is settled.
    /// </summary>
    [Fact]
    public void A_line_stated_as_ex_gst_settles_nothing()
    {
        var lines = new[] { Unknown(30.00m), new ReceiptGstBasisLine(10.00m, 0m, ReceiptPriceGstBasis.ExcludesGst) };

        Assert.False(ReceiptGstBasisPolicy.UnknownLinesIncludeGst(lines, 40.00m, ReceiptPriceGstBasis.IncludesGst));
    }

    /// <summary>With no unknown line there is nothing to settle.</summary>
    [Fact]
    public void Lines_that_all_state_their_basis_need_nothing_settled()
    {
        var lines = new[] { new ReceiptGstBasisLine(40.00m, 0m, ReceiptPriceGstBasis.IncludesGst) };

        Assert.False(ReceiptGstBasisPolicy.UnknownLinesIncludeGst(lines, 40.00m, ReceiptPriceGstBasis.IncludesGst));
    }
}
