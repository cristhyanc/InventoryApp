using Inventory.Domain.Gst;
using Xunit;

namespace InventoryApi.Tests.Domain.Gst;

/// <summary>
/// The GST rule vocabulary an administrator configures (issue #430): that "no rule" is the declared
/// <c>Unknown</c> state and stays distinct from an explicit <c>GstFree</c> rule, and that a value
/// outside the vocabulary is recognised as unsupported.
/// </summary>
public class GstRuleTests
{
    [Fact]
    public void No_rule_is_the_unknown_state_and_is_not_gst_free()
    {
        Assert.Equal(GstClassification.Unknown, GstRules.None);
        Assert.NotEqual(GstClassification.GstFree, GstRules.None);
    }

    [Theory]
    [InlineData(GstClassification.Unknown)]
    [InlineData(GstClassification.Taxable)]
    [InlineData(GstClassification.GstFree)]
    public void Every_declared_classification_is_a_supported_rule(GstClassification rule)
    {
        Assert.True(GstRules.IsSupportedRule(rule));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(999)]
    public void A_value_outside_the_vocabulary_is_not_a_supported_rule(int value)
    {
        Assert.False(GstRules.IsSupportedRule((GstClassification)value));
    }

    /// <summary>
    /// The refusal message names the vocabulary itself, so a renamed or added classification cannot
    /// leave the message describing something else.
    /// </summary>
    [Fact]
    public void The_refusal_message_lists_the_supported_vocabulary()
    {
        Assert.Equal(GstClassifications.UnsupportedMessage, GstRules.UnsupportedRuleMessage);
        Assert.Contains(nameof(GstClassification.Taxable), GstRules.UnsupportedRuleMessage, StringComparison.Ordinal);
        Assert.Contains(nameof(GstClassification.GstFree), GstRules.UnsupportedRuleMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_supplier_with_no_configured_defaults_has_none_of_the_three()
    {
        var defaults = SupplierGstDefaults.None;

        Assert.Equal(GstRules.None, defaults.ProductLines);
        Assert.Equal(GstRules.None, defaults.Delivery);
        Assert.Equal(GstRules.None, defaults.Package);
        Assert.False(defaults.HasUnsupportedRule);
    }

    /// <summary>
    /// A charge default never comes from the product-line default: the three are independent values,
    /// which is what lets a supplier sell GST-free goods and still charge GST on delivery.
    /// </summary>
    [Fact]
    public void The_three_defaults_are_independent()
    {
        var defaults = new SupplierGstDefaults(
            ProductLines: GstClassification.GstFree,
            Delivery: GstClassification.Taxable,
            Package: GstRules.None);

        Assert.Equal(GstClassification.GstFree, defaults.ProductLines);
        Assert.Equal(GstClassification.Taxable, defaults.Delivery);
        Assert.Equal(GstRules.None, defaults.Package);
        Assert.False(defaults.HasUnsupportedRule);
    }

    [Theory]
    [InlineData(999, 0, 0)]
    [InlineData(0, 999, 0)]
    [InlineData(0, 0, 999)]
    public void An_undefined_value_in_any_of_the_three_defaults_is_unsupported(
        int productLines, int delivery, int package)
    {
        var defaults = new SupplierGstDefaults(
            (GstClassification)productLines, (GstClassification)delivery, (GstClassification)package);

        Assert.True(defaults.HasUnsupportedRule);
    }
}
