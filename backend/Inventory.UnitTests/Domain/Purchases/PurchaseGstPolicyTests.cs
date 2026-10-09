using Inventory.Domain.Gst;
using Inventory.Domain.Purchases;
using Xunit;

namespace InventoryApi.Tests.Domain.Purchases;

/// <summary>
/// The purchase input-GST rules settled in parent issue #62 (decisions D2, D3 and D4). Purchase
/// amounts are GST-inclusive: a taxable component's GST is its own rounded amount divided by 11
/// and rounded again, GST-free contributes nothing, and an unclassified component contributes no
/// GST at all and stays visibly unresolved instead of being inferred.
/// </summary>
public class PurchaseGstPolicyTests
{
    private static PurchaseGstCharge NoCharge => new(null, GstClassification.Unknown);

    [Fact]
    public void Taxable_line_gst_is_the_rounded_line_amount_divided_by_eleven()
    {
        var result = PurchaseGstPolicy.Calculate(
            [new PurchaseGstLine(Quantity: 24m, UnitCost: 1.15m, GstClassification.Taxable)],
            NoCharge,
            NoCharge);

        // 24 x 1.15 = 27.60 inclusive; 27.60 / 11 = 2.5090909... -> 2.51
        Assert.Equal(2.51m, result.InputGst);
        Assert.Equal(0, result.UnresolvedComponentCount);
        Assert.Equal(0m, result.UnresolvedAmount);
    }

    /// <summary>
    /// D2: the line amount is rounded to cents <em>before</em> the GST division, away from zero -
    /// 3 x 1.675 is 5.025, which must become 5.03 and not the banker's-rounded 5.02.
    /// </summary>
    [Fact]
    public void Line_amount_rounds_away_from_zero_before_the_gst_division()
    {
        Assert.Equal(5.03m, PurchaseGstPolicy.LineAmount(quantity: 3m, unitCost: 1.675m));
        Assert.Equal(0.46m, PurchaseGstPolicy.ComponentGst(5.03m, GstClassification.Taxable));

        var result = PurchaseGstPolicy.Calculate(
            [new PurchaseGstLine(3m, 1.675m, GstClassification.Taxable)], NoCharge, NoCharge);

        Assert.Equal(0.46m, result.InputGst);
    }

    /// <summary>
    /// The concrete supplier-invoice check recorded in #62: per-component rounding is authoritative
    /// even when it differs from the grand total divided by 11. A $10.05 taxable product line plus a
    /// $10.05 taxable delivery charge is 0.91 + 0.91 = 1.82, while 20.10 / 11 rounds to 1.83.
    /// </summary>
    [Fact]
    public void Invoice_gst_sums_rounded_component_gst_and_can_differ_from_the_total_divided_by_eleven()
    {
        var result = PurchaseGstPolicy.Calculate(
            [new PurchaseGstLine(Quantity: 3m, UnitCost: 3.35m, GstClassification.Taxable)],
            new PurchaseGstCharge(10.05m, GstClassification.Taxable),
            NoCharge);

        Assert.Equal(1.82m, result.InputGst);
        Assert.Equal(1.83m, Math.Round(20.10m / 11m, 2, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void Gst_free_components_contribute_no_gst_and_are_not_unresolved()
    {
        var result = PurchaseGstPolicy.Calculate(
            [new PurchaseGstLine(2m, 5m, GstClassification.GstFree)],
            new PurchaseGstCharge(7.70m, GstClassification.GstFree),
            NoCharge);

        Assert.Equal(0m, result.InputGst);
        Assert.Equal(0, result.UnresolvedComponentCount);
        Assert.Equal(0m, result.UnresolvedAmount);
    }

    [Fact]
    public void Unknown_components_contribute_no_gst_and_are_returned_as_unresolved()
    {
        var result = PurchaseGstPolicy.Calculate(
            [
                new PurchaseGstLine(2m, 5m, GstClassification.Unknown),
                new PurchaseGstLine(4m, 1.10m, GstClassification.Taxable),
            ],
            new PurchaseGstCharge(5.50m, GstClassification.Unknown),
            new PurchaseGstCharge(1.10m, GstClassification.Taxable));

        // Only the two taxable components contribute: 4.40 / 11 = 0.40 and 1.10 / 11 = 0.10.
        Assert.Equal(0.50m, result.InputGst);
        Assert.Equal(2, result.UnresolvedComponentCount);
        Assert.Equal(15.50m, result.UnresolvedAmount);
    }

    /// <summary>D3: a delivery or package charge that is absent or zero is not a component at all.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    public void Absent_or_zero_charges_are_never_unresolved(double? amount)
    {
        var charge = new PurchaseGstCharge(amount is null ? null : (decimal)amount.Value, GstClassification.Unknown);

        var result = PurchaseGstPolicy.Calculate([], charge, charge);

        Assert.Equal(0m, result.InputGst);
        Assert.Equal(0, result.UnresolvedComponentCount);
        Assert.Equal(0m, result.UnresolvedAmount);
    }

    [Fact]
    public void A_purchase_with_no_components_has_no_gst_and_nothing_unresolved()
    {
        var result = PurchaseGstPolicy.Calculate([], NoCharge, NoCharge);

        Assert.Equal(0m, result.InputGst);
        Assert.Equal(0, result.UnresolvedComponentCount);
        Assert.Equal(0m, result.UnresolvedAmount);
    }

    /// <summary>D4: a classification a person supplies is provenance <c>Manual</c>; nothing else is inferred.</summary>
    [Theory]
    [InlineData(GstClassification.Taxable)]
    [InlineData(GstClassification.GstFree)]
    public void A_supplied_classification_is_recorded_as_manual(GstClassification requested)
    {
        var state = PurchaseGstPolicy.Classify(requested);

        Assert.Equal(requested, state.Classification);
        Assert.Equal(GstClassificationSource.Manual, state.Source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(GstClassification.Unknown)]
    public void An_omitted_or_unknown_classification_stays_unknown_with_an_unknown_source(GstClassification? requested)
    {
        var state = PurchaseGstPolicy.Classify(requested);

        Assert.Equal(GstClassification.Unknown, state.Classification);
        Assert.Equal(GstClassificationSource.Unknown, state.Source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    public void An_absent_charge_carries_no_classification(double? amount)
    {
        var state = PurchaseGstPolicy.ClassifyCharge(
            amount is null ? null : (decimal)amount.Value,
            new GstClassificationState(GstClassification.Taxable, GstClassificationSource.Manual));

        Assert.Equal(GstClassificationState.Unclassified, state);
    }

    [Fact]
    public void A_present_charge_keeps_the_classification_it_was_given()
    {
        var requested = new GstClassificationState(GstClassification.Taxable, GstClassificationSource.Manual);

        Assert.Equal(requested, PurchaseGstPolicy.ClassifyCharge(4.95m, requested));
    }

    /// <summary>
    /// An integer outside the vocabulary is not a classification at all. The enum is only a
    /// compile-time constraint - a form field or a deserialized JSON number binds any value - and
    /// an undefined one must never be accepted as a state, because it is neither taxable, nor
    /// GST-free, nor reported as unresolved. Calculating it as zero is the silent inference
    /// AGENTS.md § Purchase GST classification forbids, so the policy refuses it.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(999)]
    [InlineData(-1)]
    public void An_undefined_classification_is_rejected_rather_than_classified(int value)
    {
        var undefined = (GstClassification)value;

        Assert.Throws<ArgumentOutOfRangeException>(() => PurchaseGstPolicy.Classify(undefined));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(999)]
    [InlineData(-1)]
    public void An_undefined_classification_is_never_calculated_as_zero_gst(int value)
    {
        var undefined = (GstClassification)value;

        Assert.Throws<ArgumentOutOfRangeException>(() => PurchaseGstPolicy.ComponentGst(11m, undefined));
        Assert.Throws<ArgumentOutOfRangeException>(() => PurchaseGstPolicy.Calculate(
            [new PurchaseGstLine(1m, 11m, undefined)], NoCharge, NoCharge));
        Assert.Throws<ArgumentOutOfRangeException>(() => PurchaseGstPolicy.Calculate(
            [], new PurchaseGstCharge(11m, undefined), NoCharge));
        Assert.Throws<ArgumentOutOfRangeException>(() => PurchaseGstPolicy.Calculate(
            [], NoCharge, new PurchaseGstCharge(11m, undefined)));
    }

    /// <summary>
    /// The boundary check callers run before they store anything. It answers for the two charges
    /// and every line, and an omitted value is not unsupported: it means the caller did not submit
    /// one, which keeps a stored classification on an edit.
    /// </summary>
    [Fact]
    public void An_unsupported_submission_is_detected_for_either_charge_and_for_any_line()
    {
        var undefined = (GstClassification)999;

        Assert.True(PurchaseGstPolicy.HasUnsupportedClassification(undefined, null, []));
        Assert.True(PurchaseGstPolicy.HasUnsupportedClassification(null, undefined, []));
        Assert.True(PurchaseGstPolicy.HasUnsupportedClassification(
            null, null, [GstClassification.Taxable, undefined]));
        Assert.True(PurchaseGstPolicy.HasUnsupportedClassification(
            null, null, [(GstClassification)(-1)]));
    }

    [Fact]
    public void An_omitted_or_supported_submission_is_accepted()
    {
        Assert.False(PurchaseGstPolicy.HasUnsupportedClassification(null, null, []));
        Assert.False(PurchaseGstPolicy.HasUnsupportedClassification(
            GstClassification.Unknown,
            GstClassification.GstFree,
            [GstClassification.Taxable, GstClassification.Unknown, null]));
    }
}
