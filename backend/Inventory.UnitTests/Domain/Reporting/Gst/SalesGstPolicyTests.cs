using Inventory.Domain.Gst;
using Inventory.Domain.Reporting.Gst;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting.Gst;

/// <summary>
/// The GST-on-sales rule of issue #1255: only a product whose rule is GST-free takes its sales out
/// of the taxable total, a product with no rule at all stays taxable and is reported separately, and
/// the GST is one eleventh of the taxable total rather than a sum of per-sale amounts.
/// </summary>
public class SalesGstPolicyTests
{
    [Fact]
    public void Only_gst_free_products_reduce_the_taxable_total_and_the_no_rule_sales_are_reported_beside_it()
    {
        // The acceptance fixture: $11 of a taxable product, $3 of a GST-free product and $22 of a
        // product nobody has given a rule.
        var result = SalesGstPolicy.Calculate(
        [
            new SalesByGstRule(GstClassification.Taxable, 11m),
            new SalesByGstRule(GstClassification.GstFree, 3m),
            new SalesByGstRule(GstRules.None, 22m),
        ]);

        Assert.Equal(33m, result.TaxableSales);
        Assert.Equal(3m, result.GstFreeSales);
        Assert.Equal(22m, result.NoGstRuleSales);
        Assert.Equal(3.00m, result.GstOnSales);
    }

    [Fact]
    public void A_sale_whose_product_cannot_be_resolved_is_taxable_and_counts_as_having_no_rule()
    {
        var result = SalesGstPolicy.Calculate([new SalesByGstRule(null, 22m)]);

        Assert.Equal(22m, result.TaxableSales);
        Assert.Equal(0m, result.GstFreeSales);
        Assert.Equal(22m, result.NoGstRuleSales);
        Assert.Equal(2m, result.GstOnSales);
    }

    [Fact]
    public void A_taxable_rule_is_taxable_and_is_not_counted_as_having_no_rule()
    {
        var result = SalesGstPolicy.Calculate([new SalesByGstRule(GstClassification.Taxable, 11m)]);

        Assert.Equal(11m, result.TaxableSales);
        Assert.Equal(0m, result.NoGstRuleSales);
        Assert.Equal(1m, result.GstOnSales);
    }

    [Fact]
    public void Gst_free_sales_contribute_no_gst_at_all()
    {
        var result = SalesGstPolicy.Calculate([new SalesByGstRule(GstClassification.GstFree, 3m)]);

        Assert.Equal(0m, result.TaxableSales);
        Assert.Equal(3m, result.GstFreeSales);
        Assert.Equal(0m, result.NoGstRuleSales);
        Assert.Equal(0m, result.GstOnSales);
    }

    [Fact]
    public void Many_small_sales_give_the_gst_of_the_period_total_with_no_per_sale_drift()
    {
        // The acceptance fixture: 1,000 sales of $1.10 across a taxable and a no-rule product. Both
        // halves are taxable, so the GST is one eleventh of the whole $1,100.
        var result = SalesGstPolicy.Calculate(
        [
            new SalesByGstRule(GstClassification.Taxable, 500 * 1.10m),
            new SalesByGstRule(GstRules.None, 500 * 1.10m),
        ]);

        Assert.Equal(1100m, result.TaxableSales);
        Assert.Equal(550m, result.NoGstRuleSales);
        Assert.Equal(100.00m, result.GstOnSales);
    }

    [Fact]
    public void Gst_on_sales_is_one_eleventh_of_the_taxable_total_rather_than_a_sum_of_per_sale_amounts()
    {
        // Three $1.05 taxable sales: one eleventh of $3.15 is $0.29, while rounding each sale's own
        // eleventh to cents and adding them up gives $0.30. The period total is the authoritative
        // figure, which is what keeps a period of many small sales free of rounding drift.
        var perSaleSum = 3 * Math.Round(1.05m / 11m, 2, MidpointRounding.AwayFromZero);

        var result = SalesGstPolicy.Calculate([new SalesByGstRule(GstClassification.Taxable, 3.15m)]);

        Assert.Equal(0.29m, result.GstOnSales);
        Assert.Equal(0.30m, perSaleSum);
    }

    [Fact]
    public void Gst_on_sales_is_rounded_to_cents_away_from_zero()
    {
        var result = SalesGstPolicy.Calculate([new SalesByGstRule(GstClassification.Taxable, 100m)]);

        Assert.Equal(9.09m, result.GstOnSales);
    }

    [Fact]
    public void A_period_with_no_sales_is_all_zeroes()
    {
        var result = SalesGstPolicy.Calculate([]);

        Assert.Equal(0m, result.TaxableSales);
        Assert.Equal(0m, result.GstFreeSales);
        Assert.Equal(0m, result.NoGstRuleSales);
        Assert.Equal(0m, result.GstOnSales);
    }

    [Fact]
    public void A_stored_rule_outside_the_declared_vocabulary_stays_taxable_and_is_reported_as_having_no_rule()
    {
        // A report reads whatever is stored, so it must not fail closed the way a submitted value
        // does: an undeclared rule is not evidence that a product is GST-free, and treating it as
        // taxable while naming it in the no-rule figure keeps it visible instead of silently
        // reducing GST payable.
        var result = SalesGstPolicy.Calculate([new SalesByGstRule((GstClassification)99, 11m)]);

        Assert.Equal(11m, result.TaxableSales);
        Assert.Equal(0m, result.GstFreeSales);
        Assert.Equal(11m, result.NoGstRuleSales);
        Assert.Equal(1m, result.GstOnSales);
    }

    [Fact]
    public void Several_groups_of_one_rule_are_added_together()
    {
        var result = SalesGstPolicy.Calculate(
        [
            new SalesByGstRule(GstClassification.GstFree, 3m),
            new SalesByGstRule(GstClassification.GstFree, 4m),
            new SalesByGstRule(GstClassification.Taxable, 11m),
        ]);

        Assert.Equal(7m, result.GstFreeSales);
        Assert.Equal(11m, result.TaxableSales);
    }

    [Fact]
    public void Calculate_refuses_a_null_group_list()
    {
        Assert.Throws<ArgumentNullException>(() => SalesGstPolicy.Calculate(null!));
    }
}
