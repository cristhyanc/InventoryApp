using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace Inventory.UnitTests.Domain.PriceCatalogue;

/// <summary>
/// The rule that decides whether a product being created may be linked to the master product the
/// operator picked (issue #1051, parent #817): the opt-out, and the identity comparison against
/// #1049's prefill.
///
/// <para><strong>The comparison is the whole safeguard, so its cases are the point of this
/// file.</strong> A link is a statement that this product <em>is</em> that shared product, and the
/// only evidence the server accepts for it is that the submitted Name and unit label are still the
/// ones the prefill chose - trim, collapsed spaces and case apart. Nothing fuzzy, nothing matched on
/// the name alone, and no boolean a client sent.</para>
/// </summary>
public class MasterProductSelectionPolicyTests
{
    /// <summary>The issue's worked example, as the lookup of #1049 publishes it.</summary>
    private static readonly MasterProductPrefillText CornChips = MasterProductPrefill.For(
        "CC's",
        "Tasty Cheese Corn Chips",
        45m,
        MasterProductSizeUnit.Gram);

    #region The operator's opt-out

    /// <summary>
    /// The selection stands only while the operator wants the prices tracked, because the link
    /// <em>is</em> the tracking row: turning the switch off is therefore the same request as sending
    /// no selection at all.
    /// </summary>
    [Theory]
    [InlineData(7L, true, 7L)]
    [InlineData(7L, false, null)]
    [InlineData(null, true, null)]
    [InlineData(null, false, null)]
    public void A_selection_stands_only_while_supplier_prices_are_to_be_tracked(
        long? selected,
        bool trackSupplierPrices,
        long? expected) =>
        Assert.Equal(
            expected,
            MasterProductSelectionPolicy.SelectedMasterProductId(selected, trackSupplierPrices));

    #endregion

    #region The identity comparison

    /// <summary>
    /// The prefill submitted back unchanged is what a link rests on - and the three differences the
    /// documented normalisation forgives: whitespace at the ends, a run of whitespace inside the
    /// Name, and case in either value.
    /// </summary>
    [Theory]
    [InlineData("CC's Tasty Cheese Corn Chips", "45g")]
    [InlineData("  CC's Tasty Cheese Corn Chips  ", "  45g  ")]
    [InlineData("CC's  Tasty   Cheese\tCorn Chips", "45g")]
    [InlineData("cc's tasty cheese corn chips", "45G")]
    [InlineData("CC'S TASTY CHEESE CORN CHIPS", "45g")]
    public void The_prefill_submitted_back_is_unchanged(string name, string unitLabel) =>
        Assert.True(MasterProductSelectionPolicy.IsUnchangedPrefill(CornChips, name, unitLabel));

    /// <summary>
    /// Everything else is a change, and a change is not a link. The cases are the ones a link would
    /// quietly get wrong: another product's name, the same product edited, a size the operator
    /// retyped, a label spaced differently from the one the catalogue dictates - which is a label
    /// this formatter never produces - and a missing value.
    /// </summary>
    [Theory]
    [InlineData("CC's Tasty Cheese Corn Chip", "45g")]
    [InlineData("CC's Tasty Cheese Corn Chips Twin Pack", "45g")]
    [InlineData("Tasty Cheese Corn Chips", "45g")]
    [InlineData("CC's Tasty Cheese Corn Chips", "45 g")]
    [InlineData("CC's Tasty Cheese Corn Chips", "50g")]
    [InlineData("CC's Tasty Cheese Corn Chips", "45")]
    [InlineData("CC's Tasty Cheese Corn Chips", "")]
    [InlineData("CC's Tasty Cheese Corn Chips", null)]
    [InlineData("", "45g")]
    [InlineData(null, "45g")]
    public void Anything_else_is_a_changed_identity(string? name, string? unitLabel) =>
        Assert.False(MasterProductSelectionPolicy.IsUnchangedPrefill(CornChips, name, unitLabel));

    /// <summary>
    /// The two values travel together: the right Name under another product's label, and the right
    /// label under another product's Name, are both refused. A name-only match is exactly what this
    /// rule exists to refuse.
    /// </summary>
    [Fact]
    public void Each_value_is_compared_against_its_own_master_product()
    {
        var cola = MasterProductPrefill.For(
            "Coca-Cola",
            "Classic",
            375m,
            MasterProductSizeUnit.Millilitre);

        Assert.False(
            MasterProductSelectionPolicy.IsUnchangedPrefill(cola, cola.ProductName, CornChips.UnitLabel));
        Assert.False(
            MasterProductSelectionPolicy.IsUnchangedPrefill(cola, CornChips.ProductName, cola.UnitLabel));
    }

    /// <summary>
    /// The link and the lookup cannot drift apart: whatever #1049's formatter publishes for a master
    /// product of any declared unit is accepted by this comparison when it is submitted back. A
    /// prefill a person did not touch must never be read as an edit.
    /// </summary>
    [Fact]
    public void Every_declared_units_prefill_is_accepted_unchanged()
    {
        foreach (var unit in Enum.GetValues<MasterProductSizeUnit>())
        {
            var prefill = MasterProductPrefill.For("Zedco", "Classic", 1.25m, unit);

            Assert.True(
                MasterProductSelectionPolicy.IsUnchangedPrefill(
                    prefill,
                    prefill.ProductName,
                    prefill.UnitLabel),
                $"The prefill for {unit} must be accepted unchanged.");
        }
    }

    #endregion

    #region The refusals a client branches on

    /// <summary>
    /// The three codes are published to clients, so they are asserted as the literal text rather
    /// than as whatever the constant happens to say: renaming one is an API change.
    /// </summary>
    [Fact]
    public void The_three_refusal_codes_are_stable()
    {
        Assert.Equal("master_product_unavailable", MasterProductSelectionPolicy.UnavailableCode);
        Assert.Equal("master_product_changed", MasterProductSelectionPolicy.ChangedCode);
        Assert.Equal("master_product_refused", MasterProductSelectionPolicy.RefusedCode);
    }

    /// <summary>
    /// Each refusal says what to do about it, because the form offers exactly two ways on: choose
    /// again, or create the product without the link.
    /// </summary>
    [Theory]
    [InlineData(MasterProductSelectionPolicy.UnavailableMessage)]
    [InlineData(MasterProductSelectionPolicy.ChangedMessage)]
    [InlineData(MasterProductSelectionPolicy.RefusedMessage)]
    public void Each_refusal_is_actionable(string message)
    {
        Assert.Contains("without", message, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".", message, StringComparison.Ordinal);
    }

    #endregion
}
