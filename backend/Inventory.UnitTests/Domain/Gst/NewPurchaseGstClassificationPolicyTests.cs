using Inventory.Domain.Gst;
using Xunit;

namespace InventoryApi.Tests.Domain.Gst;

/// <summary>
/// How a new purchase's components are classified at create time (issue #1348): an omitted
/// classification takes the configured rules with the historical workflow's precedence, and a
/// submitted one - including an explicit "not classified" - is always the person's.
/// </summary>
public class NewPurchaseGstClassificationPolicyTests
{
    private static readonly SupplierGstDefaults NoDefaults = SupplierGstDefaults.None;

    [Fact]
    public void An_omitted_line_takes_the_product_rule_over_the_supplier_default()
    {
        var state = NewPurchaseGstClassificationPolicy.ClassifyLine(
            null,
            GstClassification.Taxable,
            new SupplierGstDefaults(GstClassification.GstFree, GstRules.None, GstRules.None));

        Assert.Equal(new GstClassificationState(GstClassification.Taxable, GstClassificationSource.ProductRule), state);
    }

    [Fact]
    public void An_omitted_line_without_a_product_rule_takes_the_supplier_product_line_default()
    {
        var state = NewPurchaseGstClassificationPolicy.ClassifyLine(
            null,
            GstRules.None,
            new SupplierGstDefaults(GstClassification.GstFree, GstClassification.Taxable, GstClassification.Taxable));

        Assert.Equal(new GstClassificationState(GstClassification.GstFree, GstClassificationSource.SupplierDefault), state);
    }

    [Fact]
    public void An_omitted_line_no_rule_covers_stays_unclassified()
    {
        Assert.Equal(
            GstClassificationState.Unclassified,
            NewPurchaseGstClassificationPolicy.ClassifyLine(null, GstRules.None, NoDefaults));
    }

    [Theory]
    [InlineData(GstClassification.Taxable)]
    [InlineData(GstClassification.GstFree)]
    public void A_submitted_line_classification_wins_and_is_manual(GstClassification submitted)
    {
        var state = NewPurchaseGstClassificationPolicy.ClassifyLine(
            submitted,
            submitted == GstClassification.Taxable ? GstClassification.GstFree : GstClassification.Taxable,
            new SupplierGstDefaults(GstClassification.Taxable, GstRules.None, GstRules.None));

        Assert.Equal(new GstClassificationState(submitted, GstClassificationSource.Manual), state);
    }

    [Fact]
    public void An_explicit_unknown_line_stays_unclassified_even_when_a_rule_exists()
    {
        Assert.Equal(
            GstClassificationState.Unclassified,
            NewPurchaseGstClassificationPolicy.ClassifyLine(GstClassification.Unknown, GstClassification.Taxable, NoDefaults));
    }

    [Fact]
    public void An_omitted_charge_takes_only_its_own_fee_default()
    {
        var defaults = new SupplierGstDefaults(GstClassification.Taxable, GstClassification.GstFree, GstRules.None);

        Assert.Equal(
            new GstClassificationState(GstClassification.GstFree, GstClassificationSource.SupplierFeeDefault),
            NewPurchaseGstClassificationPolicy.ClassifyCharge(GstComponentKind.DeliveryCharge, 5m, null, defaults));
        // The package charge has no default, and never borrows the product-line default.
        Assert.Equal(
            GstClassificationState.Unclassified,
            NewPurchaseGstClassificationPolicy.ClassifyCharge(GstComponentKind.PackageCharge, 2m, null, defaults));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void An_absent_or_zero_charge_carries_no_classification_whatever_is_configured(int? amount)
    {
        var defaults = new SupplierGstDefaults(GstRules.None, GstClassification.Taxable, GstClassification.Taxable);

        Assert.Equal(
            GstClassificationState.Unclassified,
            NewPurchaseGstClassificationPolicy.ClassifyCharge(GstComponentKind.DeliveryCharge, amount, null, defaults));
        Assert.Equal(
            GstClassificationState.Unclassified,
            NewPurchaseGstClassificationPolicy.ClassifyCharge(
                GstComponentKind.PackageCharge, amount, GstClassification.Taxable, defaults));
    }

    [Fact]
    public void A_submitted_charge_classification_wins_over_the_fee_default()
    {
        var defaults = new SupplierGstDefaults(GstRules.None, GstClassification.Taxable, GstRules.None);

        Assert.Equal(
            new GstClassificationState(GstClassification.GstFree, GstClassificationSource.Manual),
            NewPurchaseGstClassificationPolicy.ClassifyCharge(
                GstComponentKind.DeliveryCharge, 5m, GstClassification.GstFree, defaults));
        Assert.Equal(
            GstClassificationState.Unclassified,
            NewPurchaseGstClassificationPolicy.ClassifyCharge(
                GstComponentKind.DeliveryCharge, 5m, GstClassification.Unknown, defaults));
    }

    [Fact]
    public void A_product_line_is_not_a_charge()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NewPurchaseGstClassificationPolicy.ClassifyCharge(GstComponentKind.ProductLine, 5m, null, NoDefaults));
    }

    [Fact]
    public void An_undefined_submitted_value_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NewPurchaseGstClassificationPolicy.ClassifyLine((GstClassification)999, GstRules.None, NoDefaults));
    }
}
